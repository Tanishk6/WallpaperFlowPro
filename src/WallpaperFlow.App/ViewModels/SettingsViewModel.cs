using System.IO;
using WallpaperFlow.App.Mvvm;
using WallpaperFlow.Core;
using WallpaperFlow.Core.Audio;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;
using WallpaperFlow.Core.Updates;

namespace WallpaperFlow.App.ViewModels;

public sealed class SettingsViewModel : PageViewModel
{
    private readonly IThemeApplier _theme;
    private string _apiKey = "";
    private string _updateStatus = "";
    private UpdateCheck? _check;

    public SettingsViewModel(AppServices s, IUiDispatcher ui, IDialogService d, IThemeApplier theme) : base(s, ui, d)
    {
        _theme = theme;
        Microphones = new[] { new Choice<string?>(null, "Default microphone") }.Concat(AudioService.Microphones().Select(m => new Choice<string?>(m.Id, m.Name))).ToList();
        _apiKey = S.Ai.GetApiKey() is { Length: > 8 } k ? "••••••••" + k[^4..] : "";
        SaveApiKeyCommand = new RelayCommand(() => { if (!_apiKey.StartsWith("••")) { S.Ai.SetApiKey(_apiKey); Say(_apiKey.Length > 0 ? "API key stored (encrypted with Windows DPAPI)." : "API key removed."); } });
        PickSyncFolderCommand = new RelayCommand(() => { var f = Dialogs.PickFolder("Choose a synced folder (OneDrive, Dropbox, NAS…)"); if (f != null) SyncFolder = f; });
        SyncNowCommand = new AsyncRelayCommand(async () => Say(await S.Sync.SyncAsync()), Fail);
        BackupNowCommand = new AsyncRelayCommand(async () => { var p = await Task.Run(() => S.Backup.CreateBackup()); Say("Backup saved: " + Path.GetFileName(p)); OnPropertyChanged(nameof(LastBackup)); }, Fail);
        RestoreCommand = new RelayCommand(Restore);
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync, Fail);
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, Fail, () => _check is { IsNewer: true });
        RollbackCommand = new RelayCommand(Rollback, () => S.Updates.RollbackTarget() != null);
        OpenDataCommand = new RelayCommand(() => Dialogs.OpenFolder(AppPaths.Data));
        OpenLogsCommand = new RelayCommand(() => Dialogs.OpenFolder(AppPaths.Logs));
        PickToolCommand = new RelayCommand(PickTool);
    }

    public override string Title => "Settings";
    public IReadOnlyList<Choice<string?>> Microphones { get; }
    public RelayCommand SaveApiKeyCommand { get; }
    public RelayCommand PickSyncFolderCommand { get; }
    public AsyncRelayCommand SyncNowCommand { get; }
    public AsyncRelayCommand BackupNowCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public AsyncRelayCommand CheckUpdatesCommand { get; }
    public AsyncRelayCommand InstallUpdateCommand { get; }
    public RelayCommand RollbackCommand { get; }
    public RelayCommand OpenDataCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand PickToolCommand { get; }

    // General
    public bool StartWithWindows { get => Cfg.General.StartWithWindows; set => Update("general", x => x.General.StartWithWindows = value); }
    public bool MinimizeToTray { get => Cfg.General.MinimizeToTray; set => Update("general", x => x.General.MinimizeToTray = value); }
    public bool RestoreLastWallpaper { get => Cfg.General.RestoreLastWallpaper; set => Update("general", x => x.General.RestoreLastWallpaper = value); }
    public bool CopyImportedFiles { get => Cfg.Library.CopyImportedFiles; set => Update("library", x => x.Library.CopyImportedFiles = value); }

    // Appearance
    public ThemeMode Mode { get => Cfg.Appearance.Mode; set { Update("appearance", x => x.Appearance.Mode = value); ApplyTheme(); } }
    public BackdropKind Backdrop { get => Cfg.Appearance.Backdrop; set { Update("appearance", x => x.Appearance.Backdrop = value); ApplyTheme(); } }
    public bool HoverPreview { get => Cfg.Appearance.HoverPreview; set { Update("appearance", x => x.Appearance.HoverPreview = value); ApplyTheme(); } }
    public bool ReduceMotion { get => Cfg.Appearance.ReduceMotion; set { Update("appearance", x => x.Appearance.ReduceMotion = value); ApplyTheme(); } }
    public IReadOnlyList<Choice<BackgroundScene>> Scenes { get; } = new[]
    {
        new Choice<BackgroundScene>(BackgroundScene.Aurora, "Aurora — soft animated glow"),
        new Choice<BackgroundScene>(BackgroundScene.Neon, "Neon grid — gaming"),
        new Choice<BackgroundScene>(BackgroundScene.Sakura, "Sakura night — anime"),
        new Choice<BackgroundScene>(BackgroundScene.Wallpaper, "Your wallpaper, blurred"),
        new Choice<BackgroundScene>(BackgroundScene.Glass, "Glass — Windows Mica"),
    };
    public Choice<BackgroundScene> Scene
    {
        get => Scenes.FirstOrDefault(c => c.Value == Cfg.Appearance.Scene) ?? Scenes[0];
        set { if (value == null) return; Update("appearance", x => x.Appearance.Scene = value.Value); ApplyTheme(); }
    }
    private void ApplyTheme() => _theme.Apply(S.Themes.Current, Cfg.Appearance.Mode, Cfg.Appearance.Backdrop);

    // Audio & interaction
    public bool UseMicrophone { get => Cfg.Audio.Source == AudioSource.Microphone; set => Update("audio", x => x.Audio.Source = value ? AudioSource.Microphone : AudioSource.SystemAudio); }
    public Choice<string?> Microphone { get => Microphones.FirstOrDefault(m => m.Value == Cfg.Audio.DeviceId) ?? Microphones[0]; set => Update("audio", x => x.Audio.DeviceId = value.Value); }
    public double AudioGain { get => Cfg.Audio.Gain; set => Update("audio", x => x.Audio.Gain = Math.Clamp(Math.Round(value, 2), 0.1, 4)); }
    public double AudioSmoothing { get => Cfg.Audio.Smoothing; set => Update("audio", x => x.Audio.Smoothing = Math.Clamp(Math.Round(value, 2), 0, 0.95)); }
    public bool MouseEnabled { get => Cfg.Interaction.MouseEnabled; set => Update("interaction", x => x.Interaction.MouseEnabled = value); }
    public bool KeyboardEnabled { get => Cfg.Interaction.KeyboardEnabled; set => Update("interaction", x => x.Interaction.KeyboardEnabled = value); }

    // AI
    public bool AutoTag { get => Cfg.Ai.AutoTagOnImport; set => Update("ai", x => x.Ai.AutoTagOnImport = value); }
    public bool DetectDuplicates { get => Cfg.Ai.DetectDuplicatesOnImport; set => Update("ai", x => x.Ai.DetectDuplicatesOnImport = value); }
    public bool CloudTagging { get => Cfg.Ai.CloudTaggingEnabled; set => Update("ai", x => x.Ai.CloudTaggingEnabled = value); }
    public string CloudModel { get => Cfg.Ai.CloudModel; set => Update("ai", x => x.Ai.CloudModel = value.Trim()); }
    public string ApiKey { get => _apiKey; set => SetProperty(ref _apiKey, value); }

    // Cloud & backup
    public bool SyncEnabled { get => Cfg.Cloud.SyncEnabled; set => Update("cloud", x => x.Cloud.SyncEnabled = value); }
    public string? SyncFolder { get => Cfg.Cloud.SyncFolder; set => Update("cloud", x => x.Cloud.SyncFolder = value); }
    public bool SyncMedia { get => Cfg.Cloud.SyncMedia; set => Update("cloud", x => x.Cloud.SyncMedia = value); }
    public bool AutoBackup { get => Cfg.Cloud.AutoBackup; set => Update("cloud", x => x.Cloud.AutoBackup = value); }
    public string LastBackup => Cfg.Cloud.LastBackup is { } d ? "Last backup " + d.ToString("g") : "No backup yet";
    public string CatalogUrl { get => Cfg.Cloud.CatalogUrl; set => Update("cloud", x => x.Cloud.CatalogUrl = value.Trim()); }

    // Updates
    public bool AutoCheck { get => Cfg.Updates.AutoCheck; set => Update("updates", x => x.Updates.AutoCheck = value); }
    public bool AutoInstall { get => Cfg.Updates.AutoInstall; set => Update("updates", x => x.Updates.AutoInstall = value); }
    public bool BetaChannel { get => Cfg.Updates.Channel == "beta"; set => Update("updates", x => x.Updates.Channel = value ? "beta" : "stable"); }
    public string UpdateStatus { get => _updateStatus; private set => SetProperty(ref _updateStatus, value); }
    public string CurrentVersion => UpdateService.CurrentVersion.ToString(3);
    public string RollbackLabel => S.Updates.RollbackTarget() is { } t ? $"Roll back to {t.Version.ToString(3)}" : "No earlier version kept";

    // Security
    public bool AllowNetwork { get => Cfg.Security.AllowWebWallpaperNetwork; set => Update("security", x => x.Security.AllowWebWallpaperNetwork = value); }
    public long MaxImportMb { get => Cfg.Security.MaxImportFileMb; set => Update("security", x => x.Security.MaxImportFileMb = Math.Clamp(value, 10, 102400)); }

    // Tools
    public string MpvStatus => S.Tools.Mpv ?? "Not found — video wallpapers need mpv";
    public string FfmpegStatus => S.Tools.Ffmpeg ?? "Not found — thumbnails and optimisation need FFmpeg";
    public string FfprobeStatus => S.Tools.Ffprobe ?? "Not found";

    private void PickTool(object? which)
    {
        var f = Dialogs.PickFiles($"Locate {which}.exe", "Programs|*.exe", false);
        if (f is not { Length: 1 }) return;
        S.Settings.Update("tools", x =>
        {
            switch (which as string)
            {
                case "mpv": x.Tools.MpvPath = f[0]; break;
                case "ffmpeg": x.Tools.FfmpegPath = f[0]; break;
                case "ffprobe": x.Tools.FfprobePath = f[0]; break;
            }
        });
        OnPropertiesChanged(nameof(MpvStatus), nameof(FfmpegStatus), nameof(FfprobeStatus));
    }

    private void Restore()
    {
        var files = Dialogs.PickFiles("Restore backup", "WallpaperFlow backup|*.wfbackup", false);
        if (files is not { Length: 1 }) return;
        if (!Dialogs.Confirm("Restore backup", "Your library and settings will be replaced the next time WallpaperFlow starts. Continue?")) return;
        try { S.Backup.StageRestore(files[0]); Say("Restore staged. Exit and reopen WallpaperFlow to finish."); }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task CheckUpdatesAsync()
    {
        UpdateStatus = "Checking…";
        _check = await S.Updates.CheckAsync();
        UpdateStatus = _check == null ? "Update checks aren't set up for this build."
            : _check is { IsNewer: true } c ? $"Version {c.Manifest.Version} is available. {c.Manifest.Notes}" : "You're up to date.";
        InstallUpdateCommand.Refresh();
    }

    private async Task InstallUpdateAsync()
    {
        if (_check is not { IsNewer: true } c) return;
        if (string.IsNullOrEmpty(c.Manifest.Url))
        {
            // Distributed from a download or store page rather than a direct link: open it.
            if (!string.IsNullOrEmpty(c.Manifest.Page)) { Dialogs.OpenUrl(c.Manifest.Page); UpdateStatus = "Opened the download page for version " + c.Manifest.Version + "."; }
            else UpdateStatus = "Version " + c.Manifest.Version + " is available from wherever you got WallpaperFlow Pro.";
            return;
        }
        UpdateStatus = "Downloading and verifying…";
        var file = await S.Updates.DownloadAsync(c.Manifest, new Progress<double>(p => UpdateStatus = $"Downloading… {p:P0}"));
        UpdateStatus = "Verified. Installing — WallpaperFlow will restart.";
        UpdateService.RunInstaller(file);
    }

    private void Rollback()
    {
        if (!Dialogs.Confirm("Roll back", RollbackLabel + "? WallpaperFlow will close and reinstall the earlier version. Your library is kept.")) return;
        try { S.Updates.Rollback(); } catch (Exception ex) { Fail(ex); }
    }
}
