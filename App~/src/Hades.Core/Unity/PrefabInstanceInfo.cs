namespace Hades.Core.Unity;

/// <summary>
/// A PrefabInstance's graph-relevant content: what it instantiates, where it attaches, and which
/// of its overrides rewire references.
/// </summary>
public sealed record PrefabInstanceInfo
{
    public required long FileId { get; init; }

    /// <summary>The prefab asset being instantiated.</summary>
    public required UnityReference SourcePrefab { get; init; }

    /// <summary>
    /// Where the instance attaches. Null when it is the root, which is what makes the containing
    /// asset a prefab VARIANT rather than a scene or prefab hosting a nested instance.
    /// Measured: 7 such roots corpus-wide, against 419 instances in total.
    /// </summary>
    public UnityReference? TransformParent { get; init; }

    /// <summary>Only the overrides that change an object reference — 792 of 44,576 corpus-wide.</summary>
    public required IReadOnlyList<UnityModification> ReferenceOverrides { get; init; }

    public bool IsVariantRoot => TransformParent is null;
}
