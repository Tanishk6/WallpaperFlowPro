using System.Globalization;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Rendering;

/// <summary>Result of turning an <see cref="EffectsProfile"/> into mpv configuration.</summary>
public sealed record MpvEffectPlan(IReadOnlyList<string> Shaders, IReadOnlyDictionary<string, string> ShaderOpts, string? VideoFilter)
{
    public bool NeedsCopyBack => VideoFilter != null;
    public static readonly MpvEffectPlan Empty = new(Array.Empty<string>(), new Dictionary<string, string>(), null);
}

/// <summary>
/// Maps effects to libplacebo user shaders (runtime/effects/mpv/*.glsl). Each shader exposes
/// prefixed //!PARAM values so options never collide between shaders. Order matters: colour
/// grading first, then spatial filters (blur, bloom, glow), then overlays (weather, fire, particles).
/// </summary>
public static class EffectsCompiler
{
    private static readonly (EffectKind Kind, string File)[] Order =
    {
        (EffectKind.ColorGrade, "colorgrade.glsl"),
        (EffectKind.Blur, "blur.glsl"),
        (EffectKind.Bloom, "bloom.glsl"),
        (EffectKind.Glow, "glow.glsl"),
        (EffectKind.Fog, "fog.glsl"),
        (EffectKind.Rain, "rain.glsl"),
        (EffectKind.Snow, "snow.glsl"),
        (EffectKind.Particles, "particles.glsl"),
        (EffectKind.Fire, "fire.glsl"),
        (EffectKind.Lightning, "lightning.glsl"),
    };

    public static MpvEffectPlan ForMpv(EffectsProfile profile, bool heavyAllowed, string? effectsDir = null)
    {
        effectsDir ??= Path.Combine(AppPaths.Runtime, "effects", "mpv");
        var shaders = new List<string>();
        var opts = new Dictionary<string, string>();
        string? vf = null;

        foreach (var (kind, file) in Order)
        {
            var e = profile.Get(kind);
            if (e is not { Enabled: true } || e.Intensity <= 0) continue;
            if (!heavyAllowed && EffectsProfile.IsHeavy(kind)) continue;
            var path = Path.Combine(effectsDir, file);
            shaders.Add(path);
            foreach (var (k, v) in Params(e)) opts[k] = v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        var mb = profile.Get(EffectKind.MotionBlur);
        if (heavyAllowed && mb is { Enabled: true } && mb.Intensity > 0)
        {
            // Temporal blend of neighbouring frames; weights sharpen the centre frame at low intensity.
            int frames = mb.Intensity > 0.66 ? 5 : 3;
            var centre = (int)Math.Round(1 + (1 - mb.Intensity) * 6);
            var weights = frames == 3 ? $"1 {centre} 1" : $"1 1 {centre} 1 1";
            vf = $"@wfmblur:lavfi=[tmix=frames={frames}:weights='{weights}']";
        }
        return new MpvEffectPlan(shaders, opts, vf);
    }

    /// <summary>Shader parameter values for one effect (names match the //!PARAM blocks in the .glsl files).</summary>
    public static IEnumerable<(string, double)> Params(EffectSetting e)
    {
        var i = Math.Clamp(e.Intensity, 0, 1);
        switch (e.Kind)
        {
            case EffectKind.ColorGrade:
                yield return ("cg_amount", i);
                yield return ("cg_brightness", e.Param("brightness", 0.0));
                yield return ("cg_contrast", e.Param("contrast", 1.05));
                yield return ("cg_saturation", e.Param("saturation", 1.15));
                yield return ("cg_temperature", e.Param("temperature", 0.0));
                yield return ("cg_vignette", e.Param("vignette", 0.25));
                break;
            case EffectKind.Blur: yield return ("blur_radius", 0.5 + i * 7.5); break;
            case EffectKind.Bloom:
                yield return ("bloom_threshold", e.Param("threshold", 0.72));
                yield return ("bloom_strength", i * 1.2);
                break;
            case EffectKind.Glow:
                yield return ("glow_strength", i * 0.8);
                yield return ("glow_threshold", e.Param("threshold", 0.45));
                break;
            case EffectKind.Snow:
                yield return ("snow_amount", i);
                yield return ("snow_speed", e.Param("speed", 1.0));
                break;
            case EffectKind.Rain:
                yield return ("rain_amount", i);
                yield return ("rain_speed", e.Param("speed", 1.0));
                break;
            case EffectKind.Fog:
                yield return ("fog_amount", i);
                yield return ("fog_speed", e.Param("speed", 1.0));
                break;
            case EffectKind.Fire:
                yield return ("fire_amount", i);
                yield return ("fire_height", e.Param("height", 0.3));
                break;
            case EffectKind.Particles:
                yield return ("particles_amount", i);
                yield return ("particles_speed", e.Param("speed", 1.0));
                break;
            case EffectKind.Lightning:
                yield return ("lightning_amount", i);
                yield return ("lightning_rate", e.Param("rate", 1.0));
                break;
        }
    }

    /// <summary>JSON payload for the web runtime (canvas overlays + CSS filters).</summary>
    public static Dictionary<string, object> ForWeb(EffectsProfile profile, bool heavyAllowed)
    {
        var map = new Dictionary<string, object>();
        foreach (var e in profile.Items)
        {
            if (!e.Enabled || e.Intensity <= 0) continue;
            if (!heavyAllowed && EffectsProfile.IsHeavy(e.Kind)) continue;
            var p = new Dictionary<string, double> { ["intensity"] = e.Intensity };
            foreach (var kv in e.Parameters) p[kv.Key] = kv.Value;
            map[char.ToLowerInvariant(e.Kind.ToString()[0]) + e.Kind.ToString()[1..]] = p;
        }
        return map;
    }
}
