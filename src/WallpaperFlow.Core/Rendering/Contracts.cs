using WallpaperFlow.Core.Desktop;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Rendering;

/// <summary>Everything a renderer needs to know besides the wallpaper itself. Produced by the manager from settings + policy.</summary>
public sealed record RenderOptions
{
    public ScalingMode Scaling { get; init; } = ScalingMode.Fill;
    public int Volume { get; init; }
    public bool Muted { get; init; } = true;
    public double Speed { get; init; } = 1.0;
    public QualityPreset Quality { get; init; } = QualityPreset.Balanced;
    public HardwareDecoding HardwareDecoding { get; init; } = HardwareDecoding.Auto;
    public string? D3D11Adapter { get; init; }
    /// <summary>0 = uncapped (display refresh).</summary>
    public int FpsCap { get; init; }
    public double RenderScale { get; init; } = 1.0;
    public EffectsProfile Effects { get; init; } = new();
    public bool HeavyEffectsAllowed { get; init; } = true;
    public bool AllowNetwork { get; init; }
    public string? PropertiesJson { get; init; }
    public double SlideDurationSeconds { get; init; } = 10;
    public bool SlideShuffle { get; init; }
    public bool InputEnabled { get; init; } = true;
    public double MonitorScale { get; init; } = 1.0;
    public double RefreshRate { get; init; } = 60;
    /// <summary>Path of a pre-transcoded variant from the smart video cache (null = original).</summary>
    public string? OptimizedSource { get; init; }
    public bool SmartCache { get; init; } = true;
}

public readonly record struct PointerInput(string Kind, int X, int Y, int Button, int Delta);
public readonly record struct KeyInput(string Kind, int VirtualKey, string Key);

public interface IWallpaperRenderer : IAsyncDisposable
{
    WallpaperItem Wallpaper { get; }
    HostWindow Host { get; }
    PlaybackState State { get; }
    RenderOptions Options { get; }
    double CurrentFps { get; }
    bool WantsAudio { get; }
    bool WantsInput { get; }

    /// <summary>Processes whose CPU/RAM belong to this renderer (mpv child, WebView2 browser).</summary>
    IReadOnlyList<int> ProcessIds { get; }

    event Action<IWallpaperRenderer, string>? Faulted;

    Task StartAsync(CancellationToken ct);
    /// <summary>Soft pause keeps everything resident for instant resume; deep pause also releases memory.</summary>
    Task PauseAsync(bool deep);
    Task ResumeAsync();
    Task ApplyOptionsAsync(RenderOptions options);
    void Resize();
    void PostAudio(float[] left, float[] right, float level);
    void PostPointer(PointerInput input);
    void PostKey(KeyInput input);
}
