using WallpaperFlow.Core.Engine;
using WallpaperFlow.Core.Models;
using Xunit;

namespace WallpaperFlow.Core.Tests;

public class GovernorTests
{
    private static AppSettings Settings()
    {
        var s = new AppSettings();
        s.Performance.FpsLimit = 60;
        s.Performance.BackgroundFps = 24;
        s.Detection.EscalateToStopAfterSeconds = 300;
        s.Battery.PauseWhenUnplugged = false;
        return s;
    }

    private static GovernorInputs Inputs(params string[] monitors) => new() { MonitorIds = monitors.Length == 0 ? new[] { "A", "B" } : monitors };

    [Fact]
    public void Idle_desktop_plays_everything_at_the_configured_rate()
    {
        var d = PlaybackGovernor.Decide(Inputs(), Settings());
        Assert.All(d.Monitors.Values, p => Assert.Equal(SlotTarget.Play, p.Target));
        Assert.Equal(60, d.FpsCap);
        Assert.True(d.HeavyEffectsAllowed);
        Assert.Equal("Playing", d.Summary);
    }

    [Fact]
    public void Game_pauses_every_display_deeply()
    {
        var d = PlaybackGovernor.Decide(Inputs() with { Fullscreen = FullscreenKind.Game, FullscreenMonitorId = "A", FullscreenProcess = "game.exe", DesktopFocused = false }, Settings());
        Assert.All(d.Monitors.Values, p => { Assert.Equal(SlotTarget.Pause, p.Target); Assert.True(p.Deep); });
        Assert.Contains("Gaming mode", d.Summary);
    }

    [Fact]
    public void Long_game_session_escalates_to_stop()
    {
        var d = PlaybackGovernor.Decide(Inputs() with { Fullscreen = FullscreenKind.Game, GameActiveFor = TimeSpan.FromMinutes(6) }, Settings());
        Assert.All(d.Monitors.Values, p => Assert.Equal(SlotTarget.Stop, p.Target));
    }

    [Fact]
    public void Fullscreen_video_only_affects_its_monitor()
    {
        var d = PlaybackGovernor.Decide(Inputs() with { Fullscreen = FullscreenKind.Video, FullscreenMonitorId = "B" }, Settings());
        Assert.Equal(SlotTarget.Play, d.For("A").Target);
        Assert.Equal(SlotTarget.Pause, d.For("B").Target);
    }

    [Fact]
    public void Covered_monitor_pauses_and_span_needs_all_monitors_paused()
    {
        var d = PlaybackGovernor.Decide(Inputs() with { CoveredMonitors = new HashSet<string> { "A" } }, Settings());
        Assert.Equal(SlotTarget.Pause, d.For("A").Target);
        Assert.Equal(SlotTarget.Play, d.For("B").Target);
        Assert.Equal(SlotTarget.Play, d.ForSpan(new[] { "A", "B" }).Target);

        var both = PlaybackGovernor.Decide(Inputs() with { CoveredMonitors = new HashSet<string> { "A", "B" } }, Settings());
        Assert.Equal(SlotTarget.Pause, both.ForSpan(new[] { "A", "B" }).Target);
    }

    [Fact]
    public void User_pause_is_soft()
    {
        var d = PlaybackGovernor.Decide(Inputs() with { UserPaused = true }, Settings());
        Assert.All(d.Monitors.Values, p => { Assert.Equal(SlotTarget.Pause, p.Target); Assert.False(p.Deep); });
    }

    [Fact]
    public void Battery_caps_fps_and_drops_heavy_effects()
    {
        var d = PlaybackGovernor.Decide(Inputs() with { OnBattery = true, BatteryPercent = 80 }, Settings());
        Assert.Equal(15, d.FpsCap);
        Assert.False(d.HeavyEffectsAllowed);
        Assert.Equal(QualityPreset.Performance, d.ForceQuality);
        Assert.All(d.Monitors.Values, p => Assert.Equal(SlotTarget.Play, p.Target));
    }

    [Fact]
    public void Low_battery_pauses()
    {
        var d = PlaybackGovernor.Decide(Inputs() with { OnBattery = true, BatteryPercent = 10 }, Settings());
        Assert.All(d.Monitors.Values, p => Assert.Equal(SlotTarget.Pause, p.Target));
    }

    [Fact]
    public void Busy_gpu_triggers_adaptive_rendering()
    {
        var d = PlaybackGovernor.Decide(Inputs() with { GpuLoad = 95 }, Settings());
        Assert.Equal(24, d.FpsCap);
        Assert.False(d.HeavyEffectsAllowed);
        Assert.True(d.RenderScale <= 0.75);
    }

    [Fact]
    public void Working_in_another_app_lowers_fps()
    {
        var d = PlaybackGovernor.Decide(Inputs() with { DesktopFocused = false }, Settings());
        Assert.Equal(24, d.FpsCap);
    }

    [Fact]
    public void Process_rule_applies_its_action()
    {
        var rule = new ProcessRule { ProcessName = "obs64.exe", DisplayName = "OBS Studio", Action = PolicyAction.Stop, Enabled = true };
        var d = PlaybackGovernor.Decide(Inputs() with { ActiveRule = rule }, Settings());
        Assert.All(d.Monitors.Values, p => Assert.Equal(SlotTarget.Stop, p.Target));
    }

    [Fact]
    public void Detection_can_be_switched_off()
    {
        var s = Settings();
        s.Detection.Enabled = false;
        var d = PlaybackGovernor.Decide(Inputs() with { Fullscreen = FullscreenKind.Game, CoveredMonitors = new HashSet<string> { "A" } }, s);
        Assert.All(d.Monitors.Values, p => Assert.Equal(SlotTarget.Play, p.Target));
    }

    [Fact]
    public void Max_prefers_the_more_restrictive_policy()
    {
        var play = SlotPolicy.Playing;
        var soft = new SlotPolicy(SlotTarget.Pause, false, "a");
        var deep = new SlotPolicy(SlotTarget.Pause, true, "b");
        var stop = new SlotPolicy(SlotTarget.Stop, true, "c");
        Assert.Equal(soft, SlotPolicy.Max(play, soft));
        Assert.Equal(deep, SlotPolicy.Max(soft, deep));
        Assert.Equal(stop, SlotPolicy.Max(deep, stop));
        Assert.Equal(play, SlotPolicy.Min(play, stop));
    }

    [Fact]
    public void Restrictiveness_orders_decisions()
    {
        var s = Settings();
        var idle = PlaybackGovernor.Decide(Inputs(), s);
        var game = PlaybackGovernor.Decide(Inputs() with { Fullscreen = FullscreenKind.Game }, s);
        Assert.True(game.Restrictiveness > idle.Restrictiveness);
    }
}
