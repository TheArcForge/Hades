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

    /// <summary>The object's fileID within its asset. 0 for script nodes and for binary/imported
    /// asset nodes (textures, models, audio, fonts, shaders, animation clips — see
    /// <see cref="Unity.ImportedAssetKind"/>): neither has a fileID, since both are a whole-file
    /// identity rather than one object among several in a multi-object YAML asset.</summary>
    public long FileId { get; init; }

    // ---------------------------------------------------------------- DESIGN-07: binary asset introspection
    //
    // Five columns populated ONLY for binary/imported asset nodes (see
    // Unity.ImportedAssetKind) — null for every script/YAML-asset node. Detail itself is never
    // stored (DESIGN-07 §7.2: inspect_asset reads it through, on demand); these exist purely so
    // graph_query can filter on them without opening every asset's .meta.

    /// <summary>The importer's own sub-classification, in canonical PascalCase exactly matching
    /// the Unity enum member name — e.g. a texture's <c>textureType</c> ("Sprite", "NormalMap",
    /// "GUI", "Default", ...) or a model's rig type ("Humanoid", "Generic", "Legacy", "None").
    /// Canonical case, never <c>LOWER()</c>'d, so graph_query's <c>assetSubtype</c> filter is a
    /// plain indexable <c>=</c> (see <see cref="Graph.GraphDatabase.QueryGraph"/>'s own doc
    /// comment on why an expression index would not help here). Null when the kind has no
    /// subtype concept (audio, shaders, fonts) or the value could not be read/decoded.</summary>
    public string? AssetSubtype { get; init; }

    /// <summary>Tier 2 (DESIGN-07 §5) — a texture's measured source pixel width, sniffed from the
    /// file's own header, never from the .meta. Null for every non-texture kind and for a texture
    /// whose header could not be read (see <see cref="ImageHeaderReader"/>).</summary>
    public int? AssetWidth { get; init; }

    /// <summary>Tier 2 sibling of <see cref="AssetWidth"/>.</summary>
    public int? AssetHeight { get; init; }

    /// <summary>Tier 1 — a texture's <c>maxTextureSize</c>: the import CEILING Unity was
    /// configured with, not a measurement. A 64×64 icon and an 8192×8192 texture can both report
    /// 2048 here under default settings — see <see cref="AssetWidth"/>/<see cref="AssetHeight"/>
    /// for the measured value. Null for every non-texture kind.</summary>
    public int? AssetMaxSize { get; init; }

    /// <summary>Bitfield of performance-relevant importer flags — see <see cref="Unity.AssetFlags"/>
    /// for the bit assignments (append-only forever: the value is persisted and read back by
    /// graph_query, so reordering would silently corrupt every existing graph). Null when the
    /// kind carries none of these flags or the .meta could not be read.</summary>
    public int? AssetFlags { get; init; }
}
