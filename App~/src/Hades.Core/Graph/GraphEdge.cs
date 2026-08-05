namespace Hades.Core.Graph;

/// <summary>
/// A directed relationship between two objects, expressed the way Unity itself expresses one:
/// from a (path, fileID) object to a (guid, fileID) target. <see cref="ToGuid"/> is null for a
/// reference within the same asset file.
/// </summary>
public sealed record GraphEdge
{
    public required string FromPath { get; init; }
    public required long FromFileId { get; init; }

    public string? ToGuid { get; init; }
    public required long ToFileId { get; init; }

    /// <summary>"references" today; later plans add "instance_of", "overrides", and so on.</summary>
    public required string Kind { get; init; }

    /// <summary>Where in the source object the reference lived, e.g. "m_Script".</summary>
    public required string PropertyPath { get; init; }
}
