using System.Globalization;
using System.Text.Json;
using WallpaperFlow.Core.Infrastructure;

namespace WallpaperFlow.Core.Media;

public sealed record MediaInfo(int Width, int Height, double Duration, double Fps, string? Codec, string? Container, long Bitrate, bool HasAudio);

/// <summary>Reads resolution, duration, fps, codec and bitrate via ffprobe's JSON output.</summary>
public sealed class MediaProbe
{
    private readonly ToolLocator _tools;
    public MediaProbe(ToolLocator tools) => _tools = tools;

    public async Task<MediaInfo?> ProbeAsync(string path, CancellationToken ct = default)
    {
        var ffprobe = _tools.Ffprobe;
        if (ffprobe == null) return null;
        var res = await ProcessRunner.RunAsync(ffprobe,
            new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "--", path },
            TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (!res.Success) { Log.Warn($"ffprobe failed for {Path.GetFileName(path)}: {res.StdErr.Trim()}"); return null; }
        return Parse(res.StdOut);
    }

    public static MediaInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement? video = null;
            bool audio = false;
            if (root.TryGetProperty("streams", out var streams))
                foreach (var s in streams.EnumerateArray())
                {
                    var type = s.TryGetProperty("codec_type", out var ct) ? ct.GetString() : null;
                    var attached = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                    if (type == "video" && !attached && video == null) video = s;
                    if (type == "audio") audio = true;
                }
            if (video is not { } v) return null;
            int w = v.TryGetProperty("width", out var we) ? we.GetInt32() : 0;
            int h = v.TryGetProperty("height", out var he) ? he.GetInt32() : 0;
            var codec = v.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
            double fps = Rate(v, "avg_frame_rate");
            if (fps <= 0 || fps > 1000) fps = Rate(v, "r_frame_rate");
            double duration = Num(v, "duration");
            long bitrate = (long)Num(v, "bit_rate");
            string? container = null;
            if (root.TryGetProperty("format", out var f))
            {
                if (duration <= 0) duration = Num(f, "duration");
                if (bitrate <= 0) bitrate = (long)Num(f, "bit_rate");
                container = f.TryGetProperty("format_name", out var fn) ? fn.GetString()?.Split(',')[0] : null;
            }
            // Rotated phone videos: swap dimensions.
            if (v.TryGetProperty("side_data_list", out var sdl))
                foreach (var sd in sdl.EnumerateArray())
                    if (sd.TryGetProperty("rotation", out var rot) && Math.Abs(rot.GetDouble()) % 180 == 90) (w, h) = (h, w);
            return new MediaInfo(w, h, duration, Math.Round(fps, 3), codec, container, bitrate, audio);
        }
        catch (Exception ex)
        {
            Log.Debug("ffprobe JSON parse failed: " + ex.Message);
            return null;
        }
    }

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && double.TryParse(p.ValueKind == JsonValueKind.String ? p.GetString() : p.GetRawText(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static double Rate(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return 0;
        var parts = (p.GetString() ?? "0/1").Split('/');
        return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > 0 ? a / b : 0;
    }
}
