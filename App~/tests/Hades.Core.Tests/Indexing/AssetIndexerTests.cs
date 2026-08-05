using Hades.Core.Graph;
using Hades.Core.Indexing;

namespace Hades.Core.Tests.Indexing;

public class AssetIndexerTests : IDisposable
{
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

    void Write(string relativePath, string contents)
    {
        var full = Path.Combine(_projectRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
    }

    void WriteAsset(string relativePath, string body, string? guid = null)
    {
        Write(relativePath, Header + body);
        if (guid is not null) Write(relativePath + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
    }

    GraphDatabase OpenGraph() => GraphDatabase.Open(Path.Combine(_projectRoot, "graph.db"));

    [Fact]
    public void IndexesAPrefabsGameObjects()
    {
        WriteAsset("Assets/Player.prefab",
            "--- !u!1 &111\nGameObject:\n  m_Name: Player\n--- !u!4 &222\nTransform:\n  m_GameObject: {fileID: 111}\n",
            guid: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        using var db = OpenGraph();

        var result = AssetIndexer.IndexProject(_projectRoot, db);

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(2, result.TypesFound);
        var player = Assert.Single(db.SearchByName("Player"));
        Assert.Equal("GameObject", player.Kind);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", player.Guid);
        Assert.Equal(111L, player.FileId);
    }

    [Fact]
    public void ObjectsOfTheSameTypeInOneFileStayDistinct()
    {
        // Components have no m_Name, so identity must include fileID — otherwise every Transform
        // in a prefab collapses onto one row. Measured: without this, 24,899 real objects
        // collapsed to 5,871 nodes.
        WriteAsset("Assets/Multi.prefab",
            "--- !u!4 &1\nTransform:\n  m_Enabled: 1\n--- !u!4 &2\nTransform:\n  m_Enabled: 1\n--- !u!4 &3\nTransform:\n  m_Enabled: 1\n",
            guid: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        using var db = OpenGraph();

        AssetIndexer.IndexProject(_projectRoot, db);

        Assert.Equal(3, db.SearchByName("Transform").Count);
    }

    [Fact]
    public void AMonoBehavioursScriptBecomesAnEdgeCarryingTheTargetGuid()
    {
        WriteAsset("Assets/Thing.prefab",
            "--- !u!114 &333\nMonoBehaviour:\n  m_Script: {fileID: 11500000, guid: cccccccccccccccccccccccccccccccc, type: 3}\n",
            guid: "dddddddddddddddddddddddddddddddd");
        using var db = OpenGraph();

        AssetIndexer.IndexProject(_projectRoot, db);

        var edge = Assert.Single(db.EdgesFrom("Assets/Thing.prefab", 333));
        Assert.Equal("cccccccccccccccccccccccccccccccc", edge.ToGuid);
        Assert.Equal("m_Script", edge.PropertyPath);
    }

    [Fact]
    public void LocalReferencesInheritTheOwningAssetsGuid()
    {
        // A {fileID: N} with no guid points inside this same asset, so the edge must carry this
        // asset's GUID rather than null — otherwise it is unresolvable at query time.
        WriteAsset("Assets/Local.prefab",
            "--- !u!4 &222\nTransform:\n  m_GameObject: {fileID: 111}\n",
            guid: "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        using var db = OpenGraph();

        AssetIndexer.IndexProject(_projectRoot, db);

        Assert.Equal("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            Assert.Single(db.EdgesFrom("Assets/Local.prefab", 222)).ToGuid);
    }

    [Fact]
    public void SkipsBinaryAssetsWithoutWarning()
    {
        // Force Text does not make every asset text: Unity writes LightingData.asset as binary
        // regardless. 9 such files exist in the measured corpus.
        Write("Assets/LightingData.asset", "\0\0\0binarydata");
        WriteAsset("Assets/Good.prefab", "--- !u!1 &1\nGameObject:\n  m_Name: Good\n", guid: "ffffffffffffffffffffffffffffffff");
        using var db = OpenGraph();

        var result = AssetIndexer.IndexProject(_projectRoot, db);

        Assert.Empty(result.Warnings);
        Assert.Single(db.SearchByName("Good"));
    }

    [Fact]
    public void ReindexingAfterDeletionRemovesNodesAndEdges()
    {
        WriteAsset("Assets/Gone.prefab",
            "--- !u!114 &1\nMonoBehaviour:\n  m_Script: {fileID: 1, guid: cccccccccccccccccccccccccccccccc, type: 3}\n",
            guid: "11111111111111111111111111111111");
        WriteAsset("Assets/Stay.prefab", "--- !u!1 &2\nGameObject:\n  m_Name: Stay\n", guid: "22222222222222222222222222222222");
        using var db = OpenGraph();
        AssetIndexer.IndexProject(_projectRoot, db);
        Assert.Single(db.EdgesFrom("Assets/Gone.prefab", 1));

        File.Delete(Path.Combine(_projectRoot, "Assets/Gone.prefab"));
        File.Delete(Path.Combine(_projectRoot, "Assets/Gone.prefab.meta"));
        AssetIndexer.IndexProject(_projectRoot, db);

        Assert.Empty(db.EdgesFrom("Assets/Gone.prefab", 1));
        Assert.Single(db.SearchByName("Stay"));
    }

    [Fact]
    public void DoesNotSweepScriptNodesBelongingToTheOtherIndexer()
    {
        // Both indexers share one graph and one set of path prefixes. Without per-extension
        // sweep ownership, whichever ran second deleted the other's nodes entirely — measured
        // as a graph that went to 0 nodes.
        Write("Assets/Code.cs", "public class Code { }");
        WriteAsset("Assets/Scene.unity", "--- !u!1 &1\nGameObject:\n  m_Name: Thing\n", guid: "33333333333333333333333333333333");
        using var db = OpenGraph();

        ScriptIndexer.IndexProject(_projectRoot, db);
        AssetIndexer.IndexProject(_projectRoot, db);

        Assert.Single(db.SearchByName("Code"));
        Assert.Single(db.SearchByName("Thing"));
    }

    [Fact]
    public void IndexesAssetsInsideLocalFilePackages()
    {
        var external = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(external, "Runtime"));
        File.WriteAllText(Path.Combine(external, "Runtime", "Ext.prefab"),
            Header + "--- !u!1 &9\nGameObject:\n  m_Name: External\n");
        Write("Packages/manifest.json", $"{{\"dependencies\":{{\"com.example.pkg\":\"file:{external}\"}}}}");
        using var db = OpenGraph();

        AssetIndexer.IndexProject(_projectRoot, db);

        Assert.Equal("Packages/com.example.pkg/Runtime/Ext.prefab",
            Assert.Single(db.SearchByName("External")).Path);

        Directory.Delete(external, recursive: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }
}
