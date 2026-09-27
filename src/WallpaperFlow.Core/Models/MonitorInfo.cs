namespace WallpaperFlow.Core.Models;

/// <summary>A physical display as reported by EnumDisplayMonitors / EnumDisplayDevices.</summary>
public sealed record MonitorInfo
{
    /// <summary>Stable-ish identifier (device interface path), used as the key in settings.</summary>
    public string Id { get; init; } = "";
    public string DeviceName { get; init; } = "";     // \\.\DISPLAY1
    public string FriendlyName { get; init; } = "";
    public RectI Bounds { get; init; }
    public RectI WorkArea { get; init; }
    public bool IsPrimary { get; init; }
    public uint DpiX { get; init; } = 96;
    public double RefreshRate { get; init; } = 60;
    public nint Handle { get; init; }
    public int Index { get; init; }

    public double Scale => DpiX / 96.0;
    public string Label => string.IsNullOrWhiteSpace(FriendlyName) ? $"Display {Index + 1}" : FriendlyName;
}
