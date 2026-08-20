namespace Hades.Core.Graph;

/// <summary>One asset reached while walking `references` edges outward from a root — see
/// <see cref="GraphDatabase.TraceDependencies"/>.</summary>
public sealed record DependencyHit
{
    public required string Path { get; init; }

    /// <summary>Hops from the root. 1 = a direct dependency.</summary>
    public required int Depth { get; init; }
}
