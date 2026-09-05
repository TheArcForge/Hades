namespace Hades.Supervision;

public enum SupervisorStateKind
{
    NotStarted,
    Starting,
    Running,
    Restarting,
    Failed,
}

/// <summary>
/// Mirrors Swift's <c>CoreSupervisor.State</c> enum
/// (Mac/HadesSupervision/Sources/HadesSupervision/CoreSupervisor.swift) case for case - see that
/// type's own doc comments for what each case means and exactly when it's reached.
///
/// C# has no native tagged-union/associated-value enum, so this is a small value type carrying
/// only the payload the active <see cref="Kind"/> actually uses (<see cref="Ownership"/> for
/// <see cref="SupervisorStateKind.Running"/>, <see cref="Attempt"/> for
/// <see cref="SupervisorStateKind.Restarting"/>/<see cref="SupervisorStateKind.Failed"/>) -
/// equatable and printable like the Swift original (a <c>record struct</c> gets both for free) so
/// tests can assert on it directly, e.g. <c>Assert.Equal(SupervisorState.Running(Ownership.Adopted), supervisor.State)</c>.
/// </summary>
public readonly record struct SupervisorState
{
    public SupervisorStateKind Kind { get; }
    public Ownership? Ownership { get; }
    public int Attempt { get; }

    private SupervisorState(SupervisorStateKind kind, Ownership? ownership, int attempt)
    {
        Kind = kind;
        Ownership = ownership;
        Attempt = attempt;
    }

    public static readonly SupervisorState NotStarted = new(SupervisorStateKind.NotStarted, null, 0);
    public static readonly SupervisorState Starting = new(SupervisorStateKind.Starting, null, 0);

    public static SupervisorState Running(Ownership ownership) =>
        new(SupervisorStateKind.Running, ownership, 0);

    /// <param name="attempt">1-based; counts the spawn attempt currently being waited on - matches
    /// Swift's own <c>restarting(attempt:)</c> doc comment exactly.</param>
    public static SupervisorState Restarting(int attempt) =>
        new(SupervisorStateKind.Restarting, null, attempt);

    /// <param name="attempts">How many spawn attempts were made before giving up.</param>
    public static SupervisorState Failed(int attempts) =>
        new(SupervisorStateKind.Failed, null, attempts);

    public override string ToString() => Kind switch
    {
        SupervisorStateKind.Running => $"Running({Ownership})",
        SupervisorStateKind.Restarting => $"Restarting(attempt: {Attempt})",
        SupervisorStateKind.Failed => $"Failed(attempts: {Attempt})",
        _ => Kind.ToString(),
    };
}
