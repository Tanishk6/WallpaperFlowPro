using System.Diagnostics;
using WallpaperFlow.Core.Desktop;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;
using WallpaperFlow.Core.Native;

namespace WallpaperFlow.Core.Detection;

public sealed record DetectionSnapshot
{
    public FullscreenKind Fullscreen { get; init; }
    public string? FullscreenMonitorId { get; init; }
    public string? FullscreenProcess { get; init; }
    public DateTime? GameSince { get; init; }
    public bool Presentation { get; init; }
    public IReadOnlySet<string> CoveredMonitors { get; init; } = new HashSet<string>();
    public bool DesktopFocused { get; init; } = true;
    public string ForegroundProcess { get; init; } = "";

    public bool SameAs(DetectionSnapshot o) =>
        Fullscreen == o.Fullscreen && FullscreenMonitorId == o.FullscreenMonitorId && Presentation == o.Presentation &&
        DesktopFocused == o.DesktopFocused && CoveredMonitors.SetEquals(o.CoveredMonitors) && FullscreenProcess == o.FullscreenProcess;
}

/// <summary>
/// Watches the foreground window and window layout with WinEvent hooks (event-driven, no polling in the
/// common case) plus a 3 s safety scan. Classifies fullscreen apps as games, video, presentations or other,
/// and finds monitors completely hidden behind maximised windows.
/// </summary>
public sealed class DetectionService : IDisposable
{
    private static readonly HashSet<string> VideoApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "vlc.exe", "mpc-hc64.exe", "mpc-hc.exe", "mpc-be64.exe", "mpv.exe", "PotPlayerMini64.exe", "PotPlayerMini.exe",
        "Video.UI.exe", "Microsoft.Media.Player.exe", "wmplayer.exe", "kmplayer.exe", "smplayer.exe", "Netflix.exe",
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "Plex.exe", "Kodi.exe", "Stremio.exe",
    };
    private static readonly HashSet<string> PresentationApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "POWERPNT.EXE", "Keynote.exe", "soffice.bin", "Prezi.exe",
    };
    private static readonly string[] GameEngineClasses =
    {
        "UnityWndClass", "UnrealWindow", "SDL_app", "GLFW30", "CryENGINE", "Valve001", "LaunchUnrealUWindowsClient", "SFMLWindow", "GameMaker",
    };
    private static readonly string[] GameLibraryPaths =
    {
        @"\steamapps\common\", @"\Epic Games\", @"\GOG Galaxy\Games\", @"\XboxGames\", @"\Riot Games\", @"\Ubisoft Game Launcher\games\",
        @"\EA Games\", @"\Battle.net\", @"\Blizzard\", @"\itch\apps\",
    };
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "ForegroundStaging", "MultitaskingViewFrame",
        "TaskListThumbnailWnd", "WallpaperFlow.Engine", "WallpaperFlow.Host",
    };

    private readonly EngineThread _engine;
    private readonly Func<IReadOnlyList<MonitorInfo>> _monitors;
    private readonly Func<DetectionSettings> _settings;
    private readonly List<nint> _hooks = new();
    private Win32.WinEventProc? _proc;
    private Timer? _debounce, _safety;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly Dictionary<uint, string> _pathCache = new();

    public DetectionSnapshot Current { get; private set; } = new();
    public event Action<DetectionSnapshot>? Changed;

    public DetectionService(EngineThread engine, Func<IReadOnlyList<MonitorInfo>> monitors, Func<DetectionSettings> settings)
    {
        _engine = engine;
        _monitors = monitors;
        _settings = settings;
    }

    /// <summary>Engine thread only.</summary>
    public void Start()
    {
        _proc = OnWinEvent;
        void Hook(uint min, uint max) => _hooks.Add(Win32.SetWinEventHook(min, max, 0, _proc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS));
        Hook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND);
        Hook(Win32.EVENT_SYSTEM_MOVESIZEEND, Win32.EVENT_SYSTEM_MOVESIZEEND);
        Hook(Win32.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_SYSTEM_MINIMIZEEND);
        Hook(Win32.EVENT_OBJECT_DESTROY, Win32.EVENT_OBJECT_HIDE);
        Hook(Win32.EVENT_OBJECT_LOCATIONCHANGE, Win32.EVENT_OBJECT_LOCATIONCHANGE);
        Hook(Win32.EVENT_OBJECT_CLOAKED, Win32.EVENT_OBJECT_UNCLOAKED);
        _debounce = new Timer(_ => _engine.Post(Scan), null, Timeout.Infinite, Timeout.Infinite);
        _safety = new Timer(_ => _engine.Post(Scan), null, 1000, 3000);
    }

    private void OnWinEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Win32.OBJID_WINDOW || idChild != 0) return;
        // LOCATIONCHANGE fires for every caret/cursor move; only care about top-level windows.
        if (evt == Win32.EVENT_OBJECT_LOCATIONCHANGE || evt is >= Win32.EVENT_OBJECT_DESTROY and <= Win32.EVENT_OBJECT_HIDE)
            if (hwnd == 0 || Win32.GetAncestor(hwnd, Win32.GA_ROOT) != hwnd) return;
        _debounce?.Change(evt == Win32.EVENT_SYSTEM_FOREGROUND ? 120 : 250, Timeout.Infinite);
    }

    public void ScanNow() => _engine.Post(Scan);

    private void Scan()
    {
        try
        {
            var monitors = _monitors();
            var cfg = _settings();
            Win32.SHQueryUserNotificationState(out int quns);

            var fg = Win32.GetForegroundWindow();
            Win32.GetWindowThreadProcessId(fg, out var fgPid);
            var fgClass = fg != 0 ? Win32.ClassNameOf(fg) : "";
            var fgPath = fgPid != 0 ? PathOf(fgPid) : null;
            var fgExe = fgPath != null ? Path.GetFileName(fgPath) : "";
            bool desktopFocused = fg == 0 || fgPid == _ownPid || ShellClasses.Contains(fgClass) || DesktopHost.IsDesktopWindow(fg);

            var kind = FullscreenKind.None;
            string? fsMonitor = null;
            if (!desktopFocused && fg != 0 && Win32.IsWindowVisible(fg) && !Win32.IsIconic(fg) && !Win32.IsCloaked(fg)
                && !cfg.IgnoredProcesses.Contains(fgExe, StringComparer.OrdinalIgnoreCase))
            {
                var r = Win32.VisibleBounds(fg);
                var m = monitors.FirstOrDefault(mon => r.Covers(mon.Bounds, 0.995));
                if (m != null)
                {
                    fsMonitor = m.Id;
                    kind = Classify(quns, fgExe, fgClass, fgPath);
                }
            }
            if (kind == FullscreenKind.None && quns == Win32.QUNS_RUNNING_D3D_FULL_SCREEN && !desktopFocused)
            {
                // Exclusive fullscreen reported by the shell even though we could not match a window rect.
                kind = FullscreenKind.Game;
                fsMonitor = monitors.FirstOrDefault(mm => mm.Handle == Win32.MonitorFromWindow(fg, 2))?.Id;
            }

            var covered = new HashSet<string>();
            if (cfg.PauseWhenCovered) FindCovered(monitors, covered);
            if (fsMonitor != null) covered.Add(fsMonitor);

            var prev = Current;
            var snap = new DetectionSnapshot
            {
                Fullscreen = kind,
                FullscreenMonitorId = fsMonitor,
                FullscreenProcess = kind == FullscreenKind.None ? null : fgExe,
                GameSince = kind == FullscreenKind.Game ? prev.GameSince ?? DateTime.UtcNow : null,
                Presentation = quns == Win32.QUNS_PRESENTATION_MODE,
                CoveredMonitors = covered,
                DesktopFocused = desktopFocused,
                ForegroundProcess = fgExe,
            };
            Current = snap;
            if (!snap.SameAs(prev)) Changed?.Invoke(snap);
        }
        catch (Exception ex) { Log.Warn("Detection scan failed: " + ex.Message); }
    }

    internal static FullscreenKind Classify(int quns, string exe, string cls, string? path)
    {
        if (PresentationApps.Contains(exe) || cls == "screenClass") return FullscreenKind.Presentation;
        if (quns == Win32.QUNS_RUNNING_D3D_FULL_SCREEN) return FullscreenKind.Game;
        if (GameEngineClasses.Any(c => cls.StartsWith(c, StringComparison.Ordinal))) return FullscreenKind.Game;
        if (path != null && GameLibraryPaths.Any(p => path.Contains(p, StringComparison.OrdinalIgnoreCase))) return FullscreenKind.Game;
        if (VideoApps.Contains(exe)) return FullscreenKind.Video;
        return FullscreenKind.Other;
    }

    private void FindCovered(IReadOnlyList<MonitorInfo> monitors, HashSet<string> covered)
    {
        var remaining = monitors.ToList();
        Win32.EnumWindows((h, _) =>
        {
            if (remaining.Count == 0) return false;
            var cls = Win32.ClassNameOf(h);
            if (cls is "Progman" or "WorkerW") return false; // everything below is the desktop itself
            if (!Win32.IsWindowVisible(h) || Win32.IsIconic(h) || ShellClasses.Contains(cls) || Win32.IsCloaked(h)) return true;
            var ex = (uint)(long)Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE);
            // Overlays (game bars, Discord/Steam overlays, OSDs) are transparent or tool windows.
            if ((ex & Win32.WS_EX_TRANSPARENT) != 0 || (ex & Win32.WS_EX_TOOLWINDOW) != 0 || (ex & Win32.WS_EX_NOACTIVATE) != 0) return true;
            Win32.GetWindowThreadProcessId(h, out var pid);
            if (pid == _ownPid) return true;
            var r = Win32.VisibleBounds(h);
            for (int i = remaining.Count - 1; i >= 0; i--)
            {
                if (r.Covers(remaining[i].WorkArea, 0.98))
                {
                    covered.Add(remaining[i].Id);
                    remaining.RemoveAt(i);
                }
            }
            return true;
        }, 0);
    }

    private string? PathOf(uint pid)
    {
        if (_pathCache.TryGetValue(pid, out var p)) return p;
        if (_pathCache.Count > 256) _pathCache.Clear();
        p = Win32.ProcessPath(pid);
        _pathCache[pid] = p ?? "";
        return p;
    }

    public void Dispose()
    {
        _debounce?.Dispose();
        _safety?.Dispose();
        var hooks = _hooks.ToList();
        _hooks.Clear();
        _engine.Post(() => { foreach (var h in hooks) Win32.UnhookWinEvent(h); });
    }
}
