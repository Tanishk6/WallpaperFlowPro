using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.AI;

public sealed record TagResult(IReadOnlyList<string> Tags, string? Category);

/// <summary>
/// Offline tagging: colour families and mood from pixels, technical tags from metadata, and categories
/// from a keyword dictionary over title, file name and existing tags. Fast, private, no network.
/// </summary>
public static class LocalTagger
{
    private static readonly (string Category, string[] Keywords)[] Dictionary =
    {
        ("Nature", new[] { "forest", "tree", "mountain", "lake", "river", "waterfall", "flower", "field", "meadow", "nature", "landscape", "jungle", "grass", "leaf", "autumn", "valley" }),
        ("Space", new[] { "space", "galaxy", "nebula", "planet", "star", "cosmos", "earth", "moon", "universe", "astronaut", "orbit", "black hole", "aurora" }),
        ("Anime", new[] { "anime", "manga", "waifu", "chibi", "kawaii", "lofi", "lo-fi", "studio" }),
        ("Games", new[] { "game", "gaming", "minecraft", "fortnite", "valorant", "zelda", "elden", "rpg", "pixel", "8bit", "retro game" }),
        ("City", new[] { "city", "street", "tokyo", "skyline", "building", "urban", "night city", "neon street", "downtown", "rooftop", "traffic" }),
        ("Cyberpunk", new[] { "cyberpunk", "neon", "synthwave", "retrowave", "vaporwave", "outrun", "hologram", "glitch" }),
        ("Ocean", new[] { "ocean", "sea", "beach", "wave", "underwater", "coral", "fish", "coast", "surf", "island" }),
        ("Animals", new[] { "cat", "dog", "fox", "wolf", "bird", "animal", "owl", "deer", "koi", "whale", "lion", "tiger" }),
        ("Cars", new[] { "car", "drift", "racing", "jdm", "supercar", "motorcycle", "highway", "drive" }),
        ("Fantasy", new[] { "fantasy", "dragon", "castle", "magic", "wizard", "sword", "knight", "elf", "portal" }),
        ("Abstract", new[] { "abstract", "fluid", "gradient", "shapes", "particles", "geometry", "fractal", "shader", "flow", "liquid", "waves" }),
        ("Minimal", new[] { "minimal", "minimalist", "simple", "clean", "flat", "mono" }),
        ("Seasonal", new[] { "christmas", "halloween", "winter", "snow", "spring", "summer", "rain", "cherry blossom", "sakura" }),
        ("Technology", new[] { "tech", "code", "matrix", "circuit", "cyber", "ai", "robot", "digital", "terminal", "hacker" }),
    };

    public static TagResult Tag(WallpaperItem w, IReadOnlyList<ImageAnalysis.ColorStats> frames)
    {
        var tags = new List<string>();

        // Technical.
        if (w.Width >= 7680) tags.Add("8k");
        else if (w.Width >= 3840) tags.Add("4k");
        else if (w.Width >= 2560) tags.Add("1440p");
        else if (w.Width >= 1920) tags.Add("1080p");
        if (w.Width > 0 && w.Height > 0)
        {
            var aspect = (double)w.Width / w.Height;
            if (aspect >= 2.2) tags.Add("ultrawide");
            else if (aspect < 1) tags.Add("portrait");
        }
        if (w.Fps >= 50) tags.Add("60fps");
        if (w.Type == WallpaperType.Video && w.Duration > 0) tags.Add(w.Duration <= 15 ? "short loop" : w.Duration >= 120 ? "long" : "loop");
        if (w.IsAudioReactive) tags.Add("audio reactive");
        if (w.IsInteractive) tags.Add("interactive");
        tags.Add(w.Type switch
        {
            WallpaperType.Gif or WallpaperType.Apng => "animated image",
            WallpaperType.Shader => "shader",
            WallpaperType.Html or WallpaperType.Url => "web",
            WallpaperType.Clock => "clock",
            WallpaperType.Slideshow => "slideshow",
            _ => "video",
        });

        // Pixels.
        if (frames.Count > 0)
        {
            var bright = frames.Average(f => f.Brightness);
            var sat = frames.Average(f => f.Saturation);
            var contrast = frames.Average(f => f.Contrast);
            if (bright < 0.22) tags.Add("dark");
            else if (bright > 0.65) tags.Add("bright");
            if (sat < 0.12) tags.Add("monochrome");
            else if (sat > 0.55 && bright > 0.35) tags.Add("vibrant");
            else if (sat < 0.3 && bright > 0.6) tags.Add("pastel");
            if (bright < 0.3 && sat > 0.45 && contrast > 0.18) tags.Add("neon");
            var hues = new Dictionary<string, double>();
            foreach (var f in frames) foreach (var kv in f.Hues) hues[kv.Key] = hues.GetValueOrDefault(kv.Key) + kv.Value / frames.Count;
            tags.AddRange(hues.Where(kv => kv.Value >= 0.22).OrderByDescending(kv => kv.Value).Take(2).Select(kv => kv.Key));
        }

        // Categories from words.
        var text = $"{w.Title} {Path.GetFileNameWithoutExtension(w.Source)} {string.Join(' ', w.Tags)} {w.Description}".ToLowerInvariant()
            .Replace('_', ' ').Replace('-', ' ').Replace('.', ' ');
        var scores = Dictionary.Select(d => (d.Category, Score: d.Keywords.Count(k => ContainsWord(text, k)))).Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ToList();
        foreach (var (cat, _) in scores.Take(2)) tags.Add(cat.ToLowerInvariant());
        foreach (var d in Dictionary) foreach (var k in d.Keywords) if (ContainsWord(text, k) && tags.Count < 16) tags.Add(k);

        string? category = scores.FirstOrDefault().Category;
        if (category == null && tags.Contains("neon")) category = "Cyberpunk";
        if (category == null && w.Type == WallpaperType.Shader) category = "Abstract";
        return new TagResult(tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), category);
    }

    private static bool ContainsWord(string text, string word)
    {
        int i = text.IndexOf(word, StringComparison.Ordinal);
        while (i >= 0)
        {
            bool startOk = i == 0 || !char.IsLetter(text[i - 1]);
            int end = i + word.Length;
            bool endOk = end >= text.Length || !char.IsLetter(text[end]) || text[end] == 's';
            if (startOk && endOk) return true;
            i = text.IndexOf(word, i + 1, StringComparison.Ordinal);
        }
        return false;
    }
}
