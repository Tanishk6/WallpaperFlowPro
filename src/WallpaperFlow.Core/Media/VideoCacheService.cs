using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Media;

/// <summary>
/// Smart video cache. A 4K/60 video on a 1080p screen with a 30 fps cap wastes most of its decode work,
/// so we transcode once (in the background, hardware encoder first) to the display's resolution and the
/// FPS cap. Playback then decodes a quarter of the pixels at half the rate — for as long as the wallpaper runs.
/// </summary>
public sealed class VideoCacheService : IDisposable
{
    private static readonly string[][] Encoders =
    {
        new[] { "h264_nvenc", "-preset", "p6", "-tune", "hq", "-rc", "vbr", "-cq", "18", "-b:v", "0" },
        new[] { "h264_qsv", "-global_quality", "18" },
        new[] { "h264_amf", "-quality", "quality", "-rc", "cqp", "-qp_i", "17", "-qp_p", "19" },
        new[] { "libx264", "-preset", "slow", "-crf", "17" },
        new[] { "h264_mf", "-b:v", "30M" },
    };

    private readonly ToolLocator _tools;
    private readonly Func<PerformanceSettings> _settings;
    private readonly ConcurrentDictionary<string, bool> _queued = new();
    private readonly SemaphoreSlim _worker = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private string? _workingEncoder;

    public event Action<string>? Ready;
    public event Action<string, double>? Progress;

    public VideoCacheService(ToolLocator tools, Func<PerformanceSettings> settings)
    {
        _tools = tools;
        _settings = settings;
    }

    /// <summary>Is transcoding worth it? Only when it removes a meaningful amount of work.</summary>
    public static bool Worthwhile(WallpaperItem w, int targetW, int targetH, int fpsCap)
    {
        if (w.Type != WallpaperType.Video || w.Width <= 0 || targetW <= 0) return false;
        // Only worth it for sources much larger than the screen (e.g. 4K on 1080p).
        bool tooBig = (long)w.Width * w.Height > (long)targetW * targetH * 1.7;
        bool tooFast = fpsCap > 0 && w.Fps > fpsCap + 5;
        return tooBig || tooFast;
    }

    public static (int W, int H) TargetSize(int srcW, int srcH, int boxW, int boxH)
    {
        // Cover the box (Fill scaling) while keeping aspect; even numbers for yuv420p.
        double s = Math.Max((double)boxW / srcW, (double)boxH / srcH);
        s = Math.Min(1.0, s);
        int w = (int)Math.Round(srcW * s / 2) * 2, h = (int)Math.Round(srcH * s / 2) * 2;
        return (Math.Max(2, w), Math.Max(2, h));
    }

    private static string Key(WallpaperItem w, int tw, int th, int fps)
    {
        var id = w.ContentHash ?? $"{w.Source}|{w.FileSize}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{id}|{tw}x{th}|{fps}"));
        return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
    }

    private static string PathFor(string key) => Path.Combine(AppPaths.OptimizedCache, key + ".mp4");

    public string? TryGet(WallpaperItem w, int boxW, int boxH, int fpsCap)
    {
        if (!Worthwhile(w, boxW, boxH, fpsCap)) return null;
        var (tw, th) = TargetSize(w.Width, w.Height, boxW, boxH);
        var path = PathFor(Key(w, tw, th, EffectiveFps(w, fpsCap)));
        if (!File.Exists(path)) return null;
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }
        return path;
    }

    private static int EffectiveFps(WallpaperItem w, int cap) => cap > 0 && w.Fps > cap + 5 ? cap : 0;

    public void Request(WallpaperItem w, int boxW, int boxH, int fpsCap)
    {
        if (!_settings().SmartVideoCache || _tools.Ffmpeg == null || !Worthwhile(w, boxW, boxH, fpsCap)) return;
        var (tw, th) = TargetSize(w.Width, w.Height, boxW, boxH);
        int fps = EffectiveFps(w, fpsCap);
        var key = Key(w, tw, th, fps);
        if (!_queued.TryAdd(key, true)) return;
        _ = Task.Run(async () =>
        {
            await _worker.WaitAsync(_cts.Token).ConfigureAwait(false);
            try { await TranscodeAsync(w, tw, th, fps, key).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn($"Optimising '{w.Title}' failed: {ex.Message}"); }
            finally { _worker.Release(); _queued.TryRemove(key, out _); }
        });
    }

    private async Task TranscodeAsync(WallpaperItem w, int tw, int th, int fps, string key)
    {
        Directory.CreateDirectory(AppPaths.OptimizedCache);
        var final = PathFor(key);
        if (File.Exists(final)) return;
        var tmp = final + ".part.mp4";
        var vf = $"scale={tw}:{th}:flags=lanczos" + (fps > 0 ? $",fps={fps}" : "") + ",format=yuv420p";
        var order = _workingEncoder != null ? Encoders.Where(e => e[0] == _workingEncoder).Concat(Encoders.Where(e => e[0] != _workingEncoder)) : Encoders;
        Log.Info($"Optimising '{w.Title}' {w.Width}x{w.Height}@{w.Fps:0.#} → {tw}x{th}@{(fps > 0 ? fps : w.Fps):0.#}");
        foreach (var enc in order)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", w.ResolveEntryPath(), "-map", "0:v:0", "-map", "0:a:0?", "-vf", vf, "-c:v" };
            args.AddRange(enc);
            args.AddRange(new[] { "-g", "120", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", tmp });
            var r = await ProcessRunner.RunAsync(_tools.Ffmpeg!, args, TimeSpan.FromHours(2), _cts.Token).ConfigureAwait(false);
            if (r.Success && File.Exists(tmp) && new FileInfo(tmp).Length > 0)
            {
                File.Move(tmp, final, overwrite: true);
                _workingEncoder = enc[0];
                Log.Info($"Optimised '{w.Title}' with {enc[0]} ({new FileInfo(final).Length / 1048576} MB)");
                Enforce();
                Ready?.Invoke(w.Id);
                return;
            }
            try { File.Delete(tmp); } catch { }
        }
        Log.Warn($"No encoder could optimise '{w.Title}'");
    }

    /// <summary>LRU eviction down to the configured limit.</summary>
    public void Enforce()
    {
        try
        {
            long limit = (long)_settings().VideoCacheLimitMb * 1048576;
            var files = new DirectoryInfo(AppPaths.OptimizedCache).EnumerateFiles("*.mp4").Where(f => !f.Name.EndsWith(".part.mp4"))
                .OrderBy(f => f.LastAccessTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= limit) break;
                total -= f.Length;
                try { f.Delete(); } catch { }
            }
        }
        catch { }
    }

    public long SizeBytes()
    {
        try { return new DirectoryInfo(AppPaths.OptimizedCache).EnumerateFiles().Sum(f => f.Length); } catch { return 0; }
    }

    public void Clear()
    {
        try { foreach (var f in Directory.EnumerateFiles(AppPaths.OptimizedCache)) File.Delete(f); } catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
