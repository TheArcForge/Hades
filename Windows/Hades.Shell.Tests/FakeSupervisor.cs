using Hades.Supervision;

namespace Hades.Shell.Tests;

/// <summary>
/// A supervisor whose state the test sets directly. Shared by the tray and main-window view model
/// tests: both own a poll loop that calls <see cref="RefreshAsync"/>, and both need to be driven
/// through supervision states no real process would reach on command.
/// </summary>
sealed class FakeSupervisor : ICoreSupervisor
{
    public SupervisorState State { get; set; } = SupervisorState.NotStarted;
    public int RefreshCount { get; private set; }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        RefreshCount++;
        return Task.CompletedTask;
    }
}
