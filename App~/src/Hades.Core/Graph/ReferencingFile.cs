namespace Hades.Core.Graph;

/// <summary>
/// One file that references a given asset, with how many times and in what ways.
///
/// Grouping by file is the shape the question actually has. A prefab used 144 times across two
/// scenes is far better read as "two scenes, 144 uses" than as 144 near-identical rows — and it
/// makes truncation mostly moot, since the file count is small even when the reference count is
/// large.
/// </summary>
public sealed record ReferencingFile
{
    public required string Path { get; init; }

    /// <summary>How many individual references this file makes to the asset.</summary>
    public required int References { get; init; }

    /// <summary>Distinct relationship kinds — "instance_of", "references", "corresponds_to".</summary>
    public required IReadOnlyList<string> Relationships { get; init; }

    /// <summary>A representative property path, so the caller can see how it is used without
    /// pulling every row.</summary>
    public required string SampleVia { get; init; }
}
