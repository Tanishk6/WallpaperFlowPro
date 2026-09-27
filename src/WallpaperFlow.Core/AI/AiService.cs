using System.Text;
using WallpaperFlow.Core.Data;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Media;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.AI;

/// <summary>Runs analysis for a wallpaper: tags, category, perceptual hash, quality; optionally cloud tags.</summary>
public sealed class AiService
{
    private const int W = 96, H = 54;
    private readonly LibraryRepository _library;
    private readonly ThumbnailService _thumbs;
    private readonly SettingsStore _settings;

    public AiService(LibraryRepository library, ThumbnailService thumbs, SettingsStore settings)
    {
        _library = library;
        _thumbs = thumbs;
        _settings = settings;
    }

    public async Task AnalyzeAsync(WallpaperItem w, bool allowCloud, CancellationToken ct = default)
    {
        var frames = await _thumbs.SampleFramesAsync(w, 4, W, H, ct).ConfigureAwait(false);
        var stats = frames.Select(ImageAnalysis.Colors).ToList();
        double? sharp = null;
        if (frames.Count > 0)
        {
            var mid = frames[frames.Count / 2];
            var gray = ImageAnalysis.Gray(mid, W, H);
            w.PerceptualHash = ImageAnalysis.DHash(gray, W, H);
            // Sharpness is resolution dependent; normalise to our sample size.
            sharp = frames.Select(f => ImageAnalysis.Sharpness(ImageAnalysis.Gray(f, W, H), W, H)).Average();
        }
        var tags = LocalTagger.Tag(w, stats);
        var quality = QualityAnalyzer.Analyze(w, w.Width >= 1280 ? sharp : null);
        w.QualityScore = quality.Score;
        w.QualityFlags = quality.Flags.Count > 0 ? string.Join(", ", quality.Flags) : null;
        w.Category ??= tags.Category;
        foreach (var t in tags.Tags) if (!w.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)) w.Tags.Add(t);

        var ai = _settings.Current.Ai;
        if (allowCloud && ai.CloudTaggingEnabled && w.Thumbnail != null && File.Exists(w.Thumbnail) && GetApiKey() is { } key)
        {
            try
            {
                var cloud = await CloudVisionTagger.TagAsync(key, ai.CloudModel, w.Thumbnail, w.Title, ct).ConfigureAwait(false);
                if (cloud != null)
                {
                    foreach (var t in cloud.Tags) if (!w.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)) w.Tags.Add(t);
                    if (cloud.Category != null) w.Category = cloud.Category;
                    w.Description ??= cloud.Description;
                }
            }
            catch (Exception ex) { Log.Warn("Cloud tagging error: " + ex.Message); }
        }
        _library.Upsert(w);
    }

    public string? GetApiKey()
    {
        var p = _settings.Current.Ai.ProtectedApiKey;
        if (string.IsNullOrEmpty(p)) return null;
        try { return Encoding.UTF8.GetString(Native.Dpapi.Unprotect(Convert.FromBase64String(p))); }
        catch { return null; }
    }

    public void SetApiKey(string? key) => _settings.Update("ai", s =>
        s.Ai.ProtectedApiKey = string.IsNullOrWhiteSpace(key) ? null : Convert.ToBase64String(Native.Dpapi.Protect(Encoding.UTF8.GetBytes(key.Trim()))));

    public IReadOnlyList<DuplicateGroup> FindDuplicates() => DuplicateDetector.Find(_library.All(), _settings.Current.Ai.DuplicateThreshold);

    public IReadOnlyList<WallpaperItem> LowQuality() => _library.All().Where(w => w.QualityScore is < 50).OrderBy(w => w.QualityScore).ToList();

    public IReadOnlyList<WallpaperItem> Recommendations(int count = 12) =>
        Recommender.Recommend(_library.All(), _library.UsageSince(DateTime.UtcNow.AddDays(-90)), DateTime.UtcNow, count);
}
