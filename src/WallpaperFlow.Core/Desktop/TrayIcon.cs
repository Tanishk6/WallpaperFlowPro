using System.Runtime.InteropServices;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Native;

namespace WallpaperFlow.Core.Desktop;

public sealed record TrayMenuState(bool Paused, bool Muted, bool GamingModePause, string? NowPlaying);

/// <summary>
/// Notification-area icon with a native popup menu. Lives on the engine thread, so it keeps working
/// after the WPF window has been destroyed to save memory.
/// </summary>
public sealed class TrayIcon : IMessageHook, IDisposable
{
    private const uint CallbackMessage = Win32.WM_APP + 20;
    private const int CmdOpen = 1, CmdPause = 2, CmdNext = 3, CmdMute = 4, CmdGaming = 5, CmdExit = 9;

    private readonly EngineThread _engine;
    private readonly Func<TrayMenuState> _state;
    private nint _icon;
    private bool _ownsIcon;
    private bool _added;

    public event Action? OpenRequested;
    public event Action? TogglePauseRequested;
    public event Action? NextRequested;
    public event Action? ToggleMuteRequested;
    public event Action? ToggleGamingRequested;
    public event Action? ExitRequested;

    public TrayIcon(EngineThread engine, Func<TrayMenuState> state)
    {
        _engine = engine;
        _state = state;
    }

    /// <summary>Engine thread only.</summary>
    public void Show(string iconPath, string tooltip)
    {
        _engine.AddHook(this);
        if (File.Exists(iconPath))
            _icon = Win32.LoadImage(0, iconPath, Win32.IMAGE_ICON, Win32.GetSystemMetrics(Win32.SM_CXSMICON), Win32.GetSystemMetrics(Win32.SM_CYSMICON), Win32.LR_LOADFROMFILE);
        _ownsIcon = _icon != 0;
        if (_icon == 0) _icon = Win32.LoadIcon(0, 32512 /* IDI_APPLICATION */);
        _tooltip = tooltip;
        Add();
    }

    private string _tooltip = "WallpaperFlow Pro";

    private Win32.NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
        hWnd = _engine.MessageWindow,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
        szInfo = "", szInfoTitle = "",
    };

    private void Add()
    {
        var d = Data(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP);
        _added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref d);
        if (!_added) Log.Warn("Shell_NotifyIcon(NIM_ADD) failed");
    }

    public void SetTooltip(string text)
    {
        _engine.Post(() =>
        {
            _tooltip = text;
            if (!_added) return;
            var d = Data(Win32.NIF_TIP);
            Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref d);
        });
    }

    public void ShowBalloon(string title, string text)
    {
        _engine.Post(() =>
        {
            if (!_added) return;
            var d = Data(Win32.NIF_INFO);
            d.szInfoTitle = title.Length > 63 ? title[..63] : title;
            d.szInfo = text.Length > 255 ? text[..255] : text;
            d.dwInfoFlags = 0x1; // NIIF_INFO
            Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref d);
        });
    }

    public bool HandleMessage(nint hwnd, uint msg, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (msg == _engine.TaskbarCreatedMessage && msg != 0)
        {
            Add(); // explorer restarted: icon is gone
            return false;
        }
        if (msg != CallbackMessage) return false;

        switch ((uint)(lParam & 0xFFFF))
        {
            case Win32.WM_LBUTTONUP:
            case Win32.WM_LBUTTONDBLCLK:
                OpenRequested?.Invoke();
                break;
            case Win32.WM_RBUTTONUP:
            case Win32.WM_CONTEXTMENU:
                ShowMenu();
                break;
        }
        return true;
    }

    private void ShowMenu()
    {
        var s = _state();
        var menu = Win32.CreatePopupMenu();
        try
        {
            if (!string.IsNullOrEmpty(s.NowPlaying))
            {
                Win32.AppendMenu(menu, Win32.MF_STRING | Win32.MF_GRAYED, 0, Trim("Now: " + s.NowPlaying));
                Win32.AppendMenu(menu, Win32.MF_SEPARATOR, 0, null);
            }
            Win32.AppendMenu(menu, Win32.MF_STRING, CmdOpen, "Open WallpaperFlow");
            Win32.AppendMenu(menu, Win32.MF_STRING, CmdPause, s.Paused ? "Resume wallpapers" : "Pause wallpapers");
            Win32.AppendMenu(menu, Win32.MF_STRING, CmdNext, "Next wallpaper");
            Win32.AppendMenu(menu, Win32.MF_STRING | (s.Muted ? Win32.MF_CHECKED : 0), CmdMute, "Mute");
            Win32.AppendMenu(menu, Win32.MF_STRING | (s.GamingModePause ? Win32.MF_CHECKED : 0), CmdGaming, "Pause while gaming");
            Win32.AppendMenu(menu, Win32.MF_SEPARATOR, 0, null);
            Win32.AppendMenu(menu, Win32.MF_STRING, CmdExit, "Exit");

            Win32.GetCursorPos(out var pt);
            Win32.SetForegroundWindow(_engine.MessageWindow); // required so the menu closes on outside click
            var cmd = Win32.TrackPopupMenuEx(menu, Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD | Win32.TPM_BOTTOMALIGN | Win32.TPM_NONOTIFY,
                pt.X, pt.Y, _engine.MessageWindow, 0);
            Win32.PostMessage(_engine.MessageWindow, Win32.WM_NULL, 0, 0);
            switch (cmd)
            {
                case CmdOpen: OpenRequested?.Invoke(); break;
                case CmdPause: TogglePauseRequested?.Invoke(); break;
                case CmdNext: NextRequested?.Invoke(); break;
                case CmdMute: ToggleMuteRequested?.Invoke(); break;
                case CmdGaming: ToggleGamingRequested?.Invoke(); break;
                case CmdExit: ExitRequested?.Invoke(); break;
            }
        }
        finally { Win32.DestroyMenu(menu); }
    }

    private static string Trim(string s) => s.Length > 48 ? s[..47] + "…" : s;

    public void Dispose()
    {
        if (_added)
        {
            var d = Data(0);
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref d);
            _added = false;
        }
        _engine.RemoveHook(this);
        if (_icon != 0 && _ownsIcon) Win32.DestroyIcon(_icon);
        _icon = 0;
    }
}
