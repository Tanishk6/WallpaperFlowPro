using System.IO.Compression;
using WallpaperFlow.Core.Data;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Themes;

/// <summary>Built-in and user themes; export/import as .wftheme (zip: theme.json + optional wallpaper media).</summary>
public sealed class ThemeService
{
    private readonly LibraryRepository _library;
    private readonly SettingsStore _settings;
    public event Action? Changed;

    public ThemeService(LibraryRepository library, SettingsStore settings)
    {
        _library = library;
        _settings = settings;
    }

    public static IReadOnlyList<ThemeDefinition> BuiltIns { get; } = new[]
    {
        new ThemeDefinition { Id = "builtin.periwinkle", Name = "Periwinkle", BuiltIn = true, Mode = ThemeMode.Dark, Accent = "#7A8CFF", Highlight = "#FFB86B", Description = "The default: cool ink with a warm live-status accent." },
        new ThemeDefinition { Id = "builtin.paper", Name = "Paper", BuiltIn = true, Mode = ThemeMode.Light, Accent = "#4254D6", Highlight = "#C8641E", Description = "Light and quiet for bright rooms." },
        new ThemeDefinition { Id = "builtin.moss", Name = "Moss", BuiltIn = true, Mode = ThemeMode.Dark, Accent = "#7FC8A0", Highlight = "#F2C46D", DarkTint = "#D90F1714", Description = "Green-grey, easy on the eyes at night." },
        new ThemeDefinition { Id = "builtin.ember", Name = "Ember", BuiltIn = true, Mode = ThemeMode.Dark, Accent = "#FF8A65", Highlight = "#FFD166", DarkTint = "#D9180F10", Description = "Warm tint for sunset setups." },
        new ThemeDefinition { Id = "builtin.glacier", Name = "Glacier", BuiltIn = true, Mode = ThemeMode.Light, Accent = "#1F8FB8", Highlight = "#E0704A", LightTint = "#E6EAF3F7", Backdrop = BackdropKind.Acrylic, Description = "Acrylic glass over a pale blue tint." },
    };

    public IReadOnlyList<ThemeDefinition> All()
    {
        var list = new List<ThemeDefinition>(BuiltIns);
        Directory.CreateDirectory(AppPaths.Themes);
        foreach (var f in Directory.EnumerateFiles(AppPaths.Themes, "*.json"))
        {
            try { if (JsonDefaults.Deserialize<ThemeDefinition>(File.ReadAllText(f)) is { } t) { t.BuiltIn = false; list.Add(t); } }
            catch (Exception ex) { Log.Warn($"Theme {Path.GetFileName(f)} unreadable: {ex.Message}"); }
        }
        return list;
    }

    public ThemeDefinition Current => All().FirstOrDefault(t => t.Id == _settings.Current.Appearance.ThemeId) ?? BuiltIns[0];

    public void Save(ThemeDefinition theme)
    {
        if (theme.BuiltIn) throw new InvalidOperationException("Built-in themes are read-only; duplicate it first.");
        Directory.CreateDirectory(AppPaths.Themes);
        AtomicFile.WriteAllText(Path.Combine(AppPaths.Themes, AppPaths.SafeFileName(theme.Id) + ".json"), JsonDefaults.Serialize(theme));
        Changed?.Invoke();
    }

    public void Delete(string id)
    {
        var f = Path.Combine(AppPaths.Themes, AppPaths.SafeFileName(id) + ".json");
        if (File.Exists(f)) File.Delete(f);
        if (_settings.Current.Appearance.ThemeId == id) _settings.Update("appearance", s => s.Appearance.ThemeId = BuiltIns[0].Id);
        Changed?.Invoke();
    }

    /// <summary>Snapshot of the current appearance + wallpaper layout as a new user theme.</summary>
    public ThemeDefinition CaptureCurrent(string name)
    {
        var s = _settings.Current;
        var baseTheme = Current;
        var t = new ThemeDefinition
        {
            Name = name, Mode = s.Appearance.Mode == ThemeMode.System ? baseTheme.Mode : s.Appearance.Mode, Backdrop = s.Appearance.Backdrop,
            Accent = baseTheme.Accent, Highlight = baseTheme.Highlight, DarkTint = baseTheme.DarkTint, LightTint = baseTheme.LightTint,
            Wallpapers = new ThemeWallpaperSetup { Layout = s.Displays.Layout },
        };
        void AddRef(int monitor, string? id)
        {
            if (id == null || _library.Get(id) is not { } w) return;
            t.Wallpapers.Items.Add(new ThemeWallpaperRef { Monitor = monitor, WallpaperId = w.Id, ContentHash = w.ContentHash, Title = w.Title, Effects = w.Effects.Clone() });
        }
        AddRef(-1, s.Displays.SharedWallpaperId);
        int i = 0;
        foreach (var kv in s.Displays.PerMonitor) AddRef(i++, kv.Value);
        return t;
    }

    public void Export(ThemeDefinition theme, string destination, bool includeWallpapers)
    {
        if (File.Exists(destination)) File.Delete(destination);
        using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);
        var copy = JsonDefaults.Deserialize<ThemeDefinition>(JsonDefaults.Serialize(theme))!;
        if (includeWallpapers && copy.Wallpapers != null)
        {
            foreach (var r in copy.Wallpapers.Items)
            {
                if (r.WallpaperId == null || _library.Get(r.WallpaperId) is not { } w || !w.IsMedia || !File.Exists(w.Source)) continue;
                var entryName = $"media/{w.Id}{Path.GetExtension(w.Source)}";
                zip.CreateEntryFromFile(w.Source, entryName, CompressionLevel.NoCompression);
                r.PackagedPath = entryName;
            }
        }
        var e = zip.CreateEntry("theme.json");
        using var sw = new StreamWriter(e.Open());
        sw.Write(JsonDefaults.Serialize(copy));
    }

    /// <summary>Imports a .wftheme. Packaged media is returned so the caller can run it through the importer.</summary>
    public (ThemeDefinition Theme, string? MediaFolder) Import(string path)
    {
        var temp = Path.Combine(AppPaths.Temp, "theme-" + Guid.NewGuid().ToString("N")[..8]);
        Media.FileValidator.SafeExtract(path, temp, 4L * 1024 * 1024 * 1024, 200);
        var json = Path.Combine(temp, "theme.json");
        if (!File.Exists(json)) throw new InvalidDataException("Not a WallpaperFlow theme (theme.json missing)");
        var theme = JsonDefaults.Deserialize<ThemeDefinition>(File.ReadAllText(json)) ?? throw new InvalidDataException("Theme file is invalid");
        theme.Id = "user." + Guid.NewGuid().ToString("N")[..10];
        theme.BuiltIn = false;
        Save(theme);
        var media = Path.Combine(temp, "media");
        return (theme, Directory.Exists(media) ? media : null);
    }

    public void Apply(ThemeDefinition theme, bool applyWallpapers)
    {
        _settings.Update("appearance", s =>
        {
            s.Appearance.ThemeId = theme.Id;
            s.Appearance.Mode = theme.Mode;
            s.Appearance.Backdrop = theme.Backdrop;
        });
        if (!applyWallpapers || theme.Wallpapers == null) return;
        _settings.Update("displays", s =>
        {
            s.Displays.Layout = theme.Wallpapers.Layout;
            foreach (var r in theme.Wallpapers.Items)
            {
                var w = (r.WallpaperId != null ? _library.Get(r.WallpaperId) : null) ?? (r.ContentHash != null ? _library.FindByHash(r.ContentHash) : null);
                if (w == null) continue;
                if (r.Effects != null) { w.Effects = r.Effects.Clone(); _library.Upsert(w); }
                if (r.Monitor < 0) s.Displays.SharedWallpaperId = w.Id;
            }
        });
    }
}
