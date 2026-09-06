namespace Hades.Supervision;

/// <summary>
/// The part of <see cref="CoreSupervisor"/> a UI needs: what state the core is in, and a way to ask
/// it to re-check. The port of Swift's <c>CoreSupervising</c> protocol
/// (Mac/HadesSupervision), and it exists for the same reason - so a view model can be driven
/// through every supervision state in a unit test without spawning a real process.
///
/// Deliberately does NOT expose Start/Stop. Those are lifecycle, owned by whatever composed the
/// supervisor in the first place; a view model that could stop the core would be making a decision
/// rather than rendering one.
/// </summary>
public interface ICoreSupervisor
{
    /// <summary>The current state. Cheap, and safe to read from any thread.</summary>
    SupervisorState State { get; }

    /// <summary>
    /// Re-checks liveness and updates <see cref="State"/>, restarting a dead spawned core if the
    /// attempt budget allows. <see cref="CoreSupervisor"/> runs no timer of its own by design -
    /// callers drive the cadence.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
