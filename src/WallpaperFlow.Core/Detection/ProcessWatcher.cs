using System.Runtime.InteropServices;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Models;
using WallpaperFlow.Core.Native;

namespace WallpaperFlow.Core.Detection;

/// <summary>
/// Every 3 s takes one Toolhelp snapshot (a single cheap syscall, no per-process handles) and checks it
/// against the enabled process rules (OBS, Streamlabs, recorders, user-added games).
/// </summary>
public sealed class ProcessWatcher : IDisposable
{
    private readonly Func<DetectionSettings> _settings;
    private readonly Timer _timer;
    public ProcessRule? ActiveRule { get; private set; }
    public event Action<ProcessRule?>? Changed;

    public ProcessWatcher(Func<DetectionSettings> settings)
    {
        _settings = settings;
        _timer = new Timer(_ => Tick(), null, 500, 3000);
    }

    public static HashSet<string> RunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snap = Win32.CreateToolhelp32Snapshot(Win32.TH32CS_SNAPPROCESS, 0);
        if (snap == -1 || snap == 0) return names;
        try
        {
            var e = new Win32.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Win32.PROCESSENTRY32W>() };
            if (Win32.Process32FirstW(snap, ref e))
                do names.Add(e.szExeFile); while (Win32.Process32NextW(snap, ref e));
        }
        finally { Win32.CloseHandle(snap); }
        return names;
    }

    internal static ProcessRule? Match(IEnumerable<ProcessRule> rules, IReadOnlySet<string> running) =>
        rules.Where(r => r.Enabled && r.Action != PolicyAction.Continue && running.Contains(r.ProcessName))
             .OrderByDescending(r => r.Action).FirstOrDefault();

    private void Tick()
    {
        try
        {
            var cfg = _settings();
            ProcessRule? rule = cfg.Enabled ? Match(cfg.Rules, RunningProcessNames()) : null;
            if (!ReferenceEquals(rule, ActiveRule) && rule?.ProcessName != ActiveRule?.ProcessName)
            {
                ActiveRule = rule;
                Log.Info(rule == null ? "No rule process running" : $"Rule active: {rule.DisplayName} → {rule.Action}");
                Changed?.Invoke(rule);
            }
        }
        catch (Exception ex) { Log.Warn("Process scan failed: " + ex.Message); }
    }

    public void Dispose() => _timer.Dispose();
}
