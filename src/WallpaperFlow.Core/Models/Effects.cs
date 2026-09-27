namespace WallpaperFlow.Core.Models;

/// <summary>One enabled effect with its tunable parameters.</summary>
public sealed class EffectSetting
{
    public EffectKind Kind { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>0..1 master intensity.</summary>
    public double Intensity { get; set; } = 0.5;

    /// <summary>Effect-specific parameters (e.g. "speed", "density", "saturation"). Values 0..2 typically.</summary>
    public Dictionary<string, double> Parameters { get; set; } = new();

    public EffectSetting Clone() => new() { Kind = Kind, Enabled = Enabled, Intensity = Intensity, Parameters = new(Parameters) };

    public double Param(string name, double fallback) => Parameters.TryGetValue(name, out var v) ? v : fallback;
}

public sealed class EffectsProfile
{
    public List<EffectSetting> Items { get; set; } = new();

    /// <summary>Audio-reactive style applied on top of effects: pulse, glow, burst, waves.</summary>
    public AudioReactiveStyle AudioStyle { get; set; } = new();

    public bool Any => Items.Any(i => i.Enabled && i.Intensity > 0);

    public EffectSetting? Get(EffectKind kind) => Items.FirstOrDefault(i => i.Kind == kind);

    public EffectsProfile Clone() => new() { Items = Items.Select(i => i.Clone()).ToList(), AudioStyle = AudioStyle with { } };

    public static bool IsHeavy(EffectKind k) => k is EffectKind.Bloom or EffectKind.MotionBlur or EffectKind.Blur;

    /// <summary>Returns a copy without heavy effects (battery saver / adaptive rendering).</summary>
    public EffectsProfile WithoutHeavy() => new()
    {
        Items = Items.Where(i => !IsHeavy(i.Kind)).Select(i => i.Clone()).ToList(),
        AudioStyle = AudioStyle with { }
    };
}

public sealed record AudioReactiveStyle
{
    public bool Pulse { get; init; }
    public bool Glow { get; init; }
    public bool ParticleBurst { get; init; }
    public bool Waves { get; init; }
    public double Sensitivity { get; init; } = 1.0;
    public bool AnyEnabled => Pulse || Glow || ParticleBurst || Waves;
}
