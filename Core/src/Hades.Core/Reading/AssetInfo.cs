namespace Hades.Core.Reading;

/// <summary>
/// One asset's identity, as <c>asset_get_info</c> reports it: no graph involved, so <see
/// cref="Type"/> is the extension-derived <see cref="Unity.AssetType"/>, not a graph-resolved
/// classification, and <see cref="Guid"/> is read straight from the sibling .meta file - null
/// when there is no .meta yet, which is a normal state for a file just added outside the Editor,
/// not an error.
/// </summary>
public sealed record AssetInfo
{
    public required string Path { get; init; }
    public string? Guid { get; init; }
    public required string Type { get; init; }
}

/// <summary>
/// The importer block from one asset's .meta file, as <c>asset_get_import_settings</c> reports
/// it - e.g. a TextureImporter's compression settings, or a PrefabImporter's (sparse) settings.
/// <see cref="ImporterType"/> is whichever top-level .meta key ends in "Importer" (Unity's own
/// naming convention for every importer class, TextureImporter/MonoImporter/PrefabImporter/
/// DefaultImporter alike), and <see cref="Settings"/> is that key's value, unfiltered - the same
/// raw-tree shape <see cref="ReadThrough.GetComponentProperties"/> returns for a component.
/// </summary>
public sealed record ImportSettingsInfo
{
    public required string Path { get; init; }
    public required string ImporterType { get; init; }
    public required IReadOnlyDictionary<string, object?> Settings { get; init; }
}
