namespace Hades.Core.Unity;

/// <summary>One YAML document inside a Unity asset — a GameObject, a component, a PrefabInstance.</summary>
public sealed record UnityObject
{
    public required int ClassId { get; init; }
    /// <summary>Friendly name from the class-id table.</summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// The type name as the document itself declares it. Usually identical to
    /// <see cref="TypeName"/>, but not always: class 1001 is "PrefabInstance" in the modern
    /// format and "Prefab" in the pre-2018.3 one, and only this field can tell them apart.
    /// </summary>
    public string? DeclaredTypeName { get; init; }
    public required long FileId { get; init; }

    /// <summary>m_Name where the object has one; null for components, which take their identity
    /// from the GameObject they hang off.</summary>
    public string? Name { get; init; }

    /// <summary>A placeholder for an object owned by a nested prefab. Resolving what it stands
    /// for needs the base prefab's graph — that is plan 3.</summary>
    public bool IsStripped { get; init; }

    public required IReadOnlyList<UnityReference> References { get; init; }

    /// <summary>Entries from a PrefabInstance's m_Modifications. Empty for every other object.</summary>
    public IReadOnlyList<UnityModification> Modifications { get; init; } = [];

    /// <summary>The source object this one overrides, when it came from a nested prefab.
    /// Overwhelmingly null — 22,487 occurrences corpus-wide are almost all {fileID: 0}.</summary>
    public UnityReference? CorrespondingSourceObject { get; init; }
}
