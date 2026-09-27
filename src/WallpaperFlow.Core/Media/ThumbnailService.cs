using System.Globalization;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Media;

/// <summary>Thumbnails (480 px JPEG), 4-second hover preview clips (360 px MP4) and raw frames for analysis.</summary>
public sealed class ThumbnailService
{
    private readonly ToolLocator _tools;
    private readonly SemaphoreSlim _parallel = new(2, 2);
    public ThumbnailService(ToolLocator tools) => _tools = tools;

    private static string Inv(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    public async Task<string?> CreateThumbnailAsync(WallpaperItem w, CancellationToken ct = default)
    {
        Directory.CreateDirectory(AppPaths.Thumbnails);
        var output = Path.Combine(AppPaths.Thumbnails, w.Id + ".jpg");

        // Packages may ship their own preview (Wallpaper Engine projects do).
        if (w.IsWeb && Directory.Exists(w.Source))
        {
            foreach (var name in new[] { "preview.jpg", "preview.png", "preview.gif", "preview.webp", "thumbnail.jpg", "thumbnail.png" })
            {
                var p = Path.Combine(w.Source, name);
                if (File.Exists(p)) return await FrameAsync(p, 0, output, ct).ConfigureAwait(false) ?? CopyAs(p, output);
            }
            return null;
        }

        string? input = w.Type switch
        {
            WallpaperType.Slideshow => Directory.Exists(w.Source)
                ? Directory.EnumerateFiles(w.Source).Where(FileValidator.IsImageExtension).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null,
            _ => w.ResolveEntryPath(),
        };
        if (input == null || !File.Exists(input)) return null;
        var at = w.Type == WallpaperType.Video && w.Duration > 2 ? Math.Min(w.Duration * 0.2, 10) : 0;
        return await FrameAsync(input, at, output, ct).ConfigureAwait(false);
    }

    private static string? CopyAs(string src, string dest)
    {
        try { File.Copy(src, Path.ChangeExtension(dest, Path.GetExtension(src)), true); return Path.ChangeExtension(dest, Path.GetExtension(src)); }
        catch { return null; }
    }

    private async Task<string?> FrameAsync(string input, double at, string output, CancellationToken ct)
    {
        var ffmpeg = _tools.Ffmpeg;
        if (ffmpeg == null) return null;
        await _parallel.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            if (at > 0) args.AddRange(new[] { "-ss", Inv(at) });
            args.AddRange(new[] { "-i", input, "-frames:v", "1", "-vf", "scale=480:-2:flags=lanczos", "-q:v", "4", output });
            var r = await ProcessRunner.RunAsync(ffmpeg, args, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            return r.Success && File.Exists(output) ? output : null;
        }
        finally { _parallel.Release(); }
    }

    public async Task<string?> CreatePreviewClipAsync(WallpaperItem w, CancellationToken ct = default)
    {
        var ffmpeg = _tools.Ffmpeg;
        if (ffmpeg == null || w.Type is not (WallpaperType.Video or WallpaperType.Gif or WallpaperType.Apng)) return null;
        var input = w.ResolveEntryPath();
        if (!File.Exists(input)) return null;
        Directory.CreateDirectory(AppPaths.Previews);
        var output = Path.Combine(AppPaths.Previews, w.Id + ".mp4");
        var start = w.Duration > 8 ? Math.Min(w.Duration * 0.25, 20) : 0;
        await _parallel.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var enc in new[] { new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "30" }, new[] { "-c:v", "mpeg4", "-q:v", "7" } })
            {
                var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
                if (start > 0) args.AddRange(new[] { "-ss", Inv(start) });
                if (w.Type != WallpaperType.Video) args.AddRange(new[] { "-ignore_loop", "0" });
                args.AddRange(new[] { "-t", "4", "-i", input, "-t", "4", "-an", "-vf", "scale=360:-2:flags=bilinear,fps=24,format=yuv420p" });
                args.AddRange(enc);
                args.AddRange(new[] { "-movflags", "+faststart", output });
                var r = await ProcessRunner.RunAsync(ffmpeg, args, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                if (r.Success && File.Exists(output) && new FileInfo(output).Length > 0) return output;
            }
            return null;
        }
        finally { _parallel.Release(); }
    }

    /// <summary>Samples <paramref name="count"/> frames as packed RGB24 at width×height (for tagging, hashing, quality checks).</summary>
    public async Task<List<byte[]>> SampleFramesAsync(WallpaperItem w, int count, int width, int height, CancellationToken ct = default)
    {
        var frames = new List<byte[]>();
        var ffmpeg = _tools.Ffmpeg;
        if (ffmpeg == null) return frames;
        string? input = w.Type == WallpaperType.Slideshow
            ? Directory.EnumerateFiles(w.Source).Where(FileValidator.IsImageExtension).FirstOrDefault()
            : w.IsWeb ? (w.Thumbnail != null && File.Exists(w.Thumbnail) ? w.Thumbnail : null) : w.ResolveEntryPath();
        if (input == null) return frames;
        bool still = w.IsWeb || w.Type == WallpaperType.Slideshow || w.Duration <= 0.5;
        int n = still ? 1 : count;
        for (int i = 0; i < n; i++)
        {
            double at = still ? 0 : w.Duration * (i + 0.5) / n;
            using var ms = new MemoryStream(width * height * 3);
            var args = new List<string> { "-hide_banner", "-loglevel", "error" };
            if (at > 0) args.AddRange(new[] { "-ss", Inv(at) });
            args.AddRange(new[] { "-i", input, "-frames:v", "1", "-vf", $"scale={width}:{height}:flags=area", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" });
            var r = await ProcessRunner.RunAsync(ffmpeg, args, TimeSpan.FromSeconds(30), ct, stdoutSink: ms).ConfigureAwait(false);
            if (r.Success && ms.Length == width * height * 3) frames.Add(ms.ToArray());
        }
        return frames;
    }
}
