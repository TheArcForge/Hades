using Hades.Core.Unity;

namespace Hades.Core.Tests.Unity;

public class MetaFileReaderTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    string WriteMeta(string assetName, string contents)
    {
        Directory.CreateDirectory(_dir);
        var assetPath = Path.Combine(_dir, assetName);
        File.WriteAllText(assetPath, "asset body");
        File.WriteAllText(assetPath + ".meta", contents);
        return assetPath;
    }

    [Fact]
    public void ReadsTheGuidFromASiblingMetaFile()
    {
        var asset = WriteMeta("Player.prefab", "fileFormatVersion: 2\nguid: 9848f30f74d55944087b9a0aafbe0e75\n");
        Assert.Equal("9848f30f74d55944087b9a0aafbe0e75", MetaFileReader.TryReadGuid(asset));
    }

    [Fact]
    public void NormalisesGuidsToLowercase()
    {
        var asset = WriteMeta("Player.prefab", "guid: 9848F30F74D55944087B9A0AAFBE0E75\n");
        Assert.Equal("9848f30f74d55944087b9a0aafbe0e75", MetaFileReader.TryReadGuid(asset));
    }

    [Fact]
    public void ReturnsNullWhenThereIsNoMetaFile()
    {
        Directory.CreateDirectory(_dir);
        var orphan = Path.Combine(_dir, "NoMeta.prefab");
        File.WriteAllText(orphan, "body");
        Assert.Null(MetaFileReader.TryReadGuid(orphan));
    }

    [Fact]
    public void ReturnsNullWhenTheMetaHasNoGuid()
    {
        Assert.Null(MetaFileReader.TryReadGuid(WriteMeta("Odd.prefab", "fileFormatVersion: 2\n")));
    }

    [Fact]
    public void ReadsRealMetaFilesFromTheCorpus()
    {
        const string corpus = "/Users/mike/Projects/project_aurora/Assets";
        if (!Directory.Exists(corpus)) return;

        var withGuid = Directory.EnumerateFiles(corpus, "*.prefab", SearchOption.AllDirectories)
            .Take(50)
            .Count(f => MetaFileReader.TryReadGuid(f) is not null);

        Assert.True(withGuid >= 45, $"only {withGuid}/50 real prefabs resolved a GUID");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
