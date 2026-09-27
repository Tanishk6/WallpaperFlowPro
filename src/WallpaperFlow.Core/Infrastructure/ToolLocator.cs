using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Infrastructure;

/// <summary>Finds mpv / ffmpeg / ffprobe: explicit setting → bundled tools folder → PATH.</summary>
public sealed class ToolLocator
{
    private readonly Func<ToolSettings> _settings;
    public ToolLocator(Func<ToolSettings> settings) => _settings = settings;

    public string? Mpv => Find("mpv.exe", _settings().MpvPath);
    public string? Ffmpeg => Find("ffmpeg.exe", _settings().FfmpegPath);
    public string? Ffprobe => Find("ffprobe.exe", _settings().FfprobePath);

    public bool HasMpv => Mpv != null;
    public bool HasFfmpeg => Ffmpeg != null && Ffprobe != null;

    private static string? Find(string exe, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var bundled = Path.Combine(AppPaths.Tools, exe);
        if (File.Exists(bundled)) return bundled;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }
}
