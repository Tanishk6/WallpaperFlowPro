using WallpaperFlow.Core.Data;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.AI;

/// <summary>
/// "You might like": builds a taste profile from tags of wallpapers you used (14-day half-life) and your
/// favourites, then ranks the rest of the library by tag affinity, novelty and quality.
/// </summary>
public static class Recommender
{
    public static IReadOnlyList<WallpaperItem> Recommend(IReadOnlyList<WallpaperItem> library, IReadOnlyList<UsageEntry> usage, DateTime now, int count = 12)
    {
        var byId = library.ToDictionary(w => w.Id);
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        void Add(WallpaperItem w, double weight)
        {
            foreach (var t in w.Tags) weights[t] = weights.GetValueOrDefault(t) + weight;
            if (!string.IsNullOrEmpty(w.Category)) weights["cat:" + w.Category] = weights.GetValueOrDefault("cat:" + w.Category) + weight * 1.5;
        }
        foreach (var u in usage)
            if (byId.TryGetValue(u.WallpaperId, out var w))
                Add(w, Math.Pow(0.5, (now - u.UsedAt).TotalDays / 14.0));
        foreach (var f in library.Where(w => w.IsFavorite)) Add(f, 2.0);

        if (weights.Count == 0)
            return library.OrderByDescending(w => w.QualityScore ?? 70).ThenByDescending(w => w.DateAdded).Take(count).ToList();

        var recent = usage.Where(u => now - u.UsedAt < TimeSpan.FromDays(3)).Select(u => u.WallpaperId).ToHashSet();
        double max = weights.Values.Max();
        return library
            .Select(w =>
            {
                double affinity = w.Tags.Sum(t => weights.GetValueOrDefault(t)) + weights.GetValueOrDefault("cat:" + w.Category);
                double score = affinity / max;
                if (recent.Contains(w.Id)) score *= 0.3;              // don't recommend what you just had
                if (w.UsageCount == 0) score += 0.25;                  // novelty
                score *= 0.6 + 0.4 * ((w.QualityScore ?? 70) / 100.0); // quality
                return (w, score);
            })
            .OrderByDescending(x => x.score).Take(count).Select(x => x.w).ToList();
    }
}
