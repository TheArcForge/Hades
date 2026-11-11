namespace Hades.Core.Observation;

/// <summary>
/// What a file looked like when it was last indexed. Comparing this against disk is how "what
/// changed" is answered without re-reading every file.
/// </summary>
public sealed record FileState
{
    public required string Path { get; init; }
    public required long MTimeUtcMs { get; init; }
    public required long Size { get; init; }

    /// <summary>DESIGN-07 §7.3 (I4 fix) — the sibling <c>.meta</c>'s own last-write time, tracked
    /// SEPARATELY from <see cref="MTimeUtcMs"/> on purpose: that column is also the sort key
    /// <c>get_recently_changed</c> exposes to callers, and folding a <c>.meta</c> edit into it
    /// would pull an asset's reported "last changed" time forward independent of its own content
    /// ever changing — the exact defect the first, reverted attempt at this fix produced (see
    /// <c>Observation.IncrementalIndexTests</c>'s own historical note). Null for a path outside
    /// the scope <see cref="Observation.ProjectSweeper"/> tracks a sibling .meta for (a script or
    /// binary/imported asset — see that type's own doc comment), and for one with no .meta yet.
    /// </summary>
    public long? MetaMTimeUtcMs { get; init; }

    /// <summary>Sibling of <see cref="MetaMTimeUtcMs"/>: the .meta's own size, the same
    /// mtime-granularity hedge <see cref="Size"/> already is for the asset's own content.</summary>
    public long? MetaSize { get; init; }
}
