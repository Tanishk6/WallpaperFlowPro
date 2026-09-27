namespace WallpaperFlow.Core.Models;

/// <summary>
/// All persisted settings (settings.json). Every section is a plain mutable class so the
/// settings pages can bind to it; SettingsStore owns persistence and change notification.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 3;
    public GeneralSettings General { get; set; } = new();
    public DisplaySettings Displays { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();
    public DetectionSettings Detection { get; set; } = new();
    public BatterySettings Battery { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public InteractionSettings Interaction { get; set; } = new();
    public SchedulerSettings Scheduler { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public AiSettings Ai { get; set; } = new();
    public CloudSettings Cloud { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();
    public ToolSettings Tools { get; set; } = new();
    public LibrarySettings Library { get; set; } = new();
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; } = true;
    public bool SilentStartup { get; set; } = true;
    public bool RestoreLastWallpaper { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    public bool FirstRunCompleted { get; set; }
    public bool UserPaused { get; set; }
    public bool Muted { get; set; } = true;
}

public sealed class DisplaySettings
{
    public LayoutMode Layout { get; set; } = LayoutMode.SameOnAll;

    /// <summary>Wallpaper for SameOnAll and Span layouts.</summary>
    public string? SharedWallpaperId { get; set; }

    /// <summary>MonitorInfo.Id → wallpaper id, for PerMonitor layout.</summary>
    public Dictionary<string, string> PerMonitor { get; set; } = new();

    /// <summary>Remember assignments for monitors that are unplugged so hot-plug restores them.</summary>
    public bool RememberDisconnected { get; set; } = true;
}

public sealed class PerformanceSettings
{
    /// <summary>FPS cap for web/shader wallpapers and the optimised video cache. 0 = display refresh.</summary>
    public int FpsLimit { get; set; } = 60;
    public QualityPreset Quality { get; set; } = QualityPreset.Auto;
    public bool DynamicFps { get; set; } = true;
    /// <summary>FPS while another app has focus and the desktop is only partially visible.</summary>
    public int BackgroundFps { get; set; } = 30;
    public bool AdaptiveRendering { get; set; } = true;
    public int GpuBusyThreshold { get; set; } = 85;
    public int CpuBusyThreshold { get; set; } = 90;
    /// <summary>Render scale for web/shader wallpapers (0.5..1).</summary>
    public double RenderScale { get; set; } = 1.0;
    public bool SmartResolution { get; set; } = true;
    public HardwareDecoding HardwareDecoding { get; set; } = HardwareDecoding.Auto;
    public bool SmartVideoCache { get; set; }
    public int VideoCacheLimitMb { get; set; } = 4096;
    public bool TrimMemoryWhenHidden { get; set; } = true;
    /// <summary>Windows per-app GPU preference written for the renderer: 0 = let Windows decide, 1 = power saving, 2 = high performance.</summary>
    public int GpuPreference { get; set; }
    /// <summary>Optional DXGI adapter description substring handed to mpv (--d3d11-adapter).</summary>
    public string? D3D11Adapter { get; set; }
    public int ProcessPriorityBelowNormal { get; set; } = 1;
}

public sealed class DetectionSettings
{
    public bool Enabled { get; set; } = true;
    public PolicyAction GamingAction { get; set; } = PolicyAction.Pause;
    public PolicyAction FullscreenVideoAction { get; set; } = PolicyAction.Pause;
    public PolicyAction PresentationAction { get; set; } = PolicyAction.Pause;
    public bool PauseWhenCovered { get; set; } = true;
    public bool PauseOnLock { get; set; } = true;
    /// <summary>After this many seconds in Pause, escalate to Stop and free memory (0 = never).</summary>
    public int EscalateToStopAfterSeconds { get; set; } = 300;
    public List<ProcessRule> Rules { get; set; } = ProcessRule.Defaults();
    /// <summary>Executables never treated as fullscreen games (e.g. your IDE in fullscreen).</summary>
    public List<string> IgnoredProcesses { get; set; } = new() { "explorer.exe", "ShellExperienceHost.exe", "SearchHost.exe", "StartMenuExperienceHost.exe" };
}

public sealed class ProcessRule
{
    public string ProcessName { get; set; } = "";   // e.g. obs64.exe
    public string DisplayName { get; set; } = "";
    public RuleKind Kind { get; set; } = RuleKind.Custom;
    public PolicyAction Action { get; set; } = PolicyAction.Pause;
    public bool Enabled { get; set; } = true;

    public static List<ProcessRule> Defaults() => new()
    {
        new() { ProcessName = "obs64.exe", DisplayName = "OBS Studio", Kind = RuleKind.Streaming },
        new() { ProcessName = "obs32.exe", DisplayName = "OBS Studio (32-bit)", Kind = RuleKind.Streaming },
        new() { ProcessName = "Streamlabs OBS.exe", DisplayName = "Streamlabs Desktop", Kind = RuleKind.Streaming },
        new() { ProcessName = "XSplit.Core.exe", DisplayName = "XSplit Broadcaster", Kind = RuleKind.Streaming },
        new() { ProcessName = "bdcam.exe", DisplayName = "Bandicam", Kind = RuleKind.Recording },
        new() { ProcessName = "CamtasiaRecorder.exe", DisplayName = "Camtasia Recorder", Kind = RuleKind.Recording },
        new() { ProcessName = "ShareX.exe", DisplayName = "ShareX (while recording)", Kind = RuleKind.Recording, Enabled = false },
    };
}

public sealed class BatterySettings
{
    public bool Enabled { get; set; } = true;
    public int ReducedFps { get; set; } = 15;
    public int PauseBelowPercent { get; set; } = 20;
    public bool DisableHeavyEffects { get; set; } = true;
    public bool PauseOnEnergySaver { get; set; } = true;
    public bool PauseWhenUnplugged { get; set; }
}

public sealed class AudioSettings
{
    public AudioSource Source { get; set; } = AudioSource.SystemAudio;
    public string? DeviceId { get; set; }
    public double Gain { get; set; } = 1.0;
    public double Smoothing { get; set; } = 0.6;
    public int Bands { get; set; } = 64;
}

public sealed class InteractionSettings
{
    public bool MouseEnabled { get; set; } = true;
    public bool KeyboardEnabled { get; set; }
    public bool ClickThroughToIcons { get; set; } = true;
}

public sealed class SchedulerSettings
{
    public bool Enabled { get; set; }
    public ScheduleInterval Interval { get; set; } = ScheduleInterval.Daily;
    public int CustomMinutes { get; set; } = 90;
    public ScheduleOrder Order { get; set; } = ScheduleOrder.Random;
    public ScheduleSource Source { get; set; } = ScheduleSource.Favorites;
    public long? SourceId { get; set; }
    public TimeSpan DailyAt { get; set; } = new(8, 0, 0);
    public DayOfWeek WeeklyOn { get; set; } = DayOfWeek.Monday;
    public bool ApplyPerMonitor { get; set; }
    public DateTime? LastRotation { get; set; }
    public string? LastWallpaperId { get; set; }
    /// <summary>Time-of-day rules: at HH:mm switch to a specific wallpaper (optional, evaluated before rotation).</summary>
    public List<TimeRule> TimeRules { get; set; } = new();
}

public sealed class TimeRule
{
    public TimeSpan At { get; set; }
    public string WallpaperId { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class AppearanceSettings
{
    public ThemeMode Mode { get; set; } = ThemeMode.System;
    public BackdropKind Backdrop { get; set; } = BackdropKind.Mica;
    public string ThemeId { get; set; } = "builtin.periwinkle";
    public bool ReduceMotion { get; set; }
    public BackgroundScene Scene { get; set; } = BackgroundScene.Aurora;
    public bool HoverPreview { get; set; } = true;
    public double CardSize { get; set; } = 220;
}

public sealed class AiSettings
{
    public bool AutoTagOnImport { get; set; } = true;
    public bool DetectDuplicatesOnImport { get; set; } = true;
    public bool CloudTaggingEnabled { get; set; }
    public string CloudModel { get; set; } = "claude-haiku-4-5-20251001";
    /// <summary>API key, DPAPI-protected (base64). Never stored in plain text.</summary>
    public string? ProtectedApiKey { get; set; }
    public int DuplicateThreshold { get; set; } = 6;
}

public sealed class CloudSettings
{
    public string CatalogUrl { get; set; } = "";
    public bool SyncEnabled { get; set; }
    public string? SyncFolder { get; set; }
    public bool SyncMedia { get; set; }
    public DateTime? LastSync { get; set; }
    public bool AutoBackup { get; set; } = true;
    public int BackupKeep { get; set; } = 5;
    public DateTime? LastBackup { get; set; }
}

public sealed class UpdateSettings
{
    public bool AutoCheck { get; set; } = true;
    public bool AutoInstall { get; set; }
    public string Channel { get; set; } = "stable";
    /// <summary>Empty = update.json in the GitHub repository from <see cref="AppInfo.GitHubRepo"/>.</summary>
    public string ManifestUrl { get; set; } = "";
    public string ExpectedPublisher { get; set; } = "WallpaperFlow";
    public bool RequireSignature { get; set; } = true;
    public DateTime? LastCheck { get; set; }
    public string? SkippedVersion { get; set; }
}

public sealed class SecuritySettings
{
    /// <summary>Global default; individual HTML wallpapers can be granted network access.</summary>
    public bool AllowWebWallpaperNetwork { get; set; }
    public List<string> NetworkAllowedWallpapers { get; set; } = new();
    public long MaxImportFileMb { get; set; } = 8192;
    public long MaxPackageMb { get; set; } = 2048;
    public bool SafeMode { get; set; }
}

public sealed class ToolSettings
{
    public string? MpvPath { get; set; }
    public string? FfmpegPath { get; set; }
    public string? FfprobePath { get; set; }
}

public sealed class LibrarySettings
{
    public SortField Sort { get; set; } = SortField.DateAdded;
    public bool SortDescending { get; set; } = true;
    public bool CopyImportedFiles { get; set; } = true;
    public List<string> RecentSearches { get; set; } = new();
    public List<string> WatchFolders { get; set; } = new();
}
