using System.Text;
using WallpaperFlow.Core.Infrastructure;

namespace WallpaperFlow.Core.Platform;

/// <summary>
/// Detects unclean shutdowns with a session sentinel file. After two crashes in a row the app starts in
/// safe mode: no wallpapers are restored, so a bad wallpaper or driver can't create a crash loop.
/// </summary>
public static class CrashGuard
{
    private static string CountFile => Path.Combine(AppPaths.Data, "crash-count");
    public static bool PreviousSessionCrashed { get; private set; }
    public static bool SafeMode { get; private set; }

    public static void BeginSession(bool forceSafeMode = false)
    {
        int count = 0;
        try { if (File.Exists(CountFile)) int.TryParse(File.ReadAllText(CountFile), out count); } catch { }
        PreviousSessionCrashed = File.Exists(AppPaths.SessionLock);
        count = PreviousSessionCrashed ? count + 1 : 0;
        SafeMode = forceSafeMode || count >= 2;
        try
        {
            File.WriteAllText(CountFile, count.ToString());
            File.WriteAllText(AppPaths.SessionLock, $"{Environment.ProcessId} {DateTime.UtcNow:o}");
        }
        catch { }
        if (PreviousSessionCrashed) Log.Warn($"Previous session ended unexpectedly (consecutive: {count}); safe mode: {SafeMode}");
    }

    public static void EndSession()
    {
        try
        {
            File.Delete(AppPaths.SessionLock);
            File.WriteAllText(CountFile, "0");
        }
        catch { }
    }

    public static string WriteReport(Exception ex, string context)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Crash);
            var path = Path.Combine(AppPaths.Crash, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"WallpaperFlow Pro {typeof(CrashGuard).Assembly.GetName().Version} — {context}");
            sb.AppendLine($"Time: {DateTime.Now:o}");
            sb.AppendLine($"OS: {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")}), .NET {Environment.Version}");
            sb.AppendLine($"Memory: {Environment.WorkingSet / 1048576} MB");
            sb.AppendLine();
            sb.AppendLine(ex.ToString());
            try
            {
                if (File.Exists(Log.CurrentFile))
                {
                    sb.AppendLine().AppendLine("── last log lines ──");
                    using var fs = new FileStream(Log.CurrentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs);
                    foreach (var line in sr.ReadToEnd().Split('\n').TakeLast(80)) sb.AppendLine(line.TrimEnd());
                }
            }
            catch { }
            File.WriteAllText(path, sb.ToString());
            foreach (var old in new DirectoryInfo(AppPaths.Crash).EnumerateFiles("crash-*.txt").OrderByDescending(f => f.CreationTimeUtc).Skip(20)) old.Delete();
            return path;
        }
        catch { return ""; }
    }
}
