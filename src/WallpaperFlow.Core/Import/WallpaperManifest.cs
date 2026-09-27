using System.Text.Json;
using System.Text.Json.Serialization;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Import;

/// <summary>
/// wallpaper.json — the package format for HTML/shader/video wallpapers (see docs/WALLPAPER_API.md).
/// The properties block uses the same shape as Wallpaper Engine so existing web wallpapers port easily.
/// </summary>
public sealed class WallpaperManifest
{
    public string Title { get; set; } = "";
    public string Type { get; set; } = "html";
    public string Entry { get; set; } = "index.html";
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public List<string> Tags { get; set; } = new();
    public bool AudioReactive { get; set; }
    public bool Interactive { get; set; }
    public bool AllowNetwork { get; set; }
    public string? Preview { get; set; }
    public Dictionary<string, JsonElement>? Properties { get; set; }

    [JsonIgnore]
    public WallpaperType WallpaperType => Type.ToLowerInvariant() switch
    {
        "video" => Models.WallpaperType.Video,
        "gif" => Models.WallpaperType.Gif,
        "apng" => Models.WallpaperType.Apng,
        "shader" => Models.WallpaperType.Shader,
        "clock" => Models.WallpaperType.Clock,
        "slideshow" => Models.WallpaperType.Slideshow,
        "url" => Models.WallpaperType.Url,
        _ => Models.WallpaperType.Html,
    };

    public static WallpaperManifest? Load(string folder)
    {
        var path = Path.Combine(folder, "wallpaper.json");
        if (!File.Exists(path)) return null;
        try { return JsonDefaults.Deserialize<WallpaperManifest>(File.ReadAllText(path)); }
        catch (Exception ex) { Log.Warn($"Invalid wallpaper.json in {folder}: {ex.Message}"); return null; }
    }

    public string? PropertiesJson => Properties is { Count: > 0 } ? JsonSerializer.Serialize(Properties) : null;
}
