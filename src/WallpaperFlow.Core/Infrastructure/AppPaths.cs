namespace WallpaperFlow.Core.Infrastructure;

/// <summary>All on-disk locations. Everything user-specific lives under %LocalAppData%\WallpaperFlow.</summary>
public static class AppPaths
{
    public static string Root { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WallpaperFlow");

    public static string Data => Path.Combine(Root, "Data");
    public static string Database => Path.Combine(Data, "library.db");
    public static string SettingsFile => Path.Combine(Data, "settings.json");
    public static string Library => Path.Combine(Root, "Library");
    public static string Thumbnails => Path.Combine(Root, "cache", "thumbs");
    public static string Previews => Path.Combine(Root, "cache", "previews");
    public static string OptimizedCache => Path.Combine(Root, "cache", "optimized");
    public static string Logs => Path.Combine(Root, "Logs");
    public static string WebView2Data => Path.Combine(Root, "WebView2");
    public static string Themes => Path.Combine(Root, "Themes");
    public static string Updates => Path.Combine(Root, "Updates");
    public static string Installers => Path.Combine(Root, "installers");
    public static string Backups => Path.Combine(Root, "Backups");
    public static string Crash => Path.Combine(Root, "Crash");
    public static string Temp => Path.Combine(Root, "temp");
    public static string PendingRestore => Path.Combine(Data, "pending-restore.zip");
    public static string SessionLock => Path.Combine(Data, "session.lock");

    public static string AppDir => AppContext.BaseDirectory;
    public static string Runtime => Path.Combine(AppDir, "runtime");
    public static string Tools => Path.Combine(AppDir, "tools");

    /// <summary>For tests / portable mode.</summary>
    public static void OverrideRoot(string root) => Root = root;

    public static void EnsureCreated()
    {
        foreach (var d in new[] { Data, Library, Thumbnails, Previews, OptimizedCache, Logs, WebView2Data, Themes, Updates, Installers, Backups, Crash, Temp })
            Directory.CreateDirectory(d);
    }

    /// <summary>True when <paramref name="path"/> resolves inside <paramref name="root"/> (zip-slip / traversal guard).</summary>
    public static bool IsInside(string root, string path)
    {
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var p = Path.GetFullPath(path);
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    public static string SafeFileName(string name, int max = 80)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || c < 32 ? '_' : c).ToArray();
        var s = new string(chars).Trim().Trim('.');
        if (s.Length == 0) s = "wallpaper";
        return s.Length > max ? s[..max] : s;
    }
}
