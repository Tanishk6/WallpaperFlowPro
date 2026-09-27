using System.Runtime.InteropServices;
using System.Text;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;
using WallpaperFlow.Core.Native;

namespace WallpaperFlow.Core.Desktop;

/// <summary>
/// Finds the layer between the desktop icons and the wallpaper and creates host windows there.
///
/// Two shell layouts exist:
/// <list type="bullet">
/// <item><b>Legacy</b> (Win10, Win11 ≤ 23H2): sending 0x052C to Progman spawns a WorkerW sibling behind the
/// top-level window that holds SHELLDLL_DefView. Hosts become children of that WorkerW.</item>
/// <item><b>Raised desktop</b> (Win11 24H2+, build 26002+): SHELLDLL_DefView and the WorkerW are children of
/// Progman. Hosts are parented to Progman, placed below DefView, and the WorkerW is pushed below the hosts.
/// Child windows need WS_EX_LAYERED there or DWM does not composite them.</item>
/// </list>
/// Everything here must run on the engine thread.
/// </summary>
public sealed class DesktopHost
{
    private const uint SpawnWorkerMessage = 0x052C;
    public nint Progman { get; private set; }
    public nint WorkerW { get; private set; }
    public nint DefView { get; private set; }
    public nint Parent { get; private set; }
    public bool RaisedDesktop { get; private set; }
    public bool IsReady => Parent != 0 && Win32.IsWindow(Parent);

    public bool Initialize()
    {
        Progman = Win32.FindWindow("Progman", null);
        if (Progman == 0)
        {
            Log.Warn("Progman not found (explorer not running?)");
            return false;
        }

        // Ask Explorer to create the WorkerW. The (0xD, 1) variant is what Win11 expects; the (0,0) variant covers Win10.
        Win32.SendMessageTimeout(Progman, SpawnWorkerMessage, 0xD, 0x1, Win32.SMTO_NORMAL, 1000, out _);
        Win32.SendMessageTimeout(Progman, SpawnWorkerMessage, 0, 0, Win32.SMTO_NORMAL, 1000, out _);

        var defUnderProgman = Win32.FindWindowEx(Progman, 0, "SHELLDLL_DefView", null);
        if (defUnderProgman != 0)
        {
            RaisedDesktop = true;
            DefView = defUnderProgman;
            WorkerW = Win32.FindWindowEx(Progman, 0, "WorkerW", null);
            Parent = Progman;
            Log.Info($"Desktop layout: raised (24H2+). Progman={Progman:X} DefView={DefView:X} WorkerW={WorkerW:X}");
            return true;
        }

        RaisedDesktop = false;
        nint iconsHost = 0;
        Win32.EnumWindows((h, _) =>
        {
            var dv = Win32.FindWindowEx(h, 0, "SHELLDLL_DefView", null);
            if (dv != 0) { iconsHost = h; DefView = dv; return false; }
            return true;
        }, 0);

        WorkerW = iconsHost != 0 ? Win32.FindWindowEx(0, iconsHost, "WorkerW", null) : 0;
        Parent = WorkerW != 0 ? WorkerW : Progman;
        Log.Info($"Desktop layout: legacy. IconsHost={iconsHost:X} WorkerW={WorkerW:X} Parent={Parent:X}");
        return true;
    }

    /// <summary>Creates a host window covering <paramref name="screenBounds"/> (physical pixels, virtual-screen coordinates).</summary>
    public HostWindow CreateHost(RectI screenBounds, string debugName)
    {
        if (!IsReady && !Initialize()) throw new InvalidOperationException("Desktop is not available");
        var host = HostWindow.Create(Parent, debugName, RaisedDesktop);
        Place(host, screenBounds);
        return host;
    }

    public void Place(HostWindow host, RectI screenBounds)
    {
        var r = new Win32.RECT { Left = screenBounds.X, Top = screenBounds.Y, Right = screenBounds.Right, Bottom = screenBounds.Bottom };
        Win32.MapWindowPoints(0, Parent, ref r, 2);
        host.ClientBounds = RectI.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        host.ScreenBounds = screenBounds;

        if (RaisedDesktop && DefView != 0)
        {
            // host directly below DefView (icons stay on top) …
            Win32.SetWindowPos(host.Handle, DefView, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
            // … and Explorer's WorkerW (which paints the static wallpaper) below us.
            if (WorkerW != 0)
                Win32.SetWindowPos(WorkerW, host.Handle, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
        else
        {
            Win32.SetWindowPos(host.Handle, Win32.HWND_TOP, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }
    }

    /// <summary>Asks Explorer to repaint the static wallpaper (used when all live wallpapers are removed or on exit).</summary>
    public static void RefreshStaticWallpaper()
    {
        try
        {
            var sb = new StringBuilder(520);
            if (Win32.SystemParametersInfo(Win32.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0))
                Win32.SystemParametersInfo(Win32.SPI_SETDESKWALLPAPER, 0, sb.ToString(), Win32.SPIF_SENDCHANGE);
        }
        catch (Exception ex) { Log.Warn("Could not refresh static wallpaper: " + ex.Message); }
    }

    /// <summary>True when <paramref name="hwnd"/> belongs to the desktop (icons view, WorkerW, Progman or our hosts).</summary>
    public static bool IsDesktopWindow(nint hwnd)
    {
        if (hwnd == 0) return false;
        var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        var cls = Win32.ClassNameOf(root);
        return cls is "Progman" or "WorkerW";
    }
}

/// <summary>Raw Win32 child window a renderer draws into (mpv via --wid, WebView2 via controller).</summary>
public sealed class HostWindow : IDisposable
{
    private const string ClassName = "WallpaperFlow.Host";
    private static Win32.WndProc? _proc;
    private static bool _registered;
    private static nint _blackBrush;

    public nint Handle { get; private set; }
    public RectI ScreenBounds { get; internal set; }
    public RectI ClientBounds { get; internal set; }
    public int Width => ScreenBounds.Width;
    public int Height => ScreenBounds.Height;

    private HostWindow(nint handle) => Handle = handle;

    internal static HostWindow Create(nint parent, string name, bool layered)
    {
        EnsureClass();
        uint ex = Win32.WS_EX_NOACTIVATE | (layered ? Win32.WS_EX_LAYERED : 0);
        var h = Win32.CreateWindowEx(ex, ClassName, name,
            Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPCHILDREN | Win32.WS_CLIPSIBLINGS,
            0, 0, 1, 1, parent, 0, Win32.GetModuleHandle(null), 0);
        if (h == 0) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
        if (layered) Win32.SetLayeredWindowAttributes(h, 0, 255, Win32.LWA_ALPHA);
        return new HostWindow(h);
    }

    private static void EnsureClass()
    {
        if (_registered) return;
        _proc = (hwnd, msg, w, l) =>
        {
            if (msg == Win32.WM_MOUSEACTIVATE) return Win32.MA_NOACTIVATE;
            return Win32.DefWindowProc(hwnd, msg, w, l);
        };
        _blackBrush = Win32.CreateSolidBrush(0x000000);
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            style = Win32.CS_HREDRAW | Win32.CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = Win32.GetModuleHandle(null),
            hbrBackground = _blackBrush,
            hCursor = Win32.LoadCursor(0, 32512 /* IDC_ARROW */),
            lpszClassName = ClassName,
        };
        Win32.RegisterClassEx(ref wc);
        _registered = true;
    }

    public void SetVisible(bool visible) =>
        Win32.ShowWindow(Handle, visible ? Win32.SW_SHOWNA : Win32.SW_HIDE);

    public void Dispose()
    {
        if (Handle != 0 && Win32.IsWindow(Handle)) Win32.DestroyWindow(Handle);
        Handle = 0;
    }
}
