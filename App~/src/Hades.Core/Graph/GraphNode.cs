namespace Hades.Core.Graph;

/// <summary>One node in the knowledge graph. Identity is (Path, Kind, Name).</summary>
public sealed record GraphNode
{
    /// <summary>"Class", "Struct", "Interface", "Enum", "Record", and later asset kinds.</summary>
    public required string Kind { get; init; }

    public required string Name { get; init; }

    /// <summary>Project-relative path, e.g. "Assets/Scripts/PlayerController.cs".</summary>
    public required string Path { get; init; }

    public string? Namespace { get; init; }
    public int Line { get; init; }

    /// <summary>The owning asset's .meta GUID, for nodes that came from a Unity asset.</summary>
    public string? Guid { get; init; }

    /// <summary>The object's fileID within its asset. 0 for script nodes, which have no fileID.</summary>
    public long FileId { get; init; }
}
