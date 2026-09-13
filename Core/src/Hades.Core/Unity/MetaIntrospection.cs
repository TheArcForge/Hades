using System.Text.RegularExpressions;
using Hades.Core.Reading;

namespace Hades.Core.Unity;

/// <summary>
/// DESIGN-07 Tier 1 — decodes a binary/imported asset's sibling <c>.meta</c> importer block
/// (already read by <see cref="ReadThrough.GetImportSettings"/>) into named, human-legible
/// fields, and separately extracts the handful of scalars <see cref="Indexing.BinaryAssetIndexer"/>
/// needs at index time without paying for a full YAML parse.
///
/// <para><b>§13.1/§9 — the unknown-value rule comes first, deliberately, before any decode
/// table.</b> <see cref="DecodeEnum"/> is the ONE place a raw Unity enum integer becomes a name,
/// and its unknown-value behaviour is: report the raw integer AS A STRING (e.g. <c>"47"</c>),
/// never fall through to a plausible-looking default. Every table below is built on top of this,
/// never around it — so an importer newer than Hades' tables (a future Unity version adding an
/// enum member) degrades honestly instead of lying. This is the single most important line in
/// this file: a wrong decode is the confident-lie failure DESIGN-07 exists to prevent.</para>
///
/// <para><b>Enum values below are VERIFIED (source)</b> — not asserted from memory, which is what
/// DESIGN-07 §13.1 itself flags as the open risk. Pinned by reflecting Unity 6000.3.2f1's own
/// shipped <c>UnityEditor.CoreModule.dll</c> / <c>UnityEngine.AudioModule.dll</c> (the exact
/// version the programme's probes were run against) via <c>System.Reflection.MetadataLoadContext</c>
/// against <c>TextureImporterType</c>, <c>TextureImporterCompression</c>,
/// <c>TextureImporterAlphaSource</c>, <c>ModelImporterAnimationType</c>,
/// <c>ModelImporterAvatarSetup</c>, <c>ModelImporterMeshCompression</c>,
/// <c>ModelImporterMaterialImportMode</c>, <c>AudioClipLoadType</c>, <c>AudioCompressionFormat</c>,
/// and <c>AudioSampleRateSetting</c> — the actual enum members and integer values Unity 6000.x
/// serializes, not a transcription. <c>TextureImporterType</c> additionally carries several
/// negative legacy members (Image/Bump/Cubemap/Reflection/Advanced/HDRI, pre-5.5 holdovers kept
/// only for API back-compat) that were NOT observed in any real, current <c>.meta</c> and are
/// deliberately left out of the table — <see cref="DecodeEnum"/>'s unknown-value fallback reports
/// them as their raw integer if one is ever encountered, exactly as it would any other unmapped
/// value, so leaving them out costs nothing.</para>
///
/// <para>Field PATHS (which are nested under which parent key) were independently checked against
/// real <c>.meta</c> files rather than trusted from the design brief — one field, texture's
/// <c>sRGBTexture</c>, turned out to be nested under <c>mipmaps</c> in every real file despite
/// being described as a bare top-level key; this reads it from its real, verified location.</para>
/// </summary>
public static class MetaIntrospection
{
    // ---------------------------------------------------------------- the unknown-value rule (first, per §13.1)

    /// <summary>Decodes a raw Unity enum integer via <paramref name="table"/>; an entry the table
    /// does not recognise (a Unity version newer than this table, or a corrupt/hand-edited value)
    /// returns the raw integer as a STRING, never a default member's name. This is the ONLY
    /// function in this file allowed to turn an integer into a name - every decode table below
    /// calls through it rather than switching directly, so the unknown-value behaviour cannot be
    /// forgotten on a future table.</summary>
    public static string? DecodeEnum(string? raw, IReadOnlyDictionary<int, string> table)
    {
        if (raw is null) return null;
        if (!int.TryParse(raw, out var value)) return raw; // not even an integer - report verbatim
        return table.TryGetValue(value, out var name) ? name : value.ToString();
    }

    // ---------------------------------------------------------------- decode tables (VERIFIED (source), see class doc)

    static readonly IReadOnlyDictionary<int, string> TextureImporterType = new Dictionary<int, string>
    {
        [0] = "Default",
        [1] = "NormalMap",
        [2] = "GUI",
        [4] = "Cookie",
        [6] = "Lightmap",
        [7] = "Cursor",
        [8] = "Sprite",
        [10] = "SingleChannel",
        [11] = "Shadowmask",
        [12] = "DirectionalLightmap",
    };

    static readonly IReadOnlyDictionary<int, string> TextureImporterCompression = new Dictionary<int, string>
    {
        [0] = "Uncompressed",
        [1] = "Compressed",
        [2] = "CompressedHQ",
        [3] = "CompressedLQ",
    };

    static readonly IReadOnlyDictionary<int, string> TextureImporterAlphaSource = new Dictionary<int, string>
    {
        [0] = "None",
        [1] = "FromInput",
        [2] = "FromGrayScale",
    };

    /// <summary>Unity's own enum member for value 3 is literally "Human" (verified against the
    /// shipped assembly) - reported as "Humanoid" here to match the vocabulary Unity's Inspector
    /// UI, its own documentation prose, and DESIGN-07 §8.2's own asset_subtype vocabulary all
    /// use. Every other member's name is used unchanged.</summary>
    static readonly IReadOnlyDictionary<int, string> ModelImporterAnimationType = new Dictionary<int, string>
    {
        [0] = "None",
        [1] = "Legacy",
        [2] = "Generic",
        [3] = "Humanoid",
    };

    static readonly IReadOnlyDictionary<int, string> ModelImporterAvatarSetup = new Dictionary<int, string>
    {
        [0] = "NoAvatar",
        [1] = "CreateFromThisModel",
        [2] = "CopyFromOther",
    };

    static readonly IReadOnlyDictionary<int, string> ModelImporterMeshCompression = new Dictionary<int, string>
    {
        [0] = "Off",
        [1] = "Low",
        [2] = "Medium",
        [3] = "High",
    };

    static readonly IReadOnlyDictionary<int, string> ModelImporterMaterialImportMode = new Dictionary<int, string>
    {
        [0] = "None",
        [1] = "ImportStandard",
        [2] = "ImportViaMaterialDescription",
    };

    static readonly IReadOnlyDictionary<int, string> AudioClipLoadType = new Dictionary<int, string>
    {
        [0] = "DecompressOnLoad",
        [1] = "CompressedInMemory",
        [2] = "Streaming",
    };

    static readonly IReadOnlyDictionary<int, string> AudioCompressionFormatTable = new Dictionary<int, string>
    {
        [0] = "PCM",
        [1] = "Vorbis",
        [2] = "ADPCM",
        [3] = "MP3",
        [4] = "VAG",
        [5] = "HEVAG",
        [6] = "XMA",
        [7] = "AAC",
        [8] = "GCADPCM",
        [9] = "ATRAC9",
    };

    static readonly IReadOnlyDictionary<int, string> AudioSampleRateSetting = new Dictionary<int, string>
    {
        [0] = "PreserveSampleRate",
        [1] = "OptimizeSampleRate",
        [2] = "OverrideSampleRate",
    };

    // ---------------------------------------------------------------- Tier 1, full decode (read-through, on demand)

    /// <summary>Interprets <paramref name="settings"/> (already read by
    /// <see cref="ReadThrough.GetImportSettings"/>) into named fields for
    /// <c>inspect_asset.introspection</c>. Unrecognised importer types (a Unity version newer than
    /// this file, or a kind DESIGN-07 does not decode - fonts/shaders, deliberately cut per §4)
    /// return every decoded field null with <see cref="MetaIntrospectionResult.ImporterType"/>
    /// still set verbatim - never a guess.</summary>
    public static MetaIntrospectionResult Decode(ImportSettingsInfo settings)
    {
        var top = settings.Settings;

        return settings.ImporterType switch
        {
            "TextureImporter" => DecodeTexture(top, settings.ImporterType),
            "ModelImporter" => DecodeModel(top, settings.ImporterType),
            "AudioImporter" => DecodeAudio(top, settings.ImporterType),
            _ => new MetaIntrospectionResult { ImporterType = settings.ImporterType },
        };
    }

    static MetaIntrospectionResult DecodeTexture(IReadOnlyDictionary<string, object?> top, string importerType)
    {
        var mipmaps = AsMap(top, "mipmaps");
        var textureType = AsScalar(top, "textureType");

        return new MetaIntrospectionResult
        {
            ImporterType = importerType,
            TextureType = DecodeEnum(textureType, TextureImporterType),
            MaxTextureSize = AsInt(top, "maxTextureSize"),
            SRGB = AsBool(mipmaps, "sRGBTexture"),
            GenerateMipMaps = AsBool(mipmaps, "enableMipMap"),
            ReadWriteEnabled = AsBool(top, "isReadable"),
            StreamingMipmaps = AsBool(top, "streamingMipmaps"),
            AlphaIsTransparency = AsBool(top, "alphaIsTransparency"),
            AlphaSource = DecodeEnum(AsScalar(top, "alphaUsage"), TextureImporterAlphaSource),
            Compression = DecodeTexturePlatforms(top),
        };
    }

    static IReadOnlyList<TexturePlatformSetting>? DecodeTexturePlatforms(IReadOnlyDictionary<string, object?> top)
    {
        if (top.GetValueOrDefault("platformSettings") is not List<object?> list) return null;

        var results = new List<TexturePlatformSetting>();
        foreach (var entry in list)
        {
            if (entry is not IReadOnlyDictionary<string, object?> platform) continue;

            var compressionRaw = AsScalar(platform, "textureCompression");
            results.Add(new TexturePlatformSetting
            {
                BuildTarget = AsScalar(platform, "buildTarget") ?? "",
                Compressed = compressionRaw is not null && compressionRaw != "0",
                CompressionLevel = DecodeEnum(compressionRaw, TextureImporterCompression),
                Crunched = AsBool(platform, "crunchedCompression") ?? false,
                Quality = AsInt(platform, "compressionQuality"),
                Overridden = AsBool(platform, "overridden") ?? false,
            });
        }

        return results;
    }

    static MetaIntrospectionResult DecodeModel(IReadOnlyDictionary<string, object?> top, string importerType)
    {
        var animations = AsMap(top, "animations");
        var materials = AsMap(top, "materials");
        var meshes = AsMap(top, "meshes");
        var humanDescription = AsMap(top, "humanDescription");

        return new MetaIntrospectionResult
        {
            ImporterType = importerType,
            RigType = DecodeEnum(AsScalar(top, "animationType"), ModelImporterAnimationType),
            AvatarSetup = DecodeEnum(AsScalar(top, "avatarSetup"), ModelImporterAvatarSetup),
            ModelIsReadable = AsBool(animations, "isReadable"),
            MeshCompression = DecodeEnum(AsScalar(meshes, "meshCompression"), ModelImporterMeshCompression),
            MaterialImportMode = DecodeEnum(AsScalar(materials, "materialImportMode"), ModelImporterMaterialImportMode),
            BoneCount = (humanDescription?.GetValueOrDefault("skeleton") as List<object?>)?.Count,
            // §3.2's own caveat, reused verbatim in spirit: clipAnimations is populated only when
            // clips were configured in the Inspector, so this is a LOWER BOUND on what the FBX
            // actually contains, never a census - the same honesty shape find_unset_references'
            // listenerCount already uses.
            Clips = DecodeClips(animations),
        };
    }

    static IReadOnlyList<ModelClipInfo>? DecodeClips(IReadOnlyDictionary<string, object?>? animations)
    {
        if (animations?.GetValueOrDefault("clipAnimations") is not List<object?> list) return null;

        var results = new List<ModelClipInfo>();
        foreach (var entry in list)
        {
            if (entry is not IReadOnlyDictionary<string, object?> clip) continue;

            results.Add(new ModelClipInfo
            {
                Name = AsScalar(clip, "name") ?? "",
                FirstFrame = AsInt(clip, "firstFrame"),
                LastFrame = AsInt(clip, "lastFrame"),
                Loops = AsBool(clip, "loopTime"),
            });
        }

        return results;
    }

    static MetaIntrospectionResult DecodeAudio(IReadOnlyDictionary<string, object?> top, string importerType)
    {
        var defaultSettings = AsMap(top, "defaultSettings");

        return new MetaIntrospectionResult
        {
            ImporterType = importerType,
            LoadType = DecodeEnum(AsScalar(defaultSettings, "loadType"), AudioClipLoadType),
            AudioCompressionFormat = DecodeEnum(AsScalar(defaultSettings, "compressionFormat"), AudioCompressionFormatTable),
            Quality = AsScalar(defaultSettings, "quality"),
            SampleRateSetting = DecodeEnum(AsScalar(defaultSettings, "sampleRateSetting"), AudioSampleRateSetting),
            // sampleRateOverride is inert unless sampleRateSetting is "OverrideSampleRate" (2) -
            // reporting it unconditionally as "the sample rate" would be exactly the confident lie
            // this feature exists to avoid (DESIGN-07 §3.3), so it is only surfaced when it applies.
            SampleRateOverride = AsScalar(defaultSettings, "sampleRateSetting") == "2"
                ? AsInt(defaultSettings, "sampleRateOverride")
                : null,
            ForceToMono = AsBool(top, "forceToMono"),
            Normalize = AsBool(top, "normalize"),
            PreloadAudioData = AsBool(top, "preloadAudioData"),
            LoadInBackground = AsBool(top, "loadInBackground"),
            Ambisonic = AsBool(top, "ambisonic"),
            Is3D = AsBool(top, "3D"),
        };
    }

    // ---------------------------------------------------------------- raw-tree accessors (Settings values are always strings - see ReadThrough.ReadNode)

    static IReadOnlyDictionary<string, object?>? AsMap(IReadOnlyDictionary<string, object?> parent, string key) =>
        parent.GetValueOrDefault(key) as IReadOnlyDictionary<string, object?>;

    static string? AsScalar(IReadOnlyDictionary<string, object?>? parent, string key) =>
        parent?.GetValueOrDefault(key) as string;

    static int? AsInt(IReadOnlyDictionary<string, object?>? parent, string key) =>
        int.TryParse(AsScalar(parent, key), out var value) ? value : null;

    /// <summary>Unity's own YAML convention for a serialized bool: 0/1, never "true"/"false".</summary>
    static bool? AsBool(IReadOnlyDictionary<string, object?>? parent, string key) =>
        AsScalar(parent, key) is { } raw ? raw != "0" : null;

    // ---------------------------------------------------------------- Tier 1, bounded scan (index time only - see BinaryAssetIndexer)

    /// <summary>
    /// What <see cref="Indexing.BinaryAssetIndexer"/> needs at index time to populate
    /// <see cref="Graph.GraphNode.AssetSubtype"/>/<see cref="Graph.GraphNode.AssetMaxSize"/>/
    /// <see cref="Graph.GraphNode.AssetFlags"/> for one asset. Deliberately NOT built on
    /// <see cref="Decode"/>/<see cref="ReadThrough.GetImportSettings"/>: those construct a full
    /// YamlDotNet document tree (every nested mapping and sequence becomes its own
    /// Dictionary/List), which DESIGN-07 §7.4 measured as the dominant added indexing cost.
    /// </summary>
    public sealed record IndexingFields
    {
        public required string ImporterType { get; init; }
        public string? AssetSubtype { get; init; }
        public int? MaxTextureSize { get; init; }

        /// <summary>Null (not <see cref="AssetFlags.None"/>) for an importer type this file does
        /// not decode (fonts, shaders — cut per DESIGN-07 §4): "never checked", not "checked, none
        /// set". Texture/Model/Audio always compute an explicit value here, even when it comes out
        /// to <see cref="AssetFlags.None"/>.</summary>
        public AssetFlags? Flags { get; init; }
    }

    // One top-level ".meta" key ends in "Importer" (fileFormatVersion/guid/folderAsset are the
    // only siblings - see ReadThrough.GetImportSettings' own doc comment) - always at column 0.
    static readonly Regex ImporterKeyPattern = new(@"(?m)^([A-Za-z0-9]+Importer):\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Extracts exactly the scalars the five indexed columns need via plain per-key regex
    /// matching over the WHOLE file content - never a fixed-byte-prefix cap. DESIGN-07 §7.4
    /// itself proposed a "~16KB" prefix scan as the cost mitigation; verified against a real,
    /// populated ModelImporter .meta (a 911-line, ~30KB file with a ~60-bone humanDescription)
    /// that <c>animationType</c> sits at byte offset ~30,677 of ~30,939 - the LAST ~1% of the
    /// file, entirely past any 16KB prefix, because Unity serializes
    /// <c>humanDescription.skeleton</c> (bone-count-dependent, and the whole reason a populated
    /// rig's .meta is large at all) BEFORE <c>animationType</c>. A byte-capped scan would silently
    /// leave every well-rigged Humanoid model's rig type unindexed - exactly the asset class this
    /// feature exists to serve. This scans the full text instead: still far cheaper than a full
    /// YamlDotNet parse (a handful of linear regex passes with no per-node tree allocation, versus
    /// one Dictionary/List allocation per YAML node), and correct regardless of skeleton size.
    /// Each field is looked up by a flat "key: value" regex, safe because a single .meta has
    /// exactly one importer block, so a field name is unambiguous once <see cref="ImporterKeyPattern"/>
    /// has identified which importer this file is.
    /// </summary>
    public static IndexingFields? ScanForIndexing(string metaContent)
    {
        var importerMatch = ImporterKeyPattern.Match(metaContent);
        if (!importerMatch.Success) return null;

        var importerType = importerMatch.Groups[1].Value;

        return importerType switch
        {
            "TextureImporter" => ScanTexture(metaContent, importerType),
            "ModelImporter" => ScanModel(metaContent, importerType),
            "AudioImporter" => ScanAudio(metaContent, importerType),
            _ => new IndexingFields { ImporterType = importerType },
        };
    }

    static IndexingFields ScanTexture(string content, string importerType)
    {
        var flags = AssetFlags.None;
        if (ScanBool(content, "isReadable")) flags |= AssetFlags.Readable;
        if (ScanBool(content, "enableMipMap")) flags |= AssetFlags.Mipmaps;
        if (ScanBool(content, "sRGBTexture")) flags |= AssetFlags.SRGB;
        if (ScanBool(content, "streamingMipmaps")) flags |= AssetFlags.Streaming;

        // First occurrence only, matching real .meta files' own field order (the default/
        // DefaultTexturePlatform entry is always first in platformSettings[]) - a coarse "is this
        // asset compressed at all" bit, not a per-platform census (the full Decode() path above
        // is what reports per-platform detail).
        var compressionRaw = ScanFirst(content, "textureCompression");
        if (compressionRaw is not null && compressionRaw != "0") flags |= AssetFlags.Compressed;

        return new IndexingFields
        {
            ImporterType = importerType,
            AssetSubtype = DecodeEnum(ScanFirst(content, "textureType"), TextureImporterType),
            MaxTextureSize = ScanInt(content, "maxTextureSize"),
            Flags = flags,
        };
    }

    static IndexingFields ScanModel(string content, string importerType)
    {
        var flags = AssetFlags.None;
        // "isReadable" under ModelImporter is animations.isReadable - the only occurrence of that
        // key in a ModelImporter block.
        if (ScanBool(content, "isReadable")) flags |= AssetFlags.Readable;

        return new IndexingFields
        {
            ImporterType = importerType,
            AssetSubtype = DecodeEnum(ScanFirst(content, "animationType"), ModelImporterAnimationType),
            Flags = flags,
        };
    }

    static IndexingFields ScanAudio(string content, string importerType)
    {
        var flags = AssetFlags.None;
        if (ScanBool(content, "forceToMono")) flags |= AssetFlags.ForceToMono;

        var compressionRaw = ScanFirst(content, "compressionFormat");
        if (compressionRaw is not null && compressionRaw != "0") flags |= AssetFlags.Compressed;

        return new IndexingFields
        {
            ImporterType = importerType,
            AssetSubtype = null, // audio has no subtype concept in DESIGN-07's vocabulary
            Flags = flags,
        };
    }

    static string? ScanFirst(string content, string key)
    {
        var match = Regex.Match(content, $@"(?m)^\s*{Regex.Escape(key)}:\s*(\S*)\s*$");
        return match.Success ? match.Groups[1].Value : null;
    }

    static int? ScanInt(string content, string key) =>
        int.TryParse(ScanFirst(content, key), out var value) ? value : null;

    static bool ScanBool(string content, string key) => ScanFirst(content, key) == "1";
}

/// <summary>One texture platform override's compression settings — <see
/// cref="MetaIntrospectionResult.Compression"/>.</summary>
public sealed record TexturePlatformSetting
{
    public required string BuildTarget { get; init; }
    public required bool Compressed { get; init; }
    public string? CompressionLevel { get; init; }
    public required bool Crunched { get; init; }
    public int? Quality { get; init; }
    public required bool Overridden { get; init; }
}

/// <summary>One model animation clip's frame range — <see cref="MetaIntrospectionResult.Clips"/>.
/// A LOWER BOUND on what the FBX actually contains, not a census — see
/// <see cref="MetaIntrospection.Decode"/>'s own doc comment.</summary>
public sealed record ModelClipInfo
{
    public required string Name { get; init; }
    public int? FirstFrame { get; init; }
    public int? LastFrame { get; init; }
    public bool? Loops { get; init; }
}

/// <summary>
/// DESIGN-07 Tier 1 result — <c>inspect_asset.introspection</c>'s decoded view of one asset's
/// <c>.meta</c> importer block. Every field is nullable and absence is never silent: a field that
/// does not apply to this <see cref="ImporterType"/> is simply never populated (the type-specific
/// fields below are grouped by which importer sets them), and <see cref="MetaIntrospection.Decode"/>
/// itself is the single place an unrecognised enum value degrades to its raw integer rather than a
/// guess.
/// </summary>
public sealed record MetaIntrospectionResult
{
    /// <summary>The importer's own class name, verbatim from the .meta - "TextureImporter",
    /// "ModelImporter", "AudioImporter", or, for a kind this file does not decode (fonts, shaders
    /// — deliberately cut per DESIGN-07 §4), whatever the .meta itself names.</summary>
    public required string ImporterType { get; init; }

    // -- TextureImporter
    public string? TextureType { get; init; }
    public int? MaxTextureSize { get; init; }
    public bool? SRGB { get; init; }
    public bool? GenerateMipMaps { get; init; }
    public bool? ReadWriteEnabled { get; init; }
    public bool? StreamingMipmaps { get; init; }
    public bool? AlphaIsTransparency { get; init; }
    public string? AlphaSource { get; init; }
    public IReadOnlyList<TexturePlatformSetting>? Compression { get; init; }

    // -- ModelImporter
    public string? RigType { get; init; }
    public string? AvatarSetup { get; init; }
    public bool? ModelIsReadable { get; init; }
    public string? MeshCompression { get; init; }
    public string? MaterialImportMode { get; init; }
    public int? BoneCount { get; init; }
    public IReadOnlyList<ModelClipInfo>? Clips { get; init; }

    // -- AudioImporter
    public string? LoadType { get; init; }
    public string? AudioCompressionFormat { get; init; }
    public string? Quality { get; init; }
    public string? SampleRateSetting { get; init; }
    public int? SampleRateOverride { get; init; }
    public bool? ForceToMono { get; init; }
    public bool? Normalize { get; init; }
    public bool? PreloadAudioData { get; init; }
    public bool? LoadInBackground { get; init; }
    public bool? Ambisonic { get; init; }
    public bool? Is3D { get; init; }
}
