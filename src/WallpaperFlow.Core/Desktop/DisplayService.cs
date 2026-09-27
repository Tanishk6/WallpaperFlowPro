using System.Runtime.InteropServices;
using WallpaperFlow.Core.Models;
using WallpaperFlow.Core.Native;

namespace WallpaperFlow.Core.Desktop;

/// <summary>Enumerates monitors with stable ids, DPI and refresh rate.</summary>
public static class DisplayService
{
    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var handles = new List<nint>();
        Win32.EnumDisplayMonitors(0, 0, (nint h, nint _, ref Win32.RECT _, nint _) => { handles.Add(h); return true; }, 0);

        var result = new List<MonitorInfo>();
        foreach (var h in handles)
        {
            var mi = new Win32.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (!Win32.GetMonitorInfo(h, ref mi)) continue;

            uint dpi = 96;
            try { if (Win32.GetDpiForMonitor(h, 0, out var dx, out _) == 0) dpi = dx; } catch { }

            var dm = new Win32.DEVMODE { dmSize = (ushort)Marshal.SizeOf<Win32.DEVMODE>() };
            double hz = Win32.EnumDisplaySettings(mi.szDevice, Win32.ENUM_CURRENT_SETTINGS, ref dm) && dm.dmDisplayFrequency > 1
                ? dm.dmDisplayFrequency : 60;

            // Monitor device (child of the adapter) gives the interface path — stable across reboots and ports.
            var dd = new Win32.DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<Win32.DISPLAY_DEVICE>() };
            string id = mi.szDevice, friendly = "";
            if (Win32.EnumDisplayDevices(mi.szDevice, 0, ref dd, 1 /* EDD_GET_DEVICE_INTERFACE_NAME */))
            {
                if (!string.IsNullOrEmpty(dd.DeviceID)) id = dd.DeviceID;
                friendly = dd.DeviceString;
            }

            result.Add(new MonitorInfo
            {
                Id = id,
                DeviceName = mi.szDevice,
                FriendlyName = friendly,
                Bounds = mi.rcMonitor.ToRectI(),
                WorkArea = mi.rcWork.ToRectI(),
                IsPrimary = (mi.dwFlags & 1) != 0,
                DpiX = dpi,
                RefreshRate = hz,
                Handle = h,
            });
        }

        // Order left-to-right, top-to-bottom so "Display 1" matches what people expect.
        return result.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y)
            .Select((m, i) => m with { Index = i, FriendlyName = NiceName(m, i) }).ToList();
    }

    private static string NiceName(MonitorInfo m, int i)
    {
        var generic = string.IsNullOrWhiteSpace(m.FriendlyName) || m.FriendlyName.Contains("Generic", StringComparison.OrdinalIgnoreCase);
        var name = generic ? $"Display {i + 1}" : m.FriendlyName;
        return m.IsPrimary ? name + " (main)" : name;
    }

    public static RectI VirtualBounds(IEnumerable<MonitorInfo> monitors) =>
        monitors.Aggregate(default(RectI), (acc, m) => acc.Union(m.Bounds));

    public static MonitorInfo? FromHandle(IEnumerable<MonitorInfo> monitors, nint hMonitor) => monitors.FirstOrDefault(m => m.Handle == hMonitor);

    /// <summary>A fingerprint so hot-plug handlers can ignore no-op display notifications.</summary>
    public static string Signature(IEnumerable<MonitorInfo> monitors) =>
        string.Join("|", monitors.Select(m => $"{m.Id}@{m.Bounds}@{m.DpiX}@{m.RefreshRate}"));
}
