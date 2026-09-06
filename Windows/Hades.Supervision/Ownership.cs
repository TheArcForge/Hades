namespace Hades.Supervision;

/// <summary>
/// Whether this process started the current core, or found one already running.
///
/// <see cref="Adopted"/> is the load-bearing case: the app does not own that core's lifecycle, so
/// quitting must never kill it — and an adopted core is never assigned to the Job Object either,
/// or kill-on-close would violate that contract on exit.
/// </summary>
public enum Ownership
{
    Adopted,
    Spawned,
}
