namespace Wavelit.Core.Playback;

public enum PlaybackState
{
    /// <summary>Rendering normally.</summary>
    Playing,
    /// <summary>Visible but frozen on the current frame (user pause, battery, another display is fullscreen…).</summary>
    Frozen,
    /// <summary>Not visible at all: renderer is hidden and its processes suspended (≈0% CPU/GPU).</summary>
    Suspended,
}

/// <param name="Covered">Suspended only because windows cover it (they can move away at any moment).</param>
public sealed record PlaybackDecision(PlaybackState State, string Reason, bool Covered = false)
{
    public static readonly PlaybackDecision Play = new(PlaybackState.Playing, "Playing");
}

/// <summary>
/// A covered wallpaper is first only Frozen (frame kept on screen, render loop stopped: about 0% GPU) and is fully
/// Suspended once it has stayed covered for <see cref="Delay"/>. Suspending hides the renderer, so a window that
/// un-maximizes right away would otherwise reveal Explorer's static wallpaper for a moment (round 8, §23.2).
/// </summary>
public static class CoverGrace
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(15);

    /// <param name="coveredSince">When this host became covered (null = not covered before this decision).</param>
    public static PlaybackDecision Apply(PlaybackDecision decision, DateTime? coveredSince, DateTime now)
        => decision is { Covered: true, State: PlaybackState.Suspended } && (coveredSince is null || now - coveredSince.Value < Delay)
            ? decision with { State = PlaybackState.Frozen }
            : decision;
}

/// <summary>Snapshot of everything that affects whether wallpapers should render.</summary>
public sealed record SystemConditions
{
    public OcclusionResult Occlusion { get; init; } = OcclusionResult.None;
    public bool ExclusiveFullscreen { get; init; }
    public bool PresentationMode { get; init; }
    public bool SessionLocked { get; init; }
    public bool DisplayOff { get; init; }
    public bool OnBattery { get; init; }
    public bool BatterySaver { get; init; }
    public bool RemoteSession { get; init; }
    public bool UserPaused { get; init; }
}

public sealed record PlaybackRules
{
    public bool PauseOnFullscreen { get; init; } = true;
    public bool PauseOnMaximized { get; init; } = true;
    public bool PauseAllDisplaysOnFullscreen { get; init; } = true;
    public bool PauseOnBattery { get; init; }
    public bool PauseOnBatterySaver { get; init; } = true;
    public bool PauseOnRemoteSession { get; init; } = true;
}

/// <summary>Pure decision logic: given system conditions, what should each wallpaper host do?</summary>
public static class PlaybackPlanner
{
    /// <param name="hostMonitors">Monitor ids each host covers (one per monitor, or several when spanning).</param>
    public static PlaybackDecision Decide(IReadOnlyCollection<string> hostMonitors, SystemConditions c, PlaybackRules rules)
    {
        if (c.DisplayOff) return new(PlaybackState.Suspended, "Display is off");
        if (c.SessionLocked) return new(PlaybackState.Suspended, "PC is locked");
        if (rules.PauseOnFullscreen && c.ExclusiveFullscreen) return new(PlaybackState.Suspended, "Fullscreen game running");

        if (hostMonitors.Count > 0 && hostMonitors.All(m => IsHidden(m, c, rules)))
        {
            bool fs = hostMonitors.Any(c.Occlusion.FullscreenMonitors.Contains);
            return new(PlaybackState.Suspended, fs ? "Fullscreen app running" : "Covered by a maximized window", Covered: true);
        }

        if (c.UserPaused) return new(PlaybackState.Frozen, "Paused");

        if (rules.PauseOnFullscreen && rules.PauseAllDisplaysOnFullscreen && c.Occlusion.FullscreenMonitors.Count > 0)
            return new(PlaybackState.Frozen, "Fullscreen app on another display");

        if (c.PresentationMode) return new(PlaybackState.Frozen, "Presentation mode");
        if (rules.PauseOnBatterySaver && c.BatterySaver) return new(PlaybackState.Frozen, "Battery saver is on");
        if (rules.PauseOnBattery && c.OnBattery) return new(PlaybackState.Frozen, "Running on battery");
        if (rules.PauseOnRemoteSession && c.RemoteSession) return new(PlaybackState.Frozen, "Remote desktop session");

        return PlaybackDecision.Play;
    }

    private static bool IsHidden(string monitorId, SystemConditions c, PlaybackRules rules)
        => (rules.PauseOnFullscreen && c.Occlusion.FullscreenMonitors.Contains(monitorId))
        || (rules.PauseOnMaximized && c.Occlusion.MaximizedMonitors.Contains(monitorId));
}
