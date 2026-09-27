using System.Diagnostics;
using System.Text.RegularExpressions;
using WallpaperFlow.Core.Infrastructure;
using WallpaperFlow.Core.Native;

namespace WallpaperFlow.Core.Performance;

public sealed record PerfSample(DateTime Time, double SystemCpu, double AppCpu, double AppRamMb, double SystemGpu, double AppGpu,
    double AppGpuMemMb, double? TemperatureC, double Fps)
{
    /// <summary>GPU load caused by everything except our renderers (what adaptive rendering reacts to).</summary>
    public double OtherGpu => Math.Max(0, SystemGpu - AppGpu);
    public double OtherCpu => Math.Max(0, SystemCpu - AppCpu);
}

/// <summary>
/// CPU (GetSystemTimes + per-process times), RAM (working set of the app and its renderers), GPU engine
/// utilisation and dedicated memory (PDH "GPU Engine" / "GPU Process Memory", the same counters Task
/// Manager uses) and temperature (ACPI thermal zones, when the firmware exposes them).
/// Samples every 5 s in the background; every second while the performance page is open.
/// </summary>
public sealed partial class MetricsService : IDisposable
{
    private readonly Func<IReadOnlyList<int>> _rendererPids;
    private readonly Func<double> _fps;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly LinkedList<PerfSample> _history = new();
    private readonly Dictionary<int, TimeSpan> _lastCpu = new();
    private Win32.FILETIME _lastIdle, _lastKernel, _lastUser;
    private DateTime _lastSample = DateTime.UtcNow;
    private nint _query, _gpuEngine, _gpuMem, _thermal;
    private bool _pdhReady;
    private bool _fast;

    public const int HistoryLength = 120;
    public PerfSample? Latest { get; private set; }
    public event Action<PerfSample>? Sampled;

    public MetricsService(Func<IReadOnlyList<int>> rendererPids, Func<double> fps)
    {
        _rendererPids = rendererPids;
        _fps = fps;
        Win32.GetSystemTimes(out _lastIdle, out _lastKernel, out _lastUser);
        _timer = new Timer(_ => Sample(), null, 2000, 5000);
    }

    public IReadOnlyList<PerfSample> History { get { lock (_gate) return _history.ToList(); } }

    public void SetFastMode(bool fast)
    {
        if (_fast == fast) return;
        _fast = fast;
        _timer.Change(fast ? 200 : 5000, fast ? 1000 : 5000);
    }

    private void EnsurePdh()
    {
        if (_pdhReady || _query != 0) return;
        try
        {
            if (Pdh.PdhOpenQuery(null, 0, out _query) != 0) { _query = 0; return; }
            Pdh.PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", 0, out _gpuEngine);
            Pdh.PdhAddEnglishCounter(_query, @"\GPU Process Memory(*)\Dedicated Usage", 0, out _gpuMem);
            Pdh.PdhAddEnglishCounter(_query, @"\Thermal Zone Information(*)\Temperature", 0, out _thermal);
            Pdh.PdhCollectQueryData(_query); // first collection primes rate counters
            _pdhReady = true;
        }
        catch (Exception ex) { Log.Warn("PDH unavailable: " + ex.Message); }
    }

    [GeneratedRegex(@"pid_(\d+)_.*engtype_(\w+)", RegexOptions.IgnoreCase)] private static partial Regex EngineRx();
    [GeneratedRegex(@"pid_(\d+)_", RegexOptions.IgnoreCase)] private static partial Regex PidRx();

    private void Sample()
    {
        try
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastSample).TotalSeconds;
            _lastSample = now;
            if (elapsed <= 0) return;

            // System CPU from idle/kernel/user deltas (kernel time includes idle).
            Win32.GetSystemTimes(out var idle, out var kernel, out var user);
            double di = idle.Value - _lastIdle.Value, dk = kernel.Value - _lastKernel.Value, du = user.Value - _lastUser.Value;
            _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
            double sysCpu = dk + du > 0 ? Math.Clamp((dk + du - di) / (dk + du) * 100, 0, 100) : 0;

            // App tree: this process + renderer children.
            var pids = new HashSet<int>(_rendererPids()) { Environment.ProcessId };
            double appCpu = 0, ram = 0;
            foreach (var pid in pids)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    var cpu = p.TotalProcessorTime;
                    if (_lastCpu.TryGetValue(pid, out var prev)) appCpu += (cpu - prev).TotalSeconds / elapsed / Environment.ProcessorCount * 100;
                    _lastCpu[pid] = cpu;
                    ram += p.WorkingSet64 / 1048576.0;
                }
                catch { _lastCpu.Remove(pid); }
            }
            foreach (var stale in _lastCpu.Keys.Where(k => !pids.Contains(k)).ToList()) _lastCpu.Remove(stale);

            double sysGpu = 0, appGpu = 0, gpuMem = 0;
            double? temp = null;
            EnsurePdh();
            if (_pdhReady && Pdh.PdhCollectQueryData(_query) == 0)
            {
                var perEngine = new Dictionary<string, double>();
                var perEngineApp = new Dictionary<string, double>();
                foreach (var item in Pdh.ReadArray(_gpuEngine))
                {
                    var m = EngineRx().Match(item.Name);
                    if (!m.Success) continue;
                    var eng = m.Groups[2].Value;
                    perEngine[eng] = perEngine.GetValueOrDefault(eng) + item.Value;
                    if (pids.Contains(int.Parse(m.Groups[1].Value)))
                        perEngineApp[eng] = perEngineApp.GetValueOrDefault(eng) + item.Value;
                }
                sysGpu = perEngine.Count > 0 ? Math.Min(100, perEngine.Values.Max()) : 0;
                appGpu = perEngineApp.Count > 0 ? Math.Min(100, perEngineApp.Values.Max()) : 0;
                foreach (var item in Pdh.ReadArray(_gpuMem))
                {
                    var m = PidRx().Match(item.Name);
                    if (m.Success && pids.Contains(int.Parse(m.Groups[1].Value))) gpuMem += item.Value / 1048576.0;
                }
                var zones = Pdh.ReadArray(_thermal).Select(z => z.Value).Where(k => k > 200 && k < 400).ToList();
                if (zones.Count > 0) temp = Math.Round(zones.Max() - 273.15, 1);
            }

            var sample = new PerfSample(DateTime.Now, sysCpu, Math.Clamp(appCpu, 0, 100), ram, sysGpu, appGpu, gpuMem, temp, _fps());
            lock (_gate)
            {
                _history.AddLast(sample);
                while (_history.Count > HistoryLength) _history.RemoveFirst();
            }
            Latest = sample;
            Sampled?.Invoke(sample);
        }
        catch (Exception ex) { Log.Debug("Metrics sample failed: " + ex.Message); }
    }

    public void Dispose()
    {
        _timer.Dispose();
        if (_query != 0) Pdh.PdhCloseQuery(_query);
        _query = 0;
    }
}
