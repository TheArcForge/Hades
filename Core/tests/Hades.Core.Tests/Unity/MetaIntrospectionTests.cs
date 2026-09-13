using Hades.Core.Reading;
using Hades.Core.Unity;

namespace Hades.Core.Tests.Unity;

/// <summary>
/// DESIGN-07 Tier 1. §13.1's unknown-value rule is the first thing pinned here (before any real
/// decode table), per that section's own instruction — a wrong decode is the confident-lie
/// failure this feature exists to prevent.
/// </summary>
public class MetaIntrospectionTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    // ReadThrough.GetImportSettings resolves through ProjectWalker's scan roots, which require an
    // "Assets" directory to exist — unlike MetaFileReaderTests (which reads an absolute path
    // directly, no scan-root check needed), fixtures here must live under "Assets/".
    string WriteMeta(string assetName, string contents)
    {
        var assetPath = Path.Combine(_dir, "Assets", assetName);
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        File.WriteAllText(assetPath, "asset body");
        File.WriteAllText(assetPath + ".meta", contents);
        return assetPath;
    }

    ImportSettingsInfo ReadSettings(string assetPath) =>
        ReadThrough.GetImportSettings(_dir, "Assets/" + Path.GetFileName(assetPath));

    // ---------------------------------------------------------------- §13.1: the unknown-value rule, first

    [Fact]
    public void DecodeEnum_UnknownValue_ReturnsTheRawIntegerNeverAGuess()
    {
        var table = new Dictionary<int, string> { [0] = "Known" };
        Assert.Equal("47", MetaIntrospection.DecodeEnum("47", table));
    }

    [Fact]
    public void DecodeEnum_KnownValue_ReturnsItsName()
    {
        var table = new Dictionary<int, string> { [0] = "Known" };
        Assert.Equal("Known", MetaIntrospection.DecodeEnum("0", table));
    }

    [Fact]
    public void DecodeEnum_NonIntegerRaw_ReturnsItVerbatimRatherThanThrowing()
    {
        var table = new Dictionary<int, string> { [0] = "Known" };
        Assert.Equal("garbage", MetaIntrospection.DecodeEnum("garbage", table));
    }

    [Fact]
    public void DecodeEnum_NullRaw_ReturnsNull()
    {
        var table = new Dictionary<int, string> { [0] = "Known" };
        Assert.Null(MetaIntrospection.DecodeEnum(null, table));
    }

    // ---------------------------------------------------------------- TextureImporter

    [Fact]
    public void DecodesATextureImporterBlock()
    {
        var asset = WriteMeta("Rock.png", """
            fileFormatVersion: 2
            guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
            TextureImporter:
              mipmaps:
                enableMipMap: 0
                sRGBTexture: 1
              isReadable: 0
              streamingMipmaps: 0
              alphaIsTransparency: 1
              alphaUsage: 1
              maxTextureSize: 2048
              textureType: 2
              platformSettings:
              - buildTarget: DefaultTexturePlatform
                textureCompression: 0
                compressionQuality: 50
                crunchedCompression: 0
                overridden: 0
              - buildTarget: Standalone
                textureCompression: 1
                compressionQuality: 50
                crunchedCompression: 0
                overridden: 0
            """);

        var result = MetaIntrospection.Decode(ReadSettings(asset));

        Assert.Equal("TextureImporter", result.ImporterType);
        Assert.Equal("GUI", result.TextureType); // textureType: 2
        Assert.Equal(2048, result.MaxTextureSize);
        Assert.True(result.SRGB);
        Assert.False(result.GenerateMipMaps);
        Assert.False(result.ReadWriteEnabled);
        Assert.False(result.StreamingMipmaps);
        Assert.True(result.AlphaIsTransparency);
        Assert.Equal("FromInput", result.AlphaSource); // alphaUsage: 1

        Assert.NotNull(result.Compression);
        Assert.Equal(2, result.Compression!.Count);
        Assert.Equal("DefaultTexturePlatform", result.Compression[0].BuildTarget);
        Assert.False(result.Compression[0].Compressed);
        Assert.Equal("Standalone", result.Compression[1].BuildTarget);
        Assert.True(result.Compression[1].Compressed);
        Assert.Equal("Compressed", result.Compression[1].CompressionLevel);
    }

    [Fact]
    public void ATextureTypeOutsideTheKnownTable_ReportsTheRawIntegerNeverADefault()
    {
        var asset = WriteMeta("Weird.png", """
            fileFormatVersion: 2
            guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
            TextureImporter:
              textureType: 999
            """);

        var result = MetaIntrospection.Decode(ReadSettings(asset));

        Assert.Equal("999", result.TextureType);
    }

    // ---------------------------------------------------------------- ModelImporter

    [Fact]
    public void DecodesAModelImporterBlockIncludingTheRigType()
    {
        // "Human" is the ACTUAL enum member name (verified against Unity 6000.3.2f1's own shipped
        // assembly - see MetaIntrospection's class doc); reported as "Humanoid" to match the
        // vocabulary DESIGN-07 §8.2 and Unity's own Inspector UI both use.
        var asset = WriteMeta("Idle.fbx", """
            fileFormatVersion: 2
            guid: cccccccccccccccccccccccccccccccc
            ModelImporter:
              animations:
                isReadable: 0
                clipAnimations:
                - name: mixamo.com
                  firstFrame: 0
                  lastFrame: 59
                  loopTime: 1
              materials:
                materialImportMode: 1
              meshes:
                meshCompression: 0
              humanDescription:
                skeleton:
                - name: Hips
                  parentName:
                - name: Spine
                  parentName: Hips
              animationType: 3
              avatarSetup: 1
            """);

        var result = MetaIntrospection.Decode(ReadSettings(asset));

        Assert.Equal("Humanoid", result.RigType);
        Assert.Equal("CreateFromThisModel", result.AvatarSetup);
        Assert.False(result.ModelIsReadable);
        Assert.Equal("Off", result.MeshCompression);
        Assert.Equal("ImportStandard", result.MaterialImportMode);
        Assert.Equal(2, result.BoneCount);

        Assert.NotNull(result.Clips);
        var clip = Assert.Single(result.Clips!);
        Assert.Equal("mixamo.com", clip.Name);
        Assert.Equal(0, clip.FirstFrame);
        Assert.Equal(59, clip.LastFrame);
        Assert.True(clip.Loops);
    }

    [Fact]
    public void AGenericRigReportsGenericNotHumanoid()
    {
        var asset = WriteMeta("Prop.fbx", """
            fileFormatVersion: 2
            guid: dddddddddddddddddddddddddddddddd
            ModelImporter:
              animationType: 2
            """);

        Assert.Equal("Generic", MetaIntrospection.Decode(ReadSettings(asset)).RigType);
    }

    // ---------------------------------------------------------------- AudioImporter

    [Fact]
    public void DecodesAnAudioImporterBlock()
    {
        var asset = WriteMeta("Hit.wav", """
            fileFormatVersion: 2
            guid: eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee
            AudioImporter:
              defaultSettings:
                loadType: 0
                sampleRateSetting: 0
                sampleRateOverride: 44100
                compressionFormat: 1
                quality: 1
              forceToMono: 0
              normalize: 1
              preloadAudioData: 1
              loadInBackground: 0
              ambisonic: 0
              3D: 1
            """);

        var result = MetaIntrospection.Decode(ReadSettings(asset));

        Assert.Equal("DecompressOnLoad", result.LoadType);
        Assert.Equal("Vorbis", result.AudioCompressionFormat);
        Assert.Equal("PreserveSampleRate", result.SampleRateSetting);
        // sampleRateOverride is inert unless sampleRateSetting is OverrideSampleRate (2) - here
        // it is 0 (PreserveSampleRate), so the override must NOT be reported as "the" sample rate.
        Assert.Null(result.SampleRateOverride);
        Assert.False(result.ForceToMono);
        Assert.True(result.Normalize);
        Assert.True(result.PreloadAudioData);
        Assert.False(result.LoadInBackground);
        Assert.False(result.Ambisonic);
        Assert.True(result.Is3D);
    }

    [Fact]
    public void AnOverriddenSampleRateIsReportedOnlyWhenTheSettingSaysToOverride()
    {
        var asset = WriteMeta("Hit2.wav", """
            fileFormatVersion: 2
            guid: ffffffffffffffffffffffffffffffff
            AudioImporter:
              defaultSettings:
                sampleRateSetting: 2
                sampleRateOverride: 22050
            """);

        var result = MetaIntrospection.Decode(ReadSettings(asset));

        Assert.Equal("OverrideSampleRate", result.SampleRateSetting);
        Assert.Equal(22050, result.SampleRateOverride);
    }

    // ---------------------------------------------------------------- unknown/unsupported importer

    [Fact]
    public void AnImporterThisFileDoesNotDecode_ReportsTheImporterNameAndNothingElse()
    {
        // Fonts are deliberately cut per DESIGN-07 §4 - never guessed at.
        var asset = WriteMeta("Body.ttf", """
            fileFormatVersion: 2
            guid: 11111111111111111111111111111111
            TrueTypeFontImporter:
              fontSize: 16
            """);

        var result = MetaIntrospection.Decode(ReadSettings(asset));

        Assert.Equal("TrueTypeFontImporter", result.ImporterType);
        Assert.Null(result.TextureType);
        Assert.Null(result.RigType);
        Assert.Null(result.LoadType);
    }

    // ---------------------------------------------------------------- bounded index-time scan (§7.4)

    [Fact]
    public void ScanForIndexing_FindsFieldsPastAHumanDescriptionOfAnySize()
    {
        // §7.4's own real-file finding: animationType sits AFTER humanDescription.skeleton, whose
        // size scales with bone count - a fixed byte-prefix cap would miss it for any non-trivial
        // rig. Simulated here with a synthetic 200-bone skeleton (far more than the ~60 in the
        // real corpus file that exposed this), asserting the scan still finds animationType.
        var skeleton = string.Concat(Enumerable.Range(0, 200)
            .Select(i => $"    - name: Bone{i}\n      parentName: Bone{i - 1}\n"));
        var content = "fileFormatVersion: 2\nguid: 22222222222222222222222222222222\nModelImporter:\n"
            + "  humanDescription:\n    skeleton:\n" + skeleton
            + "  animationType: 3\n  avatarSetup: 0\n";

        var fields = MetaIntrospection.ScanForIndexing(content);

        Assert.NotNull(fields);
        Assert.Equal("Humanoid", fields!.AssetSubtype);
    }

    [Fact]
    public void ScanForIndexing_ComputesTheSameSubtypeAsTheFullDecode()
    {
        var asset = WriteMeta("Rock2.png", """
            fileFormatVersion: 2
            guid: 33333333333333333333333333333333
            TextureImporter:
              textureType: 8
              isReadable: 1
              mipmaps:
                enableMipMap: 1
                sRGBTexture: 0
            """);

        var full = MetaIntrospection.Decode(ReadSettings(asset));
        var scanned = MetaIntrospection.ScanForIndexing(File.ReadAllText(asset + ".meta"));

        Assert.Equal(full.TextureType, scanned!.AssetSubtype);
        Assert.Equal("Sprite", scanned.AssetSubtype);
        Assert.Equal(AssetFlags.Readable | AssetFlags.Mipmaps, scanned.Flags);
    }

    [Fact]
    public void ScanForIndexing_NoImporterBlock_ReturnsNull()
    {
        Assert.Null(MetaIntrospection.ScanForIndexing("fileFormatVersion: 2\nguid: 44444444444444444444444444444444\n"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
