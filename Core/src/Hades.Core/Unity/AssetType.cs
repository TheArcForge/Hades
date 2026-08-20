namespace Hades.Core.Unity;

/// <summary>
/// An asset's friendly, file-level kind — what asset_get_info reports as "type" and asset_find
/// filters by. Deliberately extension-based, not content-based: a material or animator controller
/// is always exactly one YAML document, so reading its own declared type would work just as well,
/// but a prefab or scene's FIRST document is not reliably "the file's type" (document order is not
/// hierarchy order — see ReadThrough.BuildHierarchy's own note on this), so extension is the only
/// signal that is correct for every asset kind, not just some of them. The recognised extensions
/// mirror AssetIndexer's own Extensions list plus ".cs", which ScriptIndexer covers separately —
/// exactly the file kinds Hades already treats specially elsewhere.
/// </summary>
public static class AssetType
{
    /// <summary>Every value <see cref="FromPath"/> can return, in the same order asset_find's own
    /// [Description] always listed them. The closed vocabulary graph_query's 'fileType' filter
    /// (Plan 10 Task 6) validates a caller's input against - see QueryTools' own class doc comment
    /// for why that filter reads file identity rather than graph nodes.</summary>
    public static readonly string[] KnownTypes =
        ["Script", "Scene", "Prefab", "Material", "AnimatorController", "ScriptableObject", "Asset"];

    public static string FromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "Script",
        ".unity" => "Scene",
        ".prefab" => "Prefab",
        ".mat" => "Material",
        ".controller" => "AnimatorController",
        ".asset" => "ScriptableObject",
        _ => "Asset",
    };
}
