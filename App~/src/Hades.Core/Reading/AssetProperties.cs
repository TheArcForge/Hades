namespace Hades.Core.Reading;

/// <summary>
/// One GameObject in a parsed scene or prefab's hierarchy, with its children nested inline.
///
/// Not every node is a GameObject this file fully owns. A nested prefab instance's root, and a
/// prefab variant's entire root, physically live in another asset - resolving into that asset is
/// cross-file work <see cref="ReadThrough"/> deliberately does not do (that is graph-served
/// territory). Such a node still appears here, as a placeholder: <see cref="Kind"/> is
/// "PrefabInstance" rather than "GameObject", <see cref="Name"/> is null because this file cannot
/// name it, and <see cref="SourcePrefabGuid"/> names what it instantiates.
/// </summary>
public sealed record HierarchyNode
{
    public required long FileId { get; init; }

    /// <summary>"GameObject" for an object this file fully owns, or "PrefabInstance" for a
    /// placeholder standing in for a nested or variant instance whose content lives elsewhere.</summary>
    public required string Kind { get; init; }

    /// <summary>Null when this file alone cannot name the object - always the case for a
    /// "PrefabInstance" placeholder.</summary>
    public string? Name { get; init; }

    /// <summary>The GUID of the prefab this is an instance of. Set only when <see cref="Kind"/>
    /// is "PrefabInstance".</summary>
    public string? SourcePrefabGuid { get; init; }

    /// <summary>Component kind names attached to this GameObject (e.g. "MeshRenderer"),
    /// excluding the Transform itself - that is what this node's position in the tree already
    /// represents. Always empty for a "PrefabInstance" placeholder; its components live in
    /// whichever file owns the instantiated prefab.</summary>
    public required IReadOnlyList<string> Components { get; init; }

    public required IReadOnlyList<HierarchyNode> Children { get; init; }
}

/// <summary>
/// The parsed hierarchy of one scene or prefab file - what <c>prefab_get_contents</c> and
/// <c>scene_get_hierarchy</c> both return, since the two asset kinds are the same YAML format and
/// share exactly this shape.
/// </summary>
public sealed record AssetHierarchy
{
    public required string Path { get; init; }
    public required IReadOnlyList<HierarchyNode> Roots { get; init; }
}

/// <summary>
/// One component attached to a GameObject, as <see cref="ReadThrough.GetComponents"/> reads it
/// straight from the file - no graph involved, so a MonoBehaviour's <see cref="ScriptGuid"/> is
/// the raw <c>m_Script</c> guid, not yet resolved to a script name. Resolving it is
/// <c>component_get_all</c>'s one graph touch (via <c>PathForGuid</c>), done by
/// <c>ProjectService</c>, deliberately kept out of this purely single-file mechanism.
/// </summary>
public sealed record ComponentSummary
{
    public required long FileId { get; init; }

    /// <summary>The Unity class name - "Transform", "MeshRenderer", "MonoBehaviour", etc.</summary>
    public required string Kind { get; init; }

    /// <summary>The raw <c>m_Script</c> guid. Set only when <see cref="Kind"/> is
    /// "MonoBehaviour"; null for every builtin component, and also null for a MonoBehaviour with
    /// no resolvable guid reference at all (distinct from a guid that fails to resolve later).</summary>
    public string? ScriptGuid { get; init; }
}
