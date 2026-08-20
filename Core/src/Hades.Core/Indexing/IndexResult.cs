namespace Hades.Core.Indexing;

public sealed record IndexResult
{
    public required int FilesScanned { get; init; }
    public required int TypesFound { get; init; }
    public required TimeSpan Duration { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
