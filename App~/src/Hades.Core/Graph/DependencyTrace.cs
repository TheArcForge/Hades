namespace Hades.Core.Graph;

/// <summary>
/// The full result of one <see cref="GraphDatabase.TraceDependencies"/> walk: every resolvable
/// dependency found (<see cref="Hits"/>, unchanged from before F6-honesty) plus every dangling one
/// (<see cref="Dangling"/>) — a `references` edge whose target GUID has no node, reported instead
/// of silently dropped. See <see cref="DanglingDependency"/>'s own class doc comment for exactly
/// what that means and why it happens routinely, not just on a broken project.
/// </summary>
public sealed record DependencyTrace
{
    public required IReadOnlyList<DependencyHit> Hits { get; init; }
    public required IReadOnlyList<DanglingDependency> Dangling { get; init; }
}
