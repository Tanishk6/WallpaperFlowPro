using System.Diagnostics;
using System.Text;

namespace WallpaperFlow.Core.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}

/// <summary>Runs helper tools (ffmpeg/ffprobe) hidden, at low priority, with timeout + cancellation.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout,
        CancellationToken ct = default, bool lowPriority = true, Stream? stdoutSink = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = stdoutSink is null ? Encoding.UTF8 : null,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null && err.Length < 64_000) err.AppendLine(e.Data); };
        if (!p.Start()) return new ProcessResult(-1, "", "failed to start", false);
        ChildProcessJob.Assign(p);
        if (lowPriority) { try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } }
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        string stdout = "";
        try
        {
            if (stdoutSink != null) await p.StandardOutput.BaseStream.CopyToAsync(stdoutSink, cts.Token).ConfigureAwait(false);
            else stdout = await p.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, stdout, err.ToString(), true);
        }
        return new ProcessResult(p.ExitCode, stdout, err.ToString(), false);
    }
}
