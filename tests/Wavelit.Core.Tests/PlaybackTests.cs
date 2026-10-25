using Wavelit.Core.Desktop;
using Wavelit.Core.Interop;
using Wavelit.Core.Playback;

namespace Wavelit.Core.Tests;

public class OcclusionEvaluatorTests
{
    private static readonly MonitorInfo Left = new("L", @"\\.\DISPLAY1", 0, new RECT(0, 0, 1920, 1080), new RECT(0, 0, 1920, 1040), true);
    private static readonly MonitorInfo Right = new("R", @"\\.\DISPLAY2", 1, new RECT(1920, -638, 3000, 1282), new RECT(1920, -638, 3000, 1242), false);
    private static readonly MonitorInfo[] Both = [Left, Right];

    [Fact]
    public void Borderless_fullscreen_window_covers_only_its_monitor()
    {
        var result = OcclusionEvaluator.Evaluate([new WindowSnapshot(new RECT(1920, -638, 3000, 1282), false, "Game")], Both);
        Assert.Equal(["R"], result.FullscreenMonitors);
        Assert.Empty(result.MaximizedMonitors);
    }

    [Fact]
    public void Window_larger_than_monitor_counts_as_fullscreen()
    {
        // Exclusive-style windows often extend a few pixels past the monitor edges.
        var result = OcclusionEvaluator.Evaluate([new WindowSnapshot(new RECT(-8, -8, 1928, 1088), false, "Game")], Both);
        Assert.Contains("L", result.FullscreenMonitors);
    }

    [Fact]
    public void Normal_window_covers_nothing()
    {
        var result = OcclusionEvaluator.Evaluate([new WindowSnapshot(new RECT(100, 100, 900, 700), false, "Notepad")], Both);
        Assert.Empty(result.FullscreenMonitors);
        Assert.Empty(result.MaximizedMonitors);
    }

    [Fact]
    public void Maximized_window_is_attributed_to_the_monitor_it_mostly_overlaps()
    {
        var result = OcclusionEvaluator.Evaluate([new WindowSnapshot(new RECT(1912, -8, 3008, 1250), true, "Code")], Both);
        Assert.Equal(["R"], result.MaximizedMonitors);
        Assert.Empty(result.FullscreenMonitors);
    }

    [Fact]
    public void Window_spanning_both_monitors_covers_both()
    {
        var result = OcclusionEvaluator.Evaluate([new WindowSnapshot(new RECT(0, -638, 3000, 1282), false, "Span")], Both);
        Assert.Equal(2, result.FullscreenMonitors.Count);
    }

    [Fact]
    public void Empty_rect_is_ignored()
    {
        var result = OcclusionEvaluator.Evaluate([new WindowSnapshot(new RECT(0, 0, 0, 0), true, "Ghost")], Both);
        Assert.Empty(result.FullscreenMonitors);
        Assert.Empty(result.MaximizedMonitors);
    }
}

public class PlaybackPlannerTests
{
    private static readonly PlaybackRules Defaults = new();

    private static SystemConditions Covering(string[] fullscreen, string[]? maximized = null) => new()
    {
        Occlusion = new OcclusionResult(new HashSet<string>(fullscreen), new HashSet<string>(maximized ?? [])),
    };

    [Fact]
    public void Plays_when_nothing_is_going_on()
        => Assert.Equal(PlaybackState.Playing, PlaybackPlanner.Decide(["L"], new SystemConditions(), Defaults).State);

    [Fact]
    public void Fullscreen_monitor_is_suspended_and_other_monitor_frozen()
    {
        var c = Covering(["L"]);
        Assert.Equal(PlaybackState.Suspended, PlaybackPlanner.Decide(["L"], c, Defaults).State);
        Assert.Equal(PlaybackState.Frozen, PlaybackPlanner.Decide(["R"], c, Defaults).State);
    }

    [Fact]
    public void Other_monitor_keeps_playing_when_pause_all_is_off()
    {
        var rules = Defaults with { PauseAllDisplaysOnFullscreen = false };
        Assert.Equal(PlaybackState.Playing, PlaybackPlanner.Decide(["R"], Covering(["L"]), rules).State);
    }

    [Fact]
    public void Fullscreen_rule_can_be_disabled()
    {
        var rules = Defaults with { PauseOnFullscreen = false };
        Assert.Equal(PlaybackState.Playing, PlaybackPlanner.Decide(["L"], Covering(["L"]), rules).State);
    }

    [Fact]
    public void Maximized_window_suspends_only_its_monitor()
    {
        var c = Covering([], ["L"]);
        Assert.Equal(PlaybackState.Suspended, PlaybackPlanner.Decide(["L"], c, Defaults).State);
        Assert.Equal(PlaybackState.Playing, PlaybackPlanner.Decide(["R"], c, Defaults).State);
    }

    [Fact]
    public void Span_host_is_suspended_only_when_every_monitor_is_hidden()
    {
        Assert.NotEqual(PlaybackState.Suspended, PlaybackPlanner.Decide(["L", "R"], Covering([], ["L"]), Defaults).State);
        Assert.Equal(PlaybackState.Suspended, PlaybackPlanner.Decide(["L", "R"], Covering([], ["L", "R"]), Defaults).State);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Lock_display_off_and_exclusive_fullscreen_suspend_everything(bool locked, bool displayOff, bool exclusive)
    {
        var c = new SystemConditions { SessionLocked = locked, DisplayOff = displayOff, ExclusiveFullscreen = exclusive };
        Assert.Equal(PlaybackState.Suspended, PlaybackPlanner.Decide(["L"], c, Defaults).State);
    }

    [Fact]
    public void User_pause_freezes_but_fullscreen_still_suspends()
    {
        Assert.Equal(PlaybackState.Frozen, PlaybackPlanner.Decide(["L"], new SystemConditions { UserPaused = true }, Defaults).State);
        var c = Covering(["L"]) with { UserPaused = true };
        Assert.Equal(PlaybackState.Suspended, PlaybackPlanner.Decide(["L"], c, Defaults).State);
    }

    [Fact]
    public void Battery_rules_respect_settings()
    {
        var onBattery = new SystemConditions { OnBattery = true };
        Assert.Equal(PlaybackState.Playing, PlaybackPlanner.Decide(["L"], onBattery, Defaults).State);
        Assert.Equal(PlaybackState.Frozen, PlaybackPlanner.Decide(["L"], onBattery, Defaults with { PauseOnBattery = true }).State);
        Assert.Equal(PlaybackState.Frozen, PlaybackPlanner.Decide(["L"], new SystemConditions { BatterySaver = true }, Defaults).State);
    }
}
