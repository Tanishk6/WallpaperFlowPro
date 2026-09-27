using WallpaperFlow.Core.Infrastructure;

namespace WallpaperFlow.Core.Engine;

/// <summary>
/// Re-evaluates the governor whenever an input changes (plus a 2 s safety tick) and applies the result.
/// Restrictions apply immediately; relaxations wait 600 ms so alt-tabbing through a game or a window
/// briefly uncovering the desktop doesn't thrash renderers.
/// </summary>
public sealed class PolicyEngine : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly Func<GovernorInputs> _gather;
    private readonly Func<PolicyDecision, Task> _apply;
    private readonly Timer _tick;
    private readonly Timer _relax;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PolicyDecision? _applied;
    private int _pending;

    public static readonly TimeSpan RelaxDelay = TimeSpan.FromMilliseconds(600);
    public PolicyDecision? Current => _applied;
    public GovernorInputs? LastInputs { get; private set; }
    public event Action<PolicyDecision>? DecisionApplied;

    public PolicyEngine(SettingsStore settings, Func<GovernorInputs> gather, Func<PolicyDecision, Task> apply)
    {
        _settings = settings;
        _gather = gather;
        _apply = apply;
        _tick = new Timer(_ => Invalidate(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        _relax = new Timer(_ => _ = EvaluateAsync(allowRelax: true), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Call from any thread when an input may have changed.</summary>
    public void Invalidate()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            Interlocked.Exchange(ref _pending, 0);
            await EvaluateAsync(allowRelax: false).ConfigureAwait(false);
        });
    }

    public Task ForceAsync() => EvaluateAsync(allowRelax: true);

    private async Task EvaluateAsync(bool allowRelax)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var inputs = _gather();
            LastInputs = inputs;
            var decision = PlaybackGovernor.Decide(inputs, _settings.Current);
            if (_applied != null && Same(decision, _applied)) return;

            bool relaxing = _applied != null && decision.Restrictiveness < _applied.Restrictiveness;
            if (relaxing && !allowRelax)
            {
                _relax.Change(RelaxDelay, Timeout.InfiniteTimeSpan);
                return;
            }
            _relax.Change(Timeout.Infinite, Timeout.Infinite);
            _applied = decision;
            Log.Debug("Policy: " + decision.Summary + $" (fps {decision.FpsCap})");
            await _apply(decision).ConfigureAwait(false);
            DecisionApplied?.Invoke(decision);
        }
        catch (Exception ex) { Log.Error("Policy evaluation failed", ex); }
        finally { _gate.Release(); }
    }

    private static bool Same(PolicyDecision a, PolicyDecision b) =>
        a.FpsCap == b.FpsCap && a.ForceQuality == b.ForceQuality && a.HeavyEffectsAllowed == b.HeavyEffectsAllowed &&
        Math.Abs(a.RenderScale - b.RenderScale) < 0.001 && a.Monitors.Count == b.Monitors.Count &&
        a.Monitors.All(kv => b.Monitors.TryGetValue(kv.Key, out var v) && v.Equals(kv.Value));

    public void Dispose()
    {
        _tick.Dispose();
        _relax.Dispose();
    }
}
