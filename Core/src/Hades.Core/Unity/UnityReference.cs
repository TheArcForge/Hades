namespace Hades.Core.Unity;

/// <summary>
/// Unity's universal reference triple. <see cref="Guid"/> is null for a reference within the same
/// file; when set, it names another asset via its .meta GUID. This shape is format-level and
/// version-independent — it is the backbone of the whole asset reference graph.
/// </summary>
public sealed record UnityReference
{
    public required long FileId { get; init; }
    public string? Guid { get; init; }
    public int? Type { get; init; }

    /// <summary>Dotted path of the property holding this reference, e.g. "m_Script".</summary>
    public required string PropertyPath { get; init; }

    public bool IsExternal => Guid is not null;
}
