namespace Hades.Core.Unity;

/// <summary>
/// DESIGN-07 §7.2/§13.2 — performance-relevant importer flags, packed into <see
/// cref="Graph.GraphNode.AssetFlags"/> as one bitfield rather than one column per flag ("nodes"
/// is already a wide table under a five-column unique index — see <see cref="Graph.GraphSchema"/>
/// — and every flag here is queried together, "is this asset expensive", not independently).
///
/// <para><b>Bit positions are assigned explicitly and are APPEND-ONLY FOREVER.</b> The value is
/// persisted in SQLite and read back by graph_query, so reordering — or reusing a retired bit
/// position — would silently corrupt every existing graph on disk rather than failing loudly. A
/// flag that stops being populated keeps its bit reserved, unused, rather than being deleted and
/// its number recycled. Decided in exactly one place (here), before <see cref="MetaIntrospection"/>
/// writes the column, per §13.2's own instruction.</para>
///
/// <para>Not every flag applies to every kind — a row's <see cref="Graph.GraphNode.Kind"/> says
/// which bits are meaningful for it; the rest are simply never set (0), never a lie about a value
/// that was never read:</para>
/// <list type="bullet">
/// <item><description><b>Texture2D</b> — <see cref="Readable"/> (TextureImporter.isReadable),
/// <see cref="Mipmaps"/> (mipmaps.enableMipMap), <see cref="Compressed"/> (the first/default
/// platform entry's textureCompression != Uncompressed), <see cref="SRGB"/>
/// (mipmaps.sRGBTexture), <see cref="Streaming"/> (streamingMipmaps).</description></item>
/// <item><description><b>Model</b> — <see cref="Readable"/> only (animations.isReadable).
/// </description></item>
/// <item><description><b>AudioClip</b> — <see cref="Compressed"/> (defaultSettings.
/// compressionFormat != PCM) and <see cref="ForceToMono"/> (forceToMono).</description></item>
/// </list>
/// </summary>
[Flags]
public enum AssetFlags
{
    None = 0,

    /// <summary>CPU-readable at runtime — doubles memory. TextureImporter.isReadable, or
    /// ModelImporter's animations.isReadable.</summary>
    Readable = 1 << 0,

    /// <summary>TextureImporter mipmaps.enableMipMap.</summary>
    Mipmaps = 1 << 1,

    /// <summary>Imported in a compressed form — TextureImporter's default-platform
    /// textureCompression != Uncompressed, or AudioImporter's compressionFormat != PCM.</summary>
    Compressed = 1 << 2,

    /// <summary>TextureImporter mipmaps.sRGBTexture — colour space, not compression.</summary>
    SRGB = 1 << 3,

    /// <summary>TextureImporter streamingMipmaps.</summary>
    Streaming = 1 << 4,

    /// <summary>AudioImporter forceToMono.</summary>
    ForceToMono = 1 << 5,
}
