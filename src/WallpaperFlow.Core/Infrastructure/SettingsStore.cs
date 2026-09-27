using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Infrastructure;

/// <summary>
/// Owns <see cref="AppSettings"/>. Saves are debounced (500 ms) and atomic; Changed fires with the
/// section name so services only react to what concerns them.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private readonly string _path;
    private readonly Timer _debounce;
    private readonly object _gate = new();
    public AppSettings Current { get; private set; }
    public event Action<string>? Changed;

    public SettingsStore(string path)
    {
        _path = path;
        Current = Load(path);
        _debounce = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private static AppSettings Load(string path)
    {
        var json = AtomicFile.ReadWithFallback(path, s =>
        {
            try { return JsonDefaults.Deserialize<AppSettings>(s) != null; } catch { return false; }
        });
        if (json == null) return new AppSettings();
        var s = JsonDefaults.Deserialize<AppSettings>(json)!;
        Migrate(s);
        Sanitize(s);
        return s;
    }

    /// <summary>One-time upgrades of settings written by older versions.</summary>
    internal static void Migrate(AppSettings s)
    {
        if (s.SchemaVersion < 2)
        {
            // v1 capped web wallpapers at 30 fps and re-encoded large videos at 30 fps / lower quality by default,
            // which looked choppy. Move to full frame rate and drop the old re-encoded copies.
            if (s.Performance.FpsLimit is > 0 and < 60) s.Performance.FpsLimit = 60;
            if (s.Performance.BackgroundFps < 30) s.Performance.BackgroundFps = 30;
            s.Performance.SmartVideoCache = false;
            try { if (Directory.Exists(AppPaths.OptimizedCache)) Directory.Delete(AppPaths.OptimizedCache, true); } catch { }
            s.SchemaVersion = 2;
        }
        if (s.SchemaVersion < 3)
        {
            // v2 pointed at placeholder web addresses; updates now come from the project's GitHub repository.
            if (s.Updates.ManifestUrl.Contains("wallpaperflow.app", StringComparison.OrdinalIgnoreCase)) s.Updates.ManifestUrl = "";
            if (s.Cloud.CatalogUrl.Contains("wallpaperflow.app", StringComparison.OrdinalIgnoreCase)) s.Cloud.CatalogUrl = "";
            s.SchemaVersion = 3;
        }
    }

    /// <summary>Clamps values that a hand-edited file could make dangerous.</summary>
    public static void Sanitize(AppSettings s)
    {
        s.Performance.FpsLimit = Math.Clamp(s.Performance.FpsLimit, 0, 240);
        s.Performance.BackgroundFps = Math.Clamp(s.Performance.BackgroundFps, 5, 240);
        s.Performance.RenderScale = Math.Clamp(s.Performance.RenderScale, 0.25, 1.0);
        s.Performance.GpuBusyThreshold = Math.Clamp(s.Performance.GpuBusyThreshold, 30, 100);
        s.Battery.PauseBelowPercent = Math.Clamp(s.Battery.PauseBelowPercent, 0, 100);
        s.Battery.ReducedFps = Math.Clamp(s.Battery.ReducedFps, 5, 60);
        s.Audio.Bands = Math.Clamp(s.Audio.Bands, 16, 128);
        s.Scheduler.CustomMinutes = Math.Clamp(s.Scheduler.CustomMinutes, 1, 60 * 24 * 30);
        s.Detection.Rules ??= ProcessRule.Defaults();
        s.Displays.PerMonitor ??= new();
    }

    /// <summary>Mutate settings and schedule a save. <paramref name="section"/> is broadcast to listeners.</summary>
    public void Update(string section, Action<AppSettings> mutate)
    {
        lock (_gate) mutate(Current);
        Sanitize(Current);
        _debounce.Change(500, Timeout.Infinite);
        try { Changed?.Invoke(section); } catch (Exception ex) { Log.Error("Settings listener failed", ex); }
    }

    public void Replace(AppSettings settings)
    {
        lock (_gate) Current = settings;
        Sanitize(Current);
        SaveNow();
        Changed?.Invoke("*");
    }

    public void SaveNow()
    {
        try
        {
            string json;
            lock (_gate) json = JsonDefaults.Serialize(Current);
            AtomicFile.WriteAllText(_path, json);
        }
        catch (Exception ex) { Log.Error("Saving settings failed", ex); }
    }

    public void Dispose()
    {
        _debounce.Dispose();
        SaveNow();
    }
}
