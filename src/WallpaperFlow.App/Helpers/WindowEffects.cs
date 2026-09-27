using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.App.Helpers;

/// <summary>Mica / Acrylic backdrops, dark title bar and rounded corners through DWM (Windows 11 22H2+; solid fallback on Windows 10).</summary>
public static class WindowEffects
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_SYSTEMBACKDROP_TYPE = 38;

    public static bool SupportsBackdrop => Environment.OSVersion.Version.Build >= 22621;

    public static void Apply(Window window, bool dark, BackdropKind backdrop)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int d = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref d, sizeof(int));
        int corner = 2;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        int type = backdrop switch { BackdropKind.Mica => 2, BackdropKind.Acrylic => 3, _ => 1 };
        bool glass = SupportsBackdrop && type != 1;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
            target.BackgroundColor = glass ? Colors.Transparent : (dark ? Color.FromRgb(0x0E, 0x12, 0x20) : Color.FromRgb(0xEE, 0xF1, 0xF7));
        // With a system backdrop the window paints a translucent tint over it; otherwise an opaque surface.
        window.SetResourceReference(Control.BackgroundProperty, glass ? "WindowTintBrush" : "WindowSolidBrush");
    }
}
