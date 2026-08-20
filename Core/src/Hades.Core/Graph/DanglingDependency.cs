namespace Hades.Core.Graph;

/// <summary>
/// One `references` or `instance_of` edge encountered while
/// <see cref="GraphDatabase.TraceDependencies"/> walks outward whose target GUID owns no node
/// anywhere in the graph — F6-honesty: not a broken walk,
/// since the edge itself is exactly as real as any other (it is a row in the `edges` table, keyed
/// by GUID, same as every resolvable dependency). Two DIFFERENT reasons produce this today, and
/// this record does not distinguish which: the target's asset kind genuinely is not one Hades
/// indexes as a node (a shrinking set now that textures, models, audio, fonts, shaders, and
/// animation clips are indexed — see <see cref="Unity.ImportedAssetKind"/>), or the target lives
/// outside every root <see cref="Indexing.ProjectWalker"/> scans at all — most commonly a registry
/// package resolved into <c>Library/PackageCache</c> (e.g. a URP built-in shader or texture),
/// which is deliberately never walked regardless of extension (see
/// <see cref="Indexing.ProjectWalker"/>'s own class doc comment: "third-party code and would
/// swamp the graph"). Before F6-honesty such an edge was silently dropped, so a material whose
/// only dependencies were a texture and a shader reported "depends on nothing" — an authoritative-
/// looking answer that was actually just a blind spot in what the graph could resolve.
///
/// One entry per distinct (<see cref="FromPath"/>, <see cref="ToGuid"/>) pair per hop — the same
/// per-file guid de-duplication <see cref="GraphDatabase"/>'s dependency walk has always applied
/// to resolvable targets, now applied identically to dangling ones. <see cref="PropertyPath"/> is
/// a sample (the first one found), matching <see cref="ReferencingFile.SampleVia"/>'s own
/// convention for the identical reason: several fields can point at the same target GUID.
/// </summary>
public sealed record DanglingDependency
{
    /// <summary>The file that holds the dangling edge — one hop closer to the traced root than
    /// <see cref="ToGuid"/>, exactly like <see cref="DependencyHit.Path"/> is for a resolvable one.</summary>
    public required string FromPath { get; init; }

    /// <summary>Hops from the trace's root to <see cref="FromPath"/> — matches
    /// <see cref="DependencyHit.Depth"/>'s own meaning: 1 = the root's own direct dependency.</summary>
    public required int Depth { get; init; }

    /// <summary>The unresolved target's raw GUID — nothing else identifies it, by definition:
    /// resolving it (via a path) is exactly what a graph node would provide and none exists.</summary>
    public required string ToGuid { get; init; }

    /// <summary>Where in <see cref="FromPath"/> the reference lives, e.g. "m_Shader" — a sample,
    /// not necessarily every property that names this same GUID (see this record's own class doc
    /// comment).</summary>
    public required string PropertyPath { get; init; }
}
