namespace WallpaperFlow.Core.Infrastructure;

/// <summary>Write-temp-then-replace so a crash mid-write never leaves a truncated file.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var sw = new StreamWriter(fs))
            sw.Write(content);
        if (File.Exists(path))
        {
            var bak = path + ".bak";
            File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
        }
        else File.Move(tmp, path);
    }

    /// <summary>Reads the file, falling back to the .bak copy if the primary is missing or unreadable.</summary>
    public static string? ReadWithFallback(string path, Func<string, bool> isValid)
    {
        foreach (var p in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(p)) continue;
                var s = File.ReadAllText(p);
                if (isValid(s)) return s;
            }
            catch { }
        }
        return null;
    }
}
