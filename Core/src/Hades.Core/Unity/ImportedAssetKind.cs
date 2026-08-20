namespace Hades.Core.Unity;

/// <summary>
/// Maps a binary/imported asset's file extension to its graph node <c>Kind</c>. These files are
/// opaque to Hades — it never parses their content — so this mapping, plus the sibling .meta's
/// GUID (<see cref="MetaFileReader"/>) and the filename itself, is the entire identity a node for
/// one of these needs. Single source of truth shared by <see cref="Indexing.BinaryAssetIndexer"/>
/// (which extensions to walk, and what Kind to write for each) and
/// <see cref="Observation.ProjectSweeper"/> (which extensions count as a graph event at all, for
/// freshness) — both would otherwise carry their own copy of this same list, free to drift apart.
///
/// Deliberately a NARROWER, DIFFERENT-PURPOSE vocabulary from <see cref="AssetType"/>:
/// <see cref="AssetType"/> answers "what file-level kind is this" for asset_find (a small closed
/// set — Script/Scene/Prefab/Material/AnimatorController/ScriptableObject/Asset — extension-based
/// for the same reason this is), while this answers "what graph node Kind should this specific
/// binary asset get". The two never collide in practice: every extension recognised here still
/// falls through <see cref="AssetType.FromPath"/>'s switch to its generic "Asset" catch-all,
/// exactly as it did before this type existed — asset_find's own type vocabulary is a separate
/// concern this feature does not touch.
///
/// Extension list, deliberately smaller than Unity's full importer surface: the measured baseline
/// (textures .png/.tga/.psd/.jpg, models .fbx, audio .wav, fonts .ttf/.otf, shaders
/// .shader/.shadergraph, animation .anim) plus siblings whose kind mapping is unambiguous
/// (.jpeg/.exr alongside the other textures; .obj alongside .fbx; .mp3/.ogg/.aiff alongside .wav;
/// .compute as its own ComputeShader kind, since a compute shader is a genuinely different Unity
/// object from a surface shader). Video, terrain data, and the rest of Unity's importer surface
/// are left out on purpose — a smaller honest set beats a speculative one.
/// </summary>
public static class ImportedAssetKind
{
    static readonly IReadOnlyDictionary<string, string> KindByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Textures — TextureImporter's domain regardless of source format.
            [".png"] = "Texture2D",
            [".tga"] = "Texture2D",
            [".psd"] = "Texture2D",
            [".jpg"] = "Texture2D",
            [".jpeg"] = "Texture2D",
            [".exr"] = "Texture2D",

            // Models — ModelImporter.
            [".fbx"] = "Model",
            [".obj"] = "Model",

            // Audio — AudioImporter.
            [".wav"] = "AudioClip",
            [".mp3"] = "AudioClip",
            [".ogg"] = "AudioClip",
            [".aiff"] = "AudioClip",

            // Fonts — TrueTypeFontImporter.
            [".ttf"] = "Font",
            [".otf"] = "Font",

            // Shaders — three distinct source formats, three distinct Unity object kinds.
            [".shader"] = "Shader",
            [".shadergraph"] = "ShaderGraph",
            [".compute"] = "ComputeShader",

            // Animation — the binary/imported AnimationClip, distinct from AnimatorController
            // (.controller), which is YAML and already indexed by AssetIndexer.
            [".anim"] = "AnimationClip",
        };

    /// <summary>Every extension this mapping recognises — the walk-set
    /// <see cref="Indexing.BinaryAssetIndexer"/> and <see cref="Observation.ProjectSweeper"/>
    /// both share.</summary>
    public static IReadOnlyCollection<string> Extensions { get; } = KindByExtension.Keys.ToList();

    /// <summary>The graph node Kind for <paramref name="path"/>'s extension (case-insensitive),
    /// or null when it is not one of the kinds this mapping recognises.</summary>
    public static string? KindForPath(string path) =>
        KindByExtension.TryGetValue(Path.GetExtension(path), out var kind) ? kind : null;
}
