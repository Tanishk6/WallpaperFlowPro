using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.AI;

public sealed record QualityReport(double Score, IReadOnlyList<string> Flags)
{
    public bool IsLow => Score < 50;
}

/// <summary>Heuristic quality score 0–100 with human-readable flags.</summary>
public static class QualityAnalyzer
{
    public static QualityReport Analyze(WallpaperItem w, double? sharpness, int monitorWidth = 1920)
    {
        double score = 100;
        var flags = new List<string>();
        if (w.IsMedia && w.Type != WallpaperType.Slideshow && w.Width > 0)
        {
            if (w.Width < monitorWidth * 0.5) { score -= 40; flags.Add("very low resolution"); }
            else if (w.Width < monitorWidth * 0.75) { score -= 20; flags.Add("low resolution"); }
        }
        if (w.Type == WallpaperType.Video && w.Bitrate > 0 && w.Width > 0 && w.Height > 0 && w.Fps > 0)
        {
            // Bits per pixel per frame; H.264 below ~0.03 usually shows blocking. Efficient codecs get more slack.
            double bpp = w.Bitrate / (w.Width * (double)w.Height * w.Fps);
            double threshold = w.Codec is "hevc" or "av1" or "vp9" ? 0.018 : 0.03;
            if (bpp < threshold * 0.5) { score -= 30; flags.Add("heavily compressed"); }
            else if (bpp < threshold) { score -= 15; flags.Add("compressed"); }
        }
        if (w.Type == WallpaperType.Video && w.Fps > 0 && w.Fps < 20) { score -= 15; flags.Add("low frame rate"); }
        if (sharpness is { } s)
        {
            if (s < 25) { score -= 25; flags.Add("blurry"); }
            else if (s < 60) { score -= 10; flags.Add("soft"); }
        }
        if (w.Type == WallpaperType.Video && w.Duration > 0 && w.Duration < 2) { score -= 10; flags.Add("very short loop"); }
        return new QualityReport(Math.Clamp(score, 0, 100), flags);
    }
}
