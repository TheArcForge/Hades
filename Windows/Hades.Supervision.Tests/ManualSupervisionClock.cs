namespace Hades.Supervision.Tests;

/// <summary>
/// The test double for <see cref="ISupervisionClock"/>. Two things make CoreSupervisorTests fast
/// and deterministic without ever being disconnected from reality:
///
///  1. <see cref="DelayAsync"/> never actually waits - backoff and ping-poll sleeps cost no real
///     time at all, regardless of the requested duration. This is what lets tests exercise
///     "1/2/4/8/16 second backoff" and "MaxRestartAttempts spawn attempts" without the test suite
///     itself taking tens of seconds.
///  2. <see cref="UtcNow"/> still tracks REAL wall time, plus a manually-controlled
///     <see cref="Advance"/> offset. A deadline computed from real elapsed time (e.g.
///     <c>Configuration.PingTimeout</c>) still expires correctly if something genuinely hangs - the
///     clock is never frozen, so a bug can never make a test loop forever. <see cref="Advance"/>
///     exists purely so a test can make "this core has been running for &gt;= MinimumStableUptime"
///     true an instant after a real, fast spawn succeeds, without an actual multi-second sleep -
///     see CoreSupervisorTests.Death_After_MinimumStableUptime_Resets_Attempt_Budget for the one
///     test that needs this.
/// </summary>
internal sealed class ManualSupervisionClock : ISupervisionClock
{
    private TimeSpan _offset = TimeSpan.Zero;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + _offset;

    public void Advance(TimeSpan by) => _offset += by;

    /// <summary>Always waits ~1ms regardless of <paramref name="duration"/> - "instant" in the sense
    /// that matters (a 16s backoff costs the test ~1ms, not 16s), but still a real yield rather than
    /// a synchronously-completed <see cref="Task"/>. A synchronously-completed delay would make the
    /// caller's poll loop (see <c>CoreSupervisor.SpawnOnceAsync</c>) spin as a tight, non-yielding
    /// CPU loop instead of a poll - functionally harmless, but needlessly wasteful.</summary>
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        Task.Delay(1, cancellationToken);
}
