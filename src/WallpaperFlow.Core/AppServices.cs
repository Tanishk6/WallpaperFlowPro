using WallpaperFlow.Core.AI;
using WallpaperFlow.Core.Audio;
using WallpaperFlow.Core.Cloud;
using WallpaperFlow.Core.Data;
using WallpaperFlow.Core.Desktop;
using WallpaperFlow.Core.Detection;
using WallpaperFlow.Core.Engine;
using WallpaperFlow.Core.Import;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Input;
using WallpaperFlow.Core.Media;
using WallpaperFlow.Core.Models;
using WallpaperFlow.Core.Performance;
using WallpaperFlow.Core.Platform;
using WallpaperFlow.Core.Rendering;
using WallpaperFlow.Core.Scheduling;
using WallpaperFlow.Core.Themes;
using WallpaperFlow.Core.Updates;

namespace WallpaperFlow.Core;

/// <summary>
/// Composition root. Builds every service, wires the event graph and owns the lifetime. The WPF app only
/// talks to this object, so the UI can be created and destroyed freely while wallpapers keep running.
/// </summary>
public sealed class AppServices : IAsyncDisposable
{
    public SettingsStore Settings { get; }
    public Database Database { get; }
    public LibraryRepository Library { get; }
    public ToolLocator Tools { get; }
    public MediaProbe Probe { get; }
    public ThumbnailService Thumbnails { get; }
    public VideoCacheService VideoCache { get; }
    public AiService Ai { get; }
    public ImportService Import { get; }
    public ThemeService Themes { get; }
    public BackupService Backup { get; }
    public FolderSyncService Sync { get; }
    public CatalogService Catalog { get; }
    public UpdateService Updates { get; }

    public EngineThread Engine { get; } = new();
    public DesktopHost Desktop { get; } = new();
    public SystemEventsHub Hub { get; }
    public WallpaperManager Manager { get; }
    public DetectionService Detection { get; }
    public ProcessWatcher Processes { get; }
    public PowerMonitor Power { get; }
    public MetricsService Metrics { get; }
    public AudioService Audio { get; }
    public InputRouter Input { get; }
    public Scheduler Scheduler { get; }
    public PolicyEngine Policy { get; }
    public TrayIcon Tray { get; }

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public event Action<string, string>? Notify;
    public event Action? StateChanged;

    public bool SafeMode => CrashGuard.SafeMode || Settings.Current.Security.SafeMode;

    public AppServices()
    {
        AppPaths.EnsureCreated();
        Log.Initialize(AppPaths.Logs);
        BackupService.ApplyPendingRestore();

        Settings = new SettingsStore(AppPaths.SettingsFile);
        Database = new Database(AppPaths.Database);
        Library = new LibraryRepository(Database);
        Tools = new ToolLocator(() => Settings.Current.Tools);
        Probe = new MediaProbe(Tools);
        Thumbnails = new ThumbnailService(Tools);
        VideoCache = new VideoCacheService(Tools, () => Settings.Current.Performance);
        Ai = new AiService(Library, Thumbnails, Settings);
        Import = new ImportService(Library, Probe, Thumbnails, Ai, Settings);
        Themes = new ThemeService(Library, Settings);
        Backup = new BackupService(Database, Settings);
        Sync = new FolderSyncService(Library, Settings);
        Catalog = new CatalogService(Settings, Import);
        Updates = new UpdateService(Settings);

        Hub = new SystemEventsHub(Engine);
        Manager = new WallpaperManager(Engine, Desktop, Settings, Library, Tools, VideoCache);
        Detection = new DetectionService(Engine, () => Manager.Monitors, () => Settings.Current.Detection);
        Processes = new ProcessWatcher(() => Settings.Current.Detection);
        Power = new PowerMonitor(Hub);
        Metrics = new MetricsService(() => Manager.ProcessIds, () => Manager.Snapshot.Sum(s => s.Fps));
        Audio = new AudioService(() => Settings.Current.Audio);
        Input = new InputRouter(Engine, () => Settings.Current.Interaction, Manager.RoutePointer, Manager.RouteKey);
        Scheduler = new Scheduler(Settings, Library, Manager);
        Policy = new PolicyEngine(Settings, GatherInputs, Manager.ApplyPolicyAsync);
        Tray = new TrayIcon(Engine, () => new TrayMenuState(
            Settings.Current.General.UserPaused, Settings.Current.General.Muted,
            Settings.Current.Detection.GamingAction != PolicyAction.Continue,
            Manager.Snapshot.FirstOrDefault()?.Title));
    }

    private GovernorInputs GatherInputs()
    {
        var d = Detection.Current;
        var p = Power.Current;
        var m = Metrics.Latest;
        return new GovernorInputs
        {
            MonitorIds = Manager.Monitors.Select(x => x.Id).ToList(),
            Suspended = p.Suspended,
            SessionLocked = p.Locked,
            DisplayOff = !p.DisplayOn,
            UserPaused = Settings.Current.General.UserPaused,
            Fullscreen = d.Fullscreen,
            FullscreenMonitorId = d.FullscreenMonitorId,
            FullscreenProcess = d.FullscreenProcess,
            GameActiveFor = d.GameSince is { } since ? DateTime.UtcNow - since : TimeSpan.Zero,
            Presentation = d.Presentation,
            CoveredMonitors = d.CoveredMonitors,
            DesktopFocused = d.DesktopFocused,
            ActiveRule = Processes.ActiveRule,
            OnBattery = p.OnBattery,
            BatteryPercent = p.Percent,
            EnergySaver = p.EnergySaver,
            GpuLoad = m?.OtherGpu ?? 0,
            CpuLoad = m?.OtherCpu ?? 0,
        };
    }

    public async Task StartAsync()
    {
        CrashGuard.BeginSession();
        ChildProcessJob.KillOrphans(Tools.Mpv);
        ChildProcessJob.KillOrphans(Tools.Ffmpeg);
        Log.Info($"WallpaperFlow Pro {UpdateService.CurrentVersion} starting (safe mode: {SafeMode})");
        await Engine.StartAsync().ConfigureAwait(false);

        await Engine.InvokeAsync(() =>
        {
            Hub.Register();
            Desktop.Initialize();
            Detection.Start();
            if (Settings.Current.General.ShowTrayIcon) Tray.Show(Path.Combine(AppPaths.AppDir, "Assets", "app.ico"), "WallpaperFlow Pro");
        }).ConfigureAwait(false);

        Hub.DisplaysChanged += () => _ = Manager.RefreshMonitorsAsync().ContinueWith(_ => { Detection.ScanNow(); Policy.Invalidate(); });
        Hub.ExplorerRestarted += () => _ = Manager.RecreateAllAsync();
        Detection.Changed += _ => Policy.Invalidate();
        Processes.Changed += _ => Policy.Invalidate();
        Power.Changed += _ => Policy.Invalidate();
        Metrics.Sampled += sample => { Policy.Invalidate(); _ = Manager.RefreshSnapshotAsync(); };
        Manager.SlotsChanged += OnSlotsChanged;
        Manager.WallpaperFaulted += (title, reason) => Notify?.Invoke("Wallpaper stopped", $"“{title}” kept failing and was stopped. {reason}");
        Audio.Spectrum += (l, r, level) => Manager.PostAudio((float[])l.Clone(), (float[])r.Clone(), level);
        Settings.Changed += OnSettingsChanged;
        Library.Changed += () => StateChanged?.Invoke();

        Tray.OpenRequested += () => OpenRequested?.Invoke();
        Tray.ExitRequested += () => ExitRequested?.Invoke();
        Tray.TogglePauseRequested += TogglePause;
        Tray.ToggleMuteRequested += ToggleMute;
        Tray.NextRequested += () => _ = Scheduler.NextAsync();
        Tray.ToggleGamingRequested += () => Settings.Update("detection", s =>
            s.Detection.GamingAction = s.Detection.GamingAction == PolicyAction.Continue ? PolicyAction.Pause : PolicyAction.Continue);

        StartupService.SetEnabled(Settings.Current.General.StartWithWindows);
        if (Settings.Current.Performance.GpuPreference > 0 && Tools.Mpv is { } mpv) Gpu.SetPreference(mpv, Settings.Current.Performance.GpuPreference);

        if (SafeMode)
        {
            Log.Warn("Safe mode: wallpapers are not restored");
            Notify?.Invoke("Safe mode", "WallpaperFlow closed unexpectedly twice, so wallpapers were not restored. Pick one to continue.");
        }
        else if (Settings.Current.General.RestoreLastWallpaper)
        {
            await Manager.RefreshMonitorsAsync().ConfigureAwait(false);
        }
        await Policy.ForceAsync().ConfigureAwait(false);
        _ = BackgroundMaintenanceAsync();
    }

    private void OnSlotsChanged()
    {
        Audio.SetWanted(Manager.NeedsAudio);
        Input.Update(Manager.NeedsInput);
        var playing = Manager.Snapshot.FirstOrDefault();
        Tray.SetTooltip(playing == null ? "WallpaperFlow Pro" : $"WallpaperFlow Pro — {playing.Title}");
        StateChanged?.Invoke();
    }

    private void OnSettingsChanged(string section)
    {
        switch (section)
        {
            case "displays": _ = Manager.ReconcileAsync(); break;
            case "audio": Audio.Restart(); break;
            case "interaction": Input.Update(Manager.NeedsInput); _ = Manager.RefreshOptionsAsync(); break;
            case "general":
                StartupService.SetEnabled(Settings.Current.General.StartWithWindows);
                _ = Manager.RefreshOptionsAsync();
                break;
            case "performance":
                if (Tools.Mpv is { } mpv) Gpu.SetPreference(mpv, Settings.Current.Performance.GpuPreference);
                _ = Manager.RefreshOptionsAsync();
                break;
            case "wallpaper":
            case "security":
            case "*":
                _ = Manager.RefreshOptionsAsync();
                break;
        }
        Policy.Invalidate();
        StateChanged?.Invoke();
    }

    public void TogglePause() => Settings.Update("general", s => s.General.UserPaused = !s.General.UserPaused);
    public void ToggleMute() => Settings.Update("general", s => s.General.Muted = !s.General.Muted);

    /// <summary>Persist edits to a wallpaper (effects, playback, tags) and push them to running renderers.</summary>
    public async Task SaveWallpaperAsync(WallpaperItem w, bool restart = false)
    {
        Library.Upsert(w);
        if (restart) await Manager.RestartWallpaperAsync(w.Id).ConfigureAwait(false);
        else await Manager.RefreshOptionsAsync().ConfigureAwait(false);
    }

    private async Task BackgroundMaintenanceAsync()
    {
        await Task.Delay(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        try
        {
            var cloud = Settings.Current.Cloud;
            if (cloud.AutoBackup && (cloud.LastBackup == null || DateTime.Now - cloud.LastBackup > TimeSpan.FromDays(7))) Backup.CreateBackup();
            if (cloud.SyncEnabled && !string.IsNullOrEmpty(cloud.SyncFolder)) await Sync.SyncAsync().ConfigureAwait(false);
            VideoCache.Enforce();
            UpdateService.PruneInstallers();
            var up = Settings.Current.Updates;
            if (up.AutoCheck && (up.LastCheck == null || DateTime.Now - up.LastCheck > TimeSpan.FromDays(1)))
            {
                var check = await Updates.CheckAsync().ConfigureAwait(false);
                if (check is { IsNewer: true } && check.Manifest.Version != up.SkippedVersion)
                {
                    if (up.AutoInstall && !string.IsNullOrEmpty(check.Manifest.Url))
                    {
                        var file = await Updates.DownloadAsync(check.Manifest).ConfigureAwait(false);
                        Notify?.Invoke("Update ready", $"Version {check.Manifest.Version} will install when you exit.");
                        _pendingInstaller = file;
                    }
                    else Notify?.Invoke("Update available", $"WallpaperFlow Pro {check.Manifest.Version} is available. Open Settings → Updates.");
                }
            }
        }
        catch (Exception ex) { Log.Warn("Maintenance task failed: " + ex.Message); }
    }

    private string? _pendingInstaller;

    public async ValueTask DisposeAsync()
    {
        Log.Info("Shutting down");
        Policy.Dispose();
        Scheduler.Dispose();
        Metrics.Dispose();
        Processes.Dispose();
        Audio.Dispose();
        try { Input.Dispose(); } catch { }
        Detection.Dispose();
        try { await Manager.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { Log.Error("Manager shutdown failed", ex); }
        try { await Engine.InvokeAsync(() => { Tray.Dispose(); Hub.Dispose(); }).ConfigureAwait(false); } catch { }
        Engine.Dispose();
        VideoCache.Dispose();
        Settings.Dispose();
        Database.Dispose();
        CrashGuard.EndSession();
        if (_pendingInstaller != null) { try { UpdateService.RunInstaller(_pendingInstaller); } catch (Exception ex) { Log.Error("Installer launch failed", ex); } }
        await Log.FlushAsync().ConfigureAwait(false);
    }
}
