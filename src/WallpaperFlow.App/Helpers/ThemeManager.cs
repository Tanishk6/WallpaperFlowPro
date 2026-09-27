using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using WallpaperFlow.App.ViewModels;
using WallpaperFlow.Core;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.App.Helpers;

/// <summary>Swaps the Dark/Light palette, injects theme accent colours and applies the backdrop to open windows.</summary>
public sealed class ThemeManager : IThemeApplier
{
    private readonly AppServices _services;
    private ThemeMode _mode;
    public bool IsDark { get; private set; } = true;
    public BackdropKind Backdrop { get; private set; } = BackdropKind.Mica;
    public BackgroundScene Scene { get; private set; } = BackgroundScene.Aurora;
    public bool ReduceMotion { get; private set; }
    public event Action? Applied;

    public ThemeManager(AppServices services)
    {
        _services = services;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _mode == ThemeMode.System)
                Application.Current?.Dispatcher.BeginInvoke(ApplyCurrent);
        };
    }

    public void ApplyCurrent()
    {
        var a = _services.Settings.Current.Appearance;
        Apply(_services.Themes.Current, a.Mode, a.Backdrop);
    }

    public void Apply(ThemeDefinition theme, ThemeMode mode, BackdropKind backdrop)
    {
        _mode = mode;
        IsDark = mode == ThemeMode.System ? SystemIsDark() : mode == ThemeMode.Dark;
        Backdrop = backdrop;
        Scene = _services.Settings.Current.Appearance.Scene;
        ReduceMotion = _services.Settings.Current.Appearance.ReduceMotion;
        var app = Application.Current;
        var dicts = app.Resources.MergedDictionaries;
        dicts[0] = new ResourceDictionary { Source = new Uri($"/WallpaperFlowPro;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative) };

        var accent = Parse(theme.Accent, Color.FromRgb(0x7A, 0x8C, 0xFF));
        var highlight = Parse(theme.Highlight, Color.FromRgb(0xFF, 0xB8, 0x6B));
        var tint = Parse(IsDark ? theme.DarkTint : theme.LightTint, IsDark ? Color.FromArgb(0xD9, 0x0E, 0x12, 0x20) : Color.FromArgb(0xE6, 0xEE, 0xF1, 0xF7));
        app.Resources["AccentColor"] = accent;
        app.Resources["AccentBrush"] = Frozen(accent);
        app.Resources["AccentSoftBrush"] = Frozen(Color.FromArgb(0x33, accent.R, accent.G, accent.B));
        app.Resources["AccentOnBrush"] = Frozen(Luma(accent) > 0.6 ? Color.FromRgb(0x10, 0x14, 0x24) : Colors.White);
        app.Resources["HighlightBrush"] = Frozen(highlight);
        app.Resources["HighlightSoftBrush"] = Frozen(Color.FromArgb(0x2E, highlight.R, highlight.G, highlight.B));
        app.Resources["WindowTintBrush"] = Frozen(tint);
        var gradient = new LinearGradientBrush(accent, highlight, 0);
        gradient.Freeze();
        app.Resources["AccentGradientBrush"] = gradient;
        foreach (Window w in app.Windows) WindowEffects.Apply(w, IsDark, backdrop);
        HoverPreview.Enabled = _services.Settings.Current.Appearance.HoverPreview && !_services.Settings.Current.Appearance.ReduceMotion;
        Applied?.Invoke();
    }

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    public static Color Parse(string? hex, Color fallback)
    {
        try { return string.IsNullOrWhiteSpace(hex) ? fallback : (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }

    public static bool SystemIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return true; }
    }
}
