using WallpaperFlow.Core.Models;

namespace WallpaperFlow.Core.Engine;

public enum SlotTarget { Play = 0, Pause = 1, Stop = 2 }

/// <summary>What one monitor's wallpaper should be doing and why.</summary>
public readonly record struct SlotPolicy(SlotTarget Target, bool Deep, string Reason)
{
    public static readonly SlotPolicy Playing = new(SlotTarget.Play, false, "Playing");

    /// <summary>The more restrictive of two policies (Stop &gt; Pause &gt; Play; deep beats soft).</summary>
    public static SlotPolicy Max(SlotPolicy a, SlotPolicy b)
    {
        if (a.Target != b.Target) return a.Target > b.Target ? a : b;
        if (a.Deep != b.Deep) return a.Deep ? a : b;
        return a;
    }

    public static SlotPolicy Min(SlotPolicy a, SlotPolicy b) => Max(a, b).Equals(a) ? b : a;
    public int Severity => (int)Target * 2 + (Deep ? 1 : 0);
}

/// <summary>Snapshot of everything the governor considers. Built by the policy engine; pure data so it is easy to test.</summary>
public sealed record GovernorInputs
{
    public IReadOnlyList<string> MonitorIds { get; init; } = Array.Empty<string>();
    public bool Suspended { get; init; }
    public bool SessionLocked { get; init; }
    public bool DisplayOff { get; init; }
    public bool UserPaused { get; init; }

    public FullscreenKind Fullscreen { get; init; }
    public string? FullscreenMonitorId { get; init; }
    public string? FullscreenProcess { get; init; }
    public TimeSpan GameActiveFor { get; init; }
    public bool Presentation { get; init; }
    public IReadOnlySet<string> CoveredMonitors { get; init; } = new HashSet<string>();
    public bool DesktopFocused { get; init; } = true;

    public ProcessRule? ActiveRule { get; init; }

    public bool OnBattery { get; init; }
    public int BatteryPercent { get; init; } = 100;
    public bool EnergySaver { get; init; }

    /// <summary>System-wide GPU / CPU utilisation, 0..100, excluding our own renderers when known.</summary>
    public double GpuLoad { get; init; }
    public double CpuLoad { get; init; }
}

public sealed record PolicyDecision
{
    public IReadOnlyDictionary<string, SlotPolicy> Monitors { get; init; } = new Dictionary<string, SlotPolicy>();
    /// <summary>0 = uncapped.</summary>
    public int FpsCap { get; init; }
    public QualityPreset? ForceQuality { get; init; }
    public bool HeavyEffectsAllowed { get; init; } = true;
    public double RenderScale { get; init; } = 1.0;
    public string Summary { get; init; } = "Playing";

    public SlotPolicy For(string monitorId) => Monitors.TryGetValue(monitorId, out var p) ? p : SlotPolicy.Playing;

    /// <summary>A slot spanning several monitors only pauses when every monitor it covers wants it paused.</summary>
    public SlotPolicy ForSpan(IEnumerable<string> monitorIds)
    {
        SlotPolicy? acc = null;
        foreach (var id in monitorIds) acc = acc is { } a ? SlotPolicy.Min(a, For(id)) : For(id);
        return acc ?? SlotPolicy.Playing;
    }

    /// <summary>Scalar used for hysteresis: higher = more restrictive.</summary>
    public int Restrictiveness =>
        Monitors.Values.Sum(p => p.Severity) * 1000 + (FpsCap == 0 ? 0 : 240 - Math.Min(240, FpsCap))
        + (HeavyEffectsAllowed ? 0 : 1) + (ForceQuality == QualityPreset.Performance ? 1 : 0) + (int)((1 - RenderScale) * 10);
}

/// <summary>
/// Pure decision function: (inputs, settings) → what every wallpaper should do. No I/O, no clocks.
/// Rules are applied from most to least severe and combined with <see cref="SlotPolicy.Max"/>, so a
/// weaker rule can never undo a stronger one.
/// </summary>
public static class PlaybackGovernor
{
    public static PolicyDecision Decide(GovernorInputs i, AppSettings s)
    {
        var slots = i.MonitorIds.ToDictionary(id => id, _ => SlotPolicy.Playing);
        var reasons = new List<string>();
        int fps = s.Performance.FpsLimit;
        QualityPreset? quality = s.Performance.Quality == QualityPreset.Auto ? null : s.Performance.Quality;
        bool heavy = true;
        double scale = s.Performance.RenderScale;

        void All(SlotPolicy p) { foreach (var k in slots.Keys.ToList()) slots[k] = SlotPolicy.Max(slots[k], p); }
        void One(string? id, SlotPolicy p) { if (id != null && slots.ContainsKey(id)) slots[id] = SlotPolicy.Max(slots[id], p); }
        void Cap(int f) { if (f > 0) fps = fps == 0 ? f : Math.Min(fps, f); }
        void Action(PolicyAction a, string reason, bool deep, string? monitor = null)
        {
            SlotPolicy? p = a switch
            {
                PolicyAction.Pause => new SlotPolicy(SlotTarget.Pause, deep, reason),
                PolicyAction.Stop => new SlotPolicy(SlotTarget.Stop, true, reason),
                _ => null,
            };
            if (a == PolicyAction.ReduceFps) { Cap(15); quality = QualityPreset.Performance; heavy = false; }
            if (p is { } pp) { if (monitor == null) All(pp); else One(monitor, pp); }
            if (a != PolicyAction.Continue) reasons.Add(reason);
        }

        // 1. System states where nothing is visible.
        if (i.Suspended) All(new SlotPolicy(SlotTarget.Pause, true, "System sleeping"));
        if (i.DisplayOff) All(new SlotPolicy(SlotTarget.Pause, true, "Display off"));
        if (i.SessionLocked && s.Detection.PauseOnLock) All(new SlotPolicy(SlotTarget.Pause, true, "Session locked"));
        if (i.Suspended || i.DisplayOff || (i.SessionLocked && s.Detection.PauseOnLock)) reasons.Add("Nothing visible");

        // 2. Explicit user pause (soft, so resume is instant).
        if (i.UserPaused) { All(new SlotPolicy(SlotTarget.Pause, false, "Paused by you")); reasons.Add("Paused by you"); }

        if (s.Detection.Enabled)
        {
            // 3. Process rules (streaming / recording software).
            if (i.ActiveRule is { Enabled: true } rule)
                Action(rule.Action, $"{rule.DisplayName} running", deep: true);

            // 4. Games: GPU is shared, so the action applies to every monitor.
            if (i.Fullscreen == FullscreenKind.Game)
            {
                var action = s.Detection.GamingAction;
                if (action == PolicyAction.Pause && s.Detection.EscalateToStopAfterSeconds > 0 &&
                    i.GameActiveFor.TotalSeconds >= s.Detection.EscalateToStopAfterSeconds)
                    action = PolicyAction.Stop;
                var name = string.IsNullOrEmpty(i.FullscreenProcess) ? "Game" : Path.GetFileNameWithoutExtension(i.FullscreenProcess);
                Action(action, $"Gaming mode ({name})", deep: true);
            }

            // 5. Presentations.
            if (i.Presentation || i.Fullscreen == FullscreenKind.Presentation)
                Action(s.Detection.PresentationAction, "Presentation", deep: true);

            // 6. Fullscreen video only affects the monitor it is on.
            if (i.Fullscreen == FullscreenKind.Video)
                Action(s.Detection.FullscreenVideoAction, "Fullscreen video", deep: true, i.FullscreenMonitorId);
            else if (i.Fullscreen == FullscreenKind.Other)
                One(i.FullscreenMonitorId, new SlotPolicy(SlotTarget.Pause, true, "Fullscreen app"));

            // 7. Monitors hidden behind maximised windows.
            if (s.Detection.PauseWhenCovered)
                foreach (var m in i.CoveredMonitors) One(m, new SlotPolicy(SlotTarget.Pause, true, "Covered by windows"));
        }

        // 8. Battery saver.
        if (s.Battery.Enabled && i.OnBattery)
        {
            if (s.Battery.PauseWhenUnplugged)
                Action(PolicyAction.Pause, "On battery", deep: true);
            else if (i.BatteryPercent <= s.Battery.PauseBelowPercent)
                Action(PolicyAction.Pause, $"Battery {i.BatteryPercent}%", deep: true);
            else
            {
                Cap(s.Battery.ReducedFps);
                quality = QualityPreset.Performance;
                if (s.Battery.DisableHeavyEffects) heavy = false;
                reasons.Add("Battery saver");
            }
        }
        if (s.Battery.Enabled && i.EnergySaver && s.Battery.PauseOnEnergySaver)
            Action(PolicyAction.Pause, "Energy saver", deep: true);

        // 9. Adaptive rendering when the GPU/CPU is busy with something else.
        if (s.Performance.AdaptiveRendering &&
            (i.GpuLoad >= s.Performance.GpuBusyThreshold || i.CpuLoad >= s.Performance.CpuBusyThreshold))
        {
            Cap(24);
            quality = QualityPreset.Performance;
            heavy = false;
            if (s.Performance.SmartResolution) scale = Math.Min(scale, 0.75);
            reasons.Add("System busy");
        }

        // 10. Dynamic FPS: while you work in another app, the desktop is mostly hidden.
        if (s.Performance.DynamicFps && !i.DesktopFocused) Cap(s.Performance.BackgroundFps);

        var summary = reasons.Count > 0 ? string.Join(" · ", reasons.Distinct())
            : slots.Values.All(p => p.Target == SlotTarget.Play) ? "Playing"
            : slots.Values.All(p => p.Target != SlotTarget.Play) ? "Paused while the desktop is covered"
            : "Paused on covered displays";
        return new PolicyDecision { Monitors = slots, FpsCap = fps, ForceQuality = quality, HeavyEffectsAllowed = heavy, RenderScale = scale, Summary = summary };
    }
}
