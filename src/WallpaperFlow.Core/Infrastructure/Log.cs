using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Infrastructure;

/// <summary>
/// Tiny rolling file logger. Writes happen on a background task through an unbounded channel,
/// so callers never block on disk I/O. One file per day, retained for 7 days.
/// </summary>
public static class Log
{
    private static readonly Channel<string> Queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private static Task? _writer;
    private static string _dir = "";
    public static LogSeverity MinimumLevel { get; set; } = LogSeverity.Info;
    public static event Action<LogSeverity, string>? Written;

    public static void Initialize(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(directory);
        Cleanup();
        _writer ??= Task.Run(WriteLoop);
    }

    public static void Debug(string msg, [CallerFilePath] string file = "") => Write(LogSeverity.Debug, msg, file);
    public static void Info(string msg, [CallerFilePath] string file = "") => Write(LogSeverity.Info, msg, file);
    public static void Warn(string msg, [CallerFilePath] string file = "") => Write(LogSeverity.Warn, msg, file);
    public static void Error(string msg, Exception? ex = null, [CallerFilePath] string file = "") =>
        Write(LogSeverity.Error, ex is null ? msg : $"{msg}: {ex}", file);

    private static void Write(LogSeverity level, string msg, string file)
    {
        if (level < MinimumLevel) return;
        var src = Path.GetFileNameWithoutExtension(file);
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()[..4]}] {src}: {msg}";
        Queue.Writer.TryWrite(line);
        try { Written?.Invoke(level, line); } catch { /* never throw from logging */ }
    }

    private static async Task WriteLoop()
    {
        var sb = new StringBuilder();
        while (await Queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            sb.Clear();
            while (Queue.Reader.TryRead(out var line)) sb.AppendLine(line);
            try
            {
                var path = Path.Combine(_dir, $"wallpaperflow-{DateTime.Now:yyyyMMdd}.log");
                await File.AppendAllTextAsync(path, sb.ToString()).ConfigureAwait(false);
            }
            catch { /* disk full / locked: drop */ }
        }
    }

    public static async Task FlushAsync()
    {
        for (int i = 0; i < 20 && Queue.Reader.Count > 0; i++) await Task.Delay(25).ConfigureAwait(false);
    }

    private static void Cleanup()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "wallpaperflow-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)) File.Delete(f);
        }
        catch { }
    }

    public static string CurrentFile => Path.Combine(_dir, $"wallpaperflow-{DateTime.Now:yyyyMMdd}.log");
}
