using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Import;

public sealed record WeProject(string Folder, string Title, string Type, string? File, string? Preview, bool Supported, string? Reason,
    string? WorkshopId, IReadOnlyList<string> Tags, bool AudioReactive, string? PropertiesJson, string? Description);

/// <summary>
/// Finds and imports Wallpaper Engine projects (Steam workshop items and "myprojects"). Video and web
/// projects run natively; scene and application projects rely on Wallpaper Engine's proprietary
/// renderer / arbitrary executables and are listed as unsupported with the reason.
/// </summary>
public sealed partial class WallpaperEngineImporter
{
    private const string AppId = "431960";
    private readonly ImportService _import;
    public WallpaperEngineImporter(ImportService import) => _import = import;

    public static IReadOnlyList<string> FindProjectRoots()
    {
        var roots = new List<string>();
        foreach (var lib in SteamLibraries())
        {
            roots.Add(Path.Combine(lib, "steamapps", "workshop", "content", AppId));
            roots.Add(Path.Combine(lib, "steamapps", "common", "wallpaper_engine", "projects", "myprojects"));
            roots.Add(Path.Combine(lib, "steamapps", "common", "wallpaper_engine", "projects", "defaultprojects"));
        }
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)] private static partial Regex VdfPath();

    public static IReadOnlyList<string> SteamLibraries()
    {
        var libs = new List<string>();
        try
        {
            var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                        ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
            if (string.IsNullOrEmpty(steam)) return libs;
            steam = steam.Replace('/', '\\');
            libs.Add(steam);
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in VdfPath().Matches(File.ReadAllText(vdf)))
                    libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        catch (Exception ex) { Log.Warn("Steam library lookup failed: " + ex.Message); }
        return libs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<WeProject> Scan()
    {
        var list = new List<WeProject>();
        foreach (var root in FindProjectRoots())
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (ReadProject(dir) is { } p) list.Add(p);
        return list.OrderBy(p => !p.Supported).ThenBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static WeProject? ReadProject(string folder)
    {
        var path = Path.Combine(folder, "project.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var type = (S("type") ?? "").ToLowerInvariant();
            var file = S("file");
            var tags = r.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
            bool audio = false;
            string? props = null;
            if (r.TryGetProperty("general", out var g))
            {
                audio = g.TryGetProperty("supportsaudioprocessing", out var a) && a.ValueKind == JsonValueKind.True;
                if (g.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object) props = p.GetRawText();
            }
            var (supported, reason) = type switch
            {
                "video" when file != null && File.Exists(Path.Combine(folder, file)) => (true, (string?)null),
                "web" when file != null && File.Exists(Path.Combine(folder, file)) => (true, null),
                "video" or "web" => (false, "Entry file is missing"),
                "scene" => (false, "Scene wallpapers use Wallpaper Engine's own renderer"),
                "application" => (false, "Application wallpapers run executables and are blocked for security"),
                _ => (false, "Unknown project type"),
            };
            return new WeProject(folder, S("title") ?? Path.GetFileName(folder), type, file, S("preview"), supported, reason,
                S("workshopid") ?? Path.GetFileName(folder), tags, audio, props, S("description"));
        }
        catch (Exception ex)
        {
            Log.Debug($"Unreadable project.json in {folder}: {ex.Message}");
            return null;
        }
    }

    public async Task<WallpaperItem?> ImportAsync(WeProject p, ImportResult result, CancellationToken ct)
    {
        if (!p.Supported) { result.Errors.Add($"{p.Title}: {p.Reason}"); return null; }
        var manifest = new WallpaperManifest
        {
            Title = p.Title,
            Type = p.Type == "video" ? "video" : "html",
            Entry = p.File!,
            Tags = p.Tags.Select(t => t.ToLowerInvariant()).ToList(),
            AudioReactive = p.AudioReactive,
            Interactive = p.Type == "web",
            Description = p.Description,
            Properties = p.PropertiesJson != null ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(p.PropertiesJson) : null,
        };
        return await _import.ImportPackageFolderAsync(p.Folder, copy: true, result, ct, manifestOverride: manifest, configure: w =>
        {
            w.Origin = "wallpaper-engine";
            w.OriginId = p.WorkshopId;
            w.Tags.Add("wallpaper engine");
        }).ConfigureAwait(false);
    }
}
