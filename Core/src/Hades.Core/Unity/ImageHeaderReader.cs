using System.Buffers.Binary;

namespace Hades.Core.Unity;

/// <summary>
/// DESIGN-07 Tier 2 — a texture's measured pixel dimensions, sniffed from the first ≤ 64 KB of
/// the file's own bytes. This is the one thing the design adds beyond surfacing what already
/// exists: `.meta`'s <c>maxTextureSize</c> is an import CEILING Unity was configured with, never a
/// measurement (a 64×64 icon and an 8192×8192 texture can both read 2048 under default settings) —
/// see <see cref="MetaIntrospection"/> for that side. Pure managed, fixed-offset arithmetic over a
/// <see cref="ReadOnlySpan{Byte}"/> — no codec, no pixel decode, no new package reference, and
/// byte-identical on every RID by construction because every multi-byte read below names its
/// endianness explicitly via <see cref="BinaryPrimitives"/> rather than inheriting the host's.
///
/// <para><b>Dispatch order (DESIGN-07 §2.5) — content wins over extension, always, with exactly
/// one exception:</b></para>
/// <list type="number">
/// <item><description>A Git LFS pointer file is sniffed FIRST — a &lt;200-byte ASCII stub that is
/// otherwise indistinguishable from a corrupt/truncated header, and whose correct fix
/// (<c>git lfs pull</c>) is nothing like a corrupt file's.</description></item>
/// <item><description>Magic-byte dispatch for PNG / JPEG / PSD — content identifies the format
/// regardless of what the extension claims. A `.png` that is really a JPEG (a real, in-corpus
/// case — see the class doc history in DESIGN-07 §2.5) reads correctly as JPEG.</description></item>
/// <item><description>TGA — LAST, and ONLY for a literal ".tga" extension, because TGA has no
/// header magic at all (its first bytes are small integers that look like anything). Gated on a
/// plausibility check (image type, colour-map consistency, width/height range); failing it
/// returns nulls plus a reason NAMING TGA specifically, never a guess. A real TGA saved under a
/// lying (non-.tga) extension is therefore unidentified BY DESIGN — the accepted cost of the
/// format having no magic, not an oversight (see this method's own failure-mode table in
/// DESIGN-07 §9).</description></item>
/// </list>
/// </summary>
public static class ImageHeaderReader
{
    /// <summary>Every read is bounded to this many bytes regardless of the file's actual size —
    /// no streaming, no full read, so a multi-hundred-megabyte texture costs the same as a tiny
    /// one.</summary>
    public const int MaxReadBytes = 64 * 1024;

    const string LfsPointerPrefix = "version https://git-lfs.github.com/spec/v1";

    /// <summary>Reads up to <see cref="MaxReadBytes"/> from <paramref name="absolutePath"/> and
    /// dispatches on content. IO failures (missing file, permission denial, ...) surface as a
    /// named, honest <see cref="ImageHeaderResult.UnavailableReason"/> rather than throwing — this
    /// is a best-effort enrichment, not a required read, and BinaryAssetIndexer must never fail an
    /// asset's whole node over it.</summary>
    public static ImageHeaderResult Read(string absolutePath)
    {
        byte[] buffer;
        try
        {
            using var stream = File.OpenRead(absolutePath);
            var toRead = (int)Math.Min(MaxReadBytes, stream.Length);
            buffer = new byte[toRead];
            stream.ReadExactly(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure($"'{Path.GetFileName(absolutePath)}' could not be read: {ex.Message}");
        }

        return Read(buffer, Path.GetExtension(absolutePath));
    }

    /// <summary>The pure decode, over bytes already in hand — what
    /// <see cref="Read(string)"/> and every unit test both call through, so a test never has to
    /// touch disk to pin the dispatch order.</summary>
    public static ImageHeaderResult Read(ReadOnlySpan<byte> data, string extensionHint)
    {
        if (data.Length == 0) return Failure("the file is empty.");
        if (LooksLikeLfsPointer(data))
        {
            return Failure(
                "this is a Git LFS pointer file, not the actual image content - the real file has "
                + "not been downloaded (run 'git lfs pull'), so its dimensions cannot be read.");
        }

        if (TryPng(data, out var png)) return png!;
        if (TryJpeg(data, out var jpeg)) return jpeg!;
        if (TryPsd(data, out var psd)) return psd!;

        // TGA has no header magic (§2.5) - reached only as an extension-gated LAST RESORT, after
        // every content-based identification above has already failed. Never attempted for any
        // other extension: running this plausibility heuristic against arbitrary unidentified
        // bytes would let random binary data be misread as a TGA header.
        if (string.Equals(extensionHint, ".tga", StringComparison.OrdinalIgnoreCase))
        {
            if (TryTga(data, out var tga)) return tga!;

            return Failure(
                "this file claims a .tga extension but carries no recognised image magic, and its "
                + "header did not pass the plausibility check TGA needs before being trusted (image "
                + "type, colour-map consistency, and 1-32768 width/height) - it may be corrupt, or a "
                + "different format entirely under a misleading extension.");
        }

        return Failure(
            "Hades reads image dimensions only from PNG, JPEG, TGA, and PSD headers (sniffed from "
            + "content, not the extension) - this file's content was not recognised as any of "
            + "those.");
    }

    static ImageHeaderResult Failure(string reason) => new() { UnavailableReason = reason };

    static bool LooksLikeLfsPointer(ReadOnlySpan<byte> data)
    {
        var prefix = "version https://git-lfs.github.com/spec/v1"u8;
        return data.Length < 200 && data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);
    }

    // ---------------------------------------------------------------- PNG

    static bool TryPng(ReadOnlySpan<byte> data, out ImageHeaderResult? result)
    {
        result = null;
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        if (data.Length < 26 || !data[..8].SequenceEqual(signature)) return false;
        if (!data.Slice(12, 4).SequenceEqual("IHDR"u8)) return false; // not the chunk we expect right after the signature

        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        var bitDepth = data[24];
        var colourType = data[25];

        // PNG spec §11.2.2: 0 grayscale (1 channel), 2 RGB (3), 3 palette (1 index channel),
        // 4 grayscale+alpha (2), 6 RGBA (4). 1 and 5 are not defined.
        int? channels = colourType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => null };

        result = new ImageHeaderResult { Format = "PNG", Width = width, Height = height, Channels = channels, BitDepth = bitDepth };
        return true;
    }

    // ---------------------------------------------------------------- JPEG

    static bool TryJpeg(ReadOnlySpan<byte> data, out ImageHeaderResult? result)
    {
        result = null;
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8 || data[2] != 0xFF) return false;

        var pos = 2; // positioned at the 0xFF that opens the next marker after SOI
        while (pos < data.Length)
        {
            if (data[pos] != 0xFF) { pos++; continue; } // resync - should not normally happen

            var markerPos = pos;
            while (markerPos < data.Length && data[markerPos] == 0xFF) markerPos++; // skip 0xFF fill bytes
            if (markerPos >= data.Length) break;

            var marker = data[markerPos];
            pos = markerPos + 1;

            if (marker == 0xD8) continue; // SOI (already past it)
            if (marker == 0xD9) break; // EOI - no SOF found before the end of the image
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue; // TEM / RSTn - no length/payload

            if (pos + 2 > data.Length) break; // truncated before this segment's length field
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));

            // SOF0-SOF15 (0xC0-0xCF) EXCLUDING DHT (0xC4), JPG extension (0xC8), and DAC (0xCC) -
            // those three share the SOF numeric range but are not start-of-frame segments.
            var isSofMarker = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;

            if (isSofMarker)
            {
                if (pos + 8 > data.Length) break; // truncated SOF payload
                var precision = data[pos + 2];
                var height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos + 5, 2));
                var components = data[pos + 7];

                result = new ImageHeaderResult { Format = "JPEG", Width = width, Height = height, Channels = components, BitDepth = precision };
                return true;
            }

            if (length < 2 || pos + length > data.Length) break; // malformed/truncated segment
            pos += length;
        }

        return false;
    }

    // ---------------------------------------------------------------- PSD

    static bool TryPsd(ReadOnlySpan<byte> data, out ImageHeaderResult? result)
    {
        result = null;
        if (data.Length < 26 || !data[..4].SequenceEqual("8BPS"u8)) return false;

        var channels = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(12, 2));
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(14, 4));
        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(18, 4));
        var depth = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(22, 2));

        result = new ImageHeaderResult { Format = "PSD", Width = width, Height = height, Channels = channels, BitDepth = depth };
        return true;
    }

    // ---------------------------------------------------------------- TGA (no magic - extension-gated last resort)

    static bool TryTga(ReadOnlySpan<byte> data, out ImageHeaderResult? result)
    {
        result = null;
        if (data.Length < 18) return false;

        var colourMapType = data[1];
        var imageType = data[2];

        // Recognised (possibly compressed) colour-mapped/true-colour/grayscale image types only -
        // 0 (no image data) and the run-length variants' unlisted siblings are excluded on
        // purpose: a plausible-looking header for a format Hades cannot usefully report on is
        // still an honest "not identified", not a guess.
        if (imageType is not (1 or 2 or 3 or 9 or 10 or 11)) return false;
        if (colourMapType is not (0 or 1)) return false;

        var isColourMappedType = imageType is 1 or 9;
        if (isColourMappedType != (colourMapType == 1)) return false; // type/colour-map disagree - not a real TGA

        var width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(12, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(14, 2));
        if (width is < 1 or > 32768 || height is < 1 or > 32768) return false;

        var bitsPerPixel = data[16];

        result = new ImageHeaderResult { Format = "TGA", Width = width, Height = height, BitDepth = bitsPerPixel };
        return true;
    }
}

/// <summary>
/// One <see cref="ImageHeaderReader"/> call's result. <see cref="Width"/>/<see cref="Height"/>/
/// <see cref="Format"/> are set together or not at all — when the header could not be read,
/// <see cref="UnavailableReason"/> names why, in the same "name the file, name the reason, never
/// return a well-formed zero" register DESIGN-07 §9 documents for every other read-through
/// failure mode.
/// </summary>
public sealed record ImageHeaderResult
{
    /// <summary>The SNIFFED format ("PNG"/"JPEG"/"TGA"/"PSD") - from content, never from the
    /// caller-supplied extension. Null when unavailable.</summary>
    public string? Format { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? Channels { get; init; }
    public int? BitDepth { get; init; }

    /// <summary>Null when <see cref="Format"/>/<see cref="Width"/>/<see cref="Height"/> are set;
    /// otherwise names the file-specific reason dimensions are unavailable — an LFS pointer, an
    /// unrecognised format, or a .tga that failed its plausibility check. Never both null.</summary>
    public string? UnavailableReason { get; init; }
}
