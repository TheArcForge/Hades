namespace Hades.Core.Graph;

/// <summary>
/// Plan 16 Task 1: the pre-flight impact query a destructive mutation calls before acting - see
/// docs/superpowers/plans/2026-08-07-mutation-impact.md ("The threat model" and "Design
/// constraints"). Not wired into any mutation tool yet; that is Tasks 2-4. This file only has to
/// give those tasks one shared, tested query.
///
/// <para><b>No second reference implementation.</b> <c>find_references_to</c>'s graph walk is exact
/// - hand-verified 12/12 against hand-counted GUID references, twice, and 0/0/0 on a
/// never-serialised type. Re-deriving references here, even slightly differently, risks the exact
/// failure this feature exists to prevent: two answers to "what references this" that can disagree.
/// <see cref="Analyze"/> below calls <see cref="ProjectService.FindReferencesTo"/> - the same method
/// the <c>find_references_to</c> MCP tool itself calls (<c>HadesTools.FindReferencesTo</c> in
/// Hades.Server/Mcp/HadesTools.cs) - and copies its fields; it never touches
/// <see cref="GraphDatabase"/> directly.</para>
///
/// <para><b>Cost is opt-in.</b> Nothing calls <see cref="Analyze"/> as part of indexing or of any
/// existing tool. A mutation tool pays the extra query only on the call where it chooses to - see
/// the plan's "Design constraints": adding a component or setting a property must stay exactly as
/// fast as it is today.</para>
/// </summary>
public static class MutationImpact
{
    /// <summary>
    /// The fact every impact result carries, worded once here so every consumer - human or agent -
    /// renders the identical sentence rather than each paraphrasing it differently. Per spec #3 §1
    /// ("Swift renders, .NET decides"), extended to every caller of this result: the wording is
    /// authored in .NET and rendered verbatim, never re-derived downstream.
    ///
    /// Names the concrete APIs from the plan's threat model rather than speaking abstractly -
    /// "may miss some references" invites the reader to assume the miss is rare or exotic; naming
    /// GameObject.Find, CompareTag, SetTrigger/SetBool, and Resources.Load makes clear the gap is
    /// routine Unity code, not an edge case.
    /// </summary>
    public const string BlindSpot =
        "This graph only sees GUID- and symbol-based references (prefab/scene YAML fields, C# type "
        + "usage) and cannot see string-based Unity lookups - GameObject.Find(\"...\"), "
        + "CompareTag(\"...\"), Animator.SetTrigger(\"...\")/SetBool(\"...\"), Resources.Load(\"...\"), "
        + "and similar - which are common in Unity code. A result with zero references means the "
        + "GRAPH found none; it is not a guarantee that nothing will break.";

    /// <summary>
    /// What would break if <paramref name="assetPath"/> stopped resolving - removed, renamed, or
    /// otherwise invalidated - via the same call <c>find_references_to</c> itself makes:
    /// <see cref="ProjectService.FindReferencesTo"/>. Null under the exact same condition that call
    /// returns null: <paramref name="productGuid"/> or <paramref name="assetPath"/> unknown to the
    /// graph. A known asset with zero references still returns a result, with
    /// <see cref="MutationImpactResult.TotalReferences"/> at 0 and <see cref="BlindSpot"/> still
    /// present - "known, zero references" and "unknown" mean very different things to someone
    /// asking what would break, and collapsing them here would throw away a distinction
    /// <see cref="ProjectService.FindReferencesTo"/> already took care to preserve.
    /// </summary>
    public static MutationImpactResult? Analyze(
        ProjectService projects, string productGuid, string assetPath, int limit = 100)
    {
        var references = projects.FindReferencesTo(productGuid, assetPath, limit);
        if (references is null) return null;

        return new MutationImpactResult
        {
            AssetPath = references.AssetPath,
            Guid = references.Guid,
            TotalReferences = references.TotalReferences,
            ReferencingFileCount = references.ReferencingFileCount,
            Files = references.Files,
            Truncated = references.Truncated,
            BlindSpot = BlindSpot,
        };
    }
}

/// <summary>
/// <see cref="MutationImpact.Analyze"/>'s result: <see cref="ReferenceQueryResult"/>'s own fields,
/// copied verbatim, plus <see cref="BlindSpot"/> - see <see cref="MutationImpact"/>'s own remarks
/// for why that field exists and why it is never null or empty.
/// </summary>
public sealed record MutationImpactResult
{
    public required string AssetPath { get; init; }
    public required string Guid { get; init; }

    /// <summary>Individual references, across every file. Often far larger than the file count.</summary>
    public required int TotalReferences { get; init; }

    /// <summary>Distinct files — the number that answers "how widely is this used".</summary>
    public required int ReferencingFileCount { get; init; }

    public required IReadOnlyList<ReferencingFile> Files { get; init; }

    /// <summary>True when more FILES exist than were returned.</summary>
    public required bool Truncated { get; init; }

    /// <summary>
    /// Always <see cref="MutationImpact.BlindSpot"/>, verbatim — never null or empty, present on
    /// every result including a clean (zero-reference) one. Leaving this out when
    /// <see cref="TotalReferences"/> is 0 is exactly the failure this type exists to prevent: a
    /// caller reading "zero references" as "nothing will break" when the honest claim is narrower —
    /// "nothing the GRAPH can see will break".
    /// </summary>
    public required string BlindSpot { get; init; }
}
