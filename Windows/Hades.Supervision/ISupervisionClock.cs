namespace Hades.Supervision;

/// <summary>
/// Everything <see cref="CoreSupervisor"/> needs to know "now" and to wait - abstracted purely so
/// tests can make backoff and stable-uptime measurements instant and deterministic instead of
/// sleeping for real seconds (see the task's own "do NOT sleep 3 real seconds per attempt"
/// requirement). The Swift original gets the same effect for free because <c>ContinuousClock</c>
/// and <c>Task.sleep(for:)</c> are both ordinary, already-substitutable language features; .NET has
/// no single built-in seam for "how long has this been running" plus "wait this long" together, so
/// this interface is that seam.
/// </summary>
public interface ISupervisionClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>
/// The real clock: wall time, real waiting. Used whenever a caller does not supply one of its own
/// - i.e. every real, non-test use of <see cref="CoreSupervisor"/>.
/// </summary>
public sealed class SystemSupervisionClock : ISupervisionClock
{
    public static readonly SystemSupervisionClock Instance = new();

    private SystemSupervisionClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        Task.Delay(duration, cancellationToken);
}
