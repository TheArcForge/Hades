namespace Hades.Core.Graph;

/// <summary>One object that references a given asset, and how.</summary>
public sealed record ReferencingObject
{
    public required string Path { get; init; }
    public required long FileId { get; init; }

    /// <summary>Name and kind of the referencing object, when it is itself a known node.
    /// Null for a reference from an object this indexer did not record.</summary>
    public string? Name { get; init; }
    public string? Kind { get; init; }

    /// <summary>"references", "instance_of", or "corresponds_to".</summary>
    public required string EdgeKind { get; init; }

    /// <summary>Which property held the reference, e.g. "m_Script".</summary>
    public required string PropertyPath { get; init; }
}
