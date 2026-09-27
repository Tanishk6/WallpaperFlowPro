namespace WallpaperFlow.Core.Models;

/// <summary>
/// A theme bundles app appearance (accent, backdrop, mode) with an optional wallpaper setup
/// (layout, per-monitor assignments, effects). Exported as .wftheme (zip of theme.json + optional media).
/// </summary>
public sealed class ThemeDefinition
{
    public string Id { get; set; } = "user." + Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "Untitled theme";
    public string Author { get; set; } = Environment.UserName;
    public string Description { get; set; } = "";
    public bool BuiltIn { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;

    public ThemeMode Mode { get; set; } = ThemeMode.Dark;
    public BackdropKind Backdrop { get; set; } = BackdropKind.Mica;
    public string Accent { get; set; } = "#7A8CFF";
    public string Highlight { get; set; } = "#FFB86B";
    /// <summary>Tint laid over the backdrop (dark mode). Hex with alpha.</summary>
    public string DarkTint { get; set; } = "#D90E1220";
    public string LightTint { get; set; } = "#E6EEF1F7";
    public double CornerRadius { get; set; } = 8;

    public ThemeWallpaperSetup? Wallpapers { get; set; }
}

public sealed class ThemeWallpaperSetup
{
    public LayoutMode Layout { get; set; }
    /// <summary>Wallpaper references: either library ids or package-relative paths inside the .wftheme.</summary>
    public List<ThemeWallpaperRef> Items { get; set; } = new();
}

public sealed class ThemeWallpaperRef
{
    /// <summary>Monitor index (0-based) or -1 for shared.</summary>
    public int Monitor { get; set; } = -1;
    public string? WallpaperId { get; set; }
    public string? ContentHash { get; set; }
    public string? PackagedPath { get; set; }
    public string Title { get; set; } = "";
    public EffectsProfile? Effects { get; set; }
}
