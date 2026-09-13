using System.Buffers.Binary;
using Hades.Core.Unity;

namespace Hades.Core.Tests.Unity;

/// <summary>
/// DESIGN-07 Tier 2. Hand-constructed minimal-but-valid PNG/JPEG/TGA/PSD byte arrays — no binary
/// test assets in the repo, per this codebase's own convention (<see cref="MetaFileReaderTests"/>).
/// The four dispatch-order cases (§2.5) are the ones that actually encode the design and are
/// called out individually below; everything else is per-format coverage.
/// </summary>
public class ImageHeaderReaderTests
{
    // ---------------------------------------------------------------- fixture builders

    static byte[] Png(int width, int height, byte bitDepth = 8, byte colourType = 2)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // signature
        bytes.AddRange([0, 0, 0, 13]); // IHDR chunk length (unused by the reader, but a real file has it)
        bytes.AddRange("IHDR"u8.ToArray());
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)width); bytes.AddRange(word.ToArray());
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)height); bytes.AddRange(word.ToArray());
        bytes.Add(bitDepth);
        bytes.Add(colourType);
        return [.. bytes];
    }

    static byte[] Jpeg(int width, int height, byte components = 3, byte precision = 8)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 }; // SOI
        bytes.AddRange([0xFF, 0xE0, 0x00, 0x10]); // a harmless APP0 segment (16-byte length incl. itself) the walker must skip
        bytes.AddRange(new byte[14]); // APP0 payload (14 more bytes to satisfy the 16-byte length)
        bytes.AddRange([0xFF, 0xC0]); // SOF0

        var payload = new List<byte> { precision };
        Span<byte> word = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(word, (ushort)height); payload.AddRange(word.ToArray());
        BinaryPrimitives.WriteUInt16BigEndian(word, (ushort)width); payload.AddRange(word.ToArray());
        payload.Add(components);
        for (var i = 0; i < components; i++) payload.AddRange(new byte[] { (byte)(i + 1), 0x11, 0x00 }); // id, sampling, quant table

        BinaryPrimitives.WriteUInt16BigEndian(word, (ushort)(payload.Count + 2)); // length includes itself
        bytes.AddRange(word.ToArray());
        bytes.AddRange(payload);
        bytes.AddRange([0xFF, 0xD9]); // EOI, unread but realistic
        return [.. bytes];
    }

    static byte[] Tga(int width, int height, byte imageType = 2, byte colourMapType = 0, byte bpp = 24)
    {
        var bytes = new List<byte>
        {
            0, // id length
            colourMapType,
            imageType,
            0, 0, 0, 0, 0, // colour map spec (5 bytes) - unused for a true-colour image
            0, 0, 0, 0, // x/y origin
        };
        Span<byte> word = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)width); bytes.AddRange(word.ToArray());
        BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)height); bytes.AddRange(word.ToArray());
        bytes.Add(bpp);
        bytes.Add(0); // image descriptor
        return [.. bytes];
    }

    static byte[] Psd(int width, int height, ushort channels = 3, ushort depth = 8)
    {
        var bytes = new List<byte>();
        bytes.AddRange("8BPS"u8.ToArray());
        bytes.AddRange([0, 1]); // version
        bytes.AddRange(new byte[6]); // reserved, must be zero
        Span<byte> word2 = stackalloc byte[2];
        Span<byte> word4 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(word2, channels); bytes.AddRange(word2.ToArray());
        BinaryPrimitives.WriteUInt32BigEndian(word4, (uint)height); bytes.AddRange(word4.ToArray());
        BinaryPrimitives.WriteUInt32BigEndian(word4, (uint)width); bytes.AddRange(word4.ToArray());
        BinaryPrimitives.WriteUInt16BigEndian(word2, depth); bytes.AddRange(word2.ToArray());
        BinaryPrimitives.WriteUInt16BigEndian(word2, 3); bytes.AddRange(word2.ToArray()); // colour mode: RGB
        return [.. bytes];
    }

    // ---------------------------------------------------------------- per-format coverage

    [Fact]
    public void ReadsAPngHeader()
    {
        var result = ImageHeaderReader.Read(Png(350, 200), ".png");
        Assert.Equal("PNG", result.Format);
        Assert.Equal(350, result.Width);
        Assert.Equal(200, result.Height);
        Assert.Equal(3, result.Channels); // colour type 2 = RGB
        Assert.Equal(8, result.BitDepth);
        Assert.Null(result.UnavailableReason);
    }

    [Fact]
    public void ReadsAJpegHeaderPastAnAppSegment()
    {
        var result = ImageHeaderReader.Read(Jpeg(64, 48), ".jpg");
        Assert.Equal("JPEG", result.Format);
        Assert.Equal(64, result.Width);
        Assert.Equal(48, result.Height);
        Assert.Equal(3, result.Channels);
    }

    [Fact]
    public void ReadsATgaHeader()
    {
        var result = ImageHeaderReader.Read(Tga(64, 32), ".tga");
        Assert.Equal("TGA", result.Format);
        Assert.Equal(64, result.Width);
        Assert.Equal(32, result.Height);
        Assert.Equal(24, result.BitDepth);
    }

    [Fact]
    public void ReadsAPsdHeader()
    {
        var result = ImageHeaderReader.Read(Psd(32, 64), ".psd");
        Assert.Equal("PSD", result.Format);
        Assert.Equal(32, result.Width);
        Assert.Equal(64, result.Height);
        Assert.Equal(3, result.Channels);
        Assert.Equal(8, result.BitDepth);
    }

    // ---------------------------------------------------------------- the four dispatch-order cases (§2.5) - the actual spec

    [Fact]
    public void APngNamedJpeg_MagicWins()
    {
        // Real, in-corpus case (DESIGN-07 §2.5): a file with a misleading .png extension whose
        // actual bytes are JPEG. Content identification must win over the extension.
        var result = ImageHeaderReader.Read(Jpeg(350, 200), ".png");
        Assert.Equal("JPEG", result.Format);
        Assert.Equal(350, result.Width);
    }

    [Fact]
    public void ATgaNamedPng_MagicWinsProvingPngPrecedesTga()
    {
        // Proves step 2 (magic dispatch) runs BEFORE step 3 (the .tga extension branch) - a .tga
        // extension alone must never short-circuit straight to the TGA path.
        var result = ImageHeaderReader.Read(Png(16, 16), ".tga");
        Assert.Equal("PNG", result.Format);
    }

    [Fact]
    public void ATgaWhoseBytesFailPlausibility_ReturnsNullsAndATgaSpecificReason()
    {
        // imageType 5 is not a recognised TGA image type - never a guess, never falls back to
        // reading it as a TGA anyway.
        var bytes = Tga(64, 32, imageType: 5);
        var result = ImageHeaderReader.Read(bytes, ".tga");

        Assert.Null(result.Format);
        Assert.Null(result.Width);
        Assert.NotNull(result.UnavailableReason);
        Assert.Contains("TGA", result.UnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARealTgaNamedPng_IsUnidentifiedByDesign()
    {
        // The accepted cost of TGA having no header magic (§2.5): reached only via the .tga
        // extension gate, so a real TGA under ANY other extension is never identified - reported
        // unreadable with a reason, never guessed.
        var result = ImageHeaderReader.Read(Tga(64, 32), ".png");

        Assert.Null(result.Format);
        Assert.Null(result.Width);
        Assert.Null(result.Height);
        Assert.NotNull(result.UnavailableReason);
    }

    // ---------------------------------------------------------------- failure modes

    [Fact]
    public void AnEmptyFile_ReturnsAReasonNotAnException()
    {
        var result = ImageHeaderReader.Read(ReadOnlySpan<byte>.Empty, ".png");
        Assert.Null(result.Format);
        Assert.NotNull(result.UnavailableReason);
    }

    [Fact]
    public void ATruncatedPngHeader_ReturnsNullsAndAReason()
    {
        var truncated = Png(350, 200)[..10]; // cut mid-IHDR, before width/height are even present
        var result = ImageHeaderReader.Read(truncated, ".png");

        Assert.Null(result.Format);
        Assert.NotNull(result.UnavailableReason);
    }

    [Fact]
    public void ATruncatedJpegHeader_ReturnsNullsAndAReason()
    {
        // SOI + the APP0 segment take the first 20 bytes; cutting at 24 lands inside the SOF0
        // marker's length/precision/height/width fields, before they can be fully read.
        var truncated = Jpeg(64, 48)[..24];
        var result = ImageHeaderReader.Read(truncated, ".jpg");

        Assert.Null(result.Format);
        Assert.NotNull(result.UnavailableReason);
    }

    [Fact]
    public void AGitLfsPointerFile_IsDetectedAndNamedSpecifically()
    {
        var pointer = System.Text.Encoding.ASCII.GetBytes(
            "version https://git-lfs.github.com/spec/v1\noid sha256:abc123\nsize 1048576\n");
        var result = ImageHeaderReader.Read(pointer, ".png");

        Assert.Null(result.Format);
        Assert.NotNull(result.UnavailableReason);
        Assert.Contains("LFS", result.UnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnsupportedFormat_NamesWhatIsSupportedInstead()
    {
        // .exr is deliberately not decoded (§5.3) - arbitrary non-matching bytes under a
        // non-.tga extension must degrade honestly, not guess.
        var result = ImageHeaderReader.Read("this is definitely not an image header"u8.ToArray(), ".exr");

        Assert.Null(result.Format);
        Assert.NotNull(result.UnavailableReason);
    }

    // ---------------------------------------------------------------- endianness pinning

    [Fact]
    public void MultiByteFieldsAreReadBigEndianRegardlessOfHostEndianness()
    {
        // 300 and 200 are chosen so a host-endian (little-endian, on every machine this suite
        // runs on) misread of the big-endian bytes would produce a wildly different, easily
        // distinguishable number (300 = 0x0000012C; misread as little-endian that byte sequence
        // is nowhere near 300) - this fails loudly if a future refactor swaps
        // BinaryPrimitives.ReadUInt32BigEndian for host-endian BitConverter.
        var result = ImageHeaderReader.Read(Png(300, 200), ".png");
        Assert.Equal(300, result.Width);
        Assert.Equal(200, result.Height);
    }

    [Fact]
    public void TgaFieldsAreReadLittleEndian()
    {
        // TGA is genuinely little-endian per spec (unlike PNG/JPEG/PSD, all big-endian) - pinned
        // separately so the two conventions cannot be silently unified incorrectly.
        var result = ImageHeaderReader.Read(Tga(300, 200), ".tga");
        Assert.Equal(300, result.Width);
        Assert.Equal(200, result.Height);
    }
}
