namespace Hades.Core.Graph;

/// <summary>One asset_find hit: a distinct project-relative path and its extension-derived
/// <see cref="Unity.AssetType"/>.</summary>
public sealed record AssetMatch
{
    public required string Path { get; init; }
    public required string Type { get; init; }
}
