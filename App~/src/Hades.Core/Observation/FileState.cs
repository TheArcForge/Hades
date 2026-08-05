namespace Hades.Core.Observation;

/// <summary>
/// What a file looked like when it was last indexed. Comparing this against disk is how "what
/// changed" is answered without re-reading every file.
/// </summary>
public sealed record FileState
{
    public required string Path { get; init; }
    public required long MTimeUtcMs { get; init; }
    public required long Size { get; init; }
}
