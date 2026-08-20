using Hades.Core.Graph;
using Hades.Core.Indexing;

namespace Hades.Core.Tests.Indexing;

/// <summary>
/// Meta-only node emission for binary/imported assets — no content parse, no edges from the
/// binary side, prune-on-delete through the same file_state mechanism every other indexer uses.
/// Same fixture shape as <see cref="AssetIndexerTests"/> (temp project root, Write/WriteAsset
/// helpers), since both share <see cref="Observation.ProjectSweeper"/> and
/// <see cref="ProjectWalker"/>.
/// </summary>
public class BinaryAssetIndexerTests : IDisposable
{
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    void Write(string relativePath, string contents)
    {
        var full = Path.Combine(_projectRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
    }

    void WriteBinaryAsset(string relativePath, string? guid)
    {
        // Content is irrelevant and deliberately not YAML-shaped — a meta-only indexer must
        // never need to read it at all, only stand it up on disk so File.Exists is true.
        Write(relativePath, "\0\0\0 not real binary data, existence is all that matters here");
        if (guid is not null) Write(relativePath + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
    }

    GraphDatabase OpenGraph() => GraphDatabase.Open(Path.Combine(_projectRoot, "graph.db"));

    [Fact]
    public void IndexesATextureAsAMetaOnlyNode()
    {
        WriteBinaryAsset("Assets/Textures/Rock.png", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        using var db = OpenGraph();

        var result = BinaryAssetIndexer.IndexProject(_projectRoot, db);

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(1, result.TypesFound);

        var node = Assert.Single(db.SearchByName("Rock"));
        Assert.Equal("Texture2D", node.Kind);
        Assert.Equal("Rock", node.Name);
        Assert.Equal("Assets/Textures/Rock.png", node.Path);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", node.Guid);
        Assert.Equal(0L, node.FileId);
    }

    [Theory]
    [InlineData("Assets/A.fbx", "Model")]
    [InlineData("Assets/A.wav", "AudioClip")]
    [InlineData("Assets/A.ttf", "Font")]
    [InlineData("Assets/A.shader", "Shader")]
    [InlineData("Assets/A.shadergraph", "ShaderGraph")]
    [InlineData("Assets/A.compute", "ComputeShader")]
    [InlineData("Assets/A.anim", "AnimationClip")]
    public void EveryRecognisedKindIndexesEndToEnd(string path, string expectedKind)
    {
        WriteBinaryAsset(path, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        using var db = OpenGraph();

        BinaryAssetIndexer.IndexProject(_projectRoot, db);

        var node = db.SearchByName(Path.GetFileNameWithoutExtension(path)).Single(n => n.Path == path);
        Assert.Equal(expectedKind, node.Kind);
    }

    [Fact]
    public void NeverEmitsAnEdge()
    {
        // The whole point of "meta-only": these files carry no structure Hades can read, so
        // there is nothing to walk outward FROM one — only into it, via edges other indexers
        // already write against its guid.
        WriteBinaryAsset("Assets/Textures/Rock.png", "cccccccccccccccccccccccccccccccc");
        using var db = OpenGraph();

        BinaryAssetIndexer.IndexProject(_projectRoot, db);

        Assert.Empty(db.EdgesFromPath("Assets/Textures/Rock.png"));
    }

    [Fact]
    public void MakesAnExistingReferenceToItResolveInsteadOfDangle()
    {
        // The actual payoff: AssetIndexer already writes a `references` edge from the .mat to
        // the texture's guid (it reads the referencing YAML, same as always). Before this node
        // existed, that edge's target owned no node anywhere, so TraceDependencies could only
        // report it dangling. Indexing the texture is the entire fix — no change to AssetIndexer
        // or TraceDependencies needed.
        const string textureGuid = "dddddddddddddddddddddddddddddddd";
        WriteBinaryAsset("Assets/Textures/Rock.png", textureGuid);
        Write("Assets/M.mat",
            "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!21 &2100000\nMaterial:\n  m_Name: M\n"
            + $"  m_SavedProperties:\n    m_TexEnvs:\n    - _MainTex:\n        m_Texture: {{fileID: 2800000, guid: {textureGuid}, type: 3}}\n");
        Write("Assets/M.mat.meta", "fileFormatVersion: 2\nguid: eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\n");
        using var db = OpenGraph();

        AssetIndexer.IndexProject(_projectRoot, db);
        BinaryAssetIndexer.IndexProject(_projectRoot, db);

        var trace = db.TraceDependencies("Assets/M.mat", maxDepth: 1);
        Assert.Empty(trace.Dangling);
        var hit = Assert.Single(trace.Hits);
        Assert.Equal("Assets/Textures/Rock.png", hit.Path);
    }

    [Fact]
    public void AFileWithNoMetaStillGetsANode_ButWithNoGuid()
    {
        // Same convention ScriptIndexer already established for a .cs with no .meta yet: absence
        // of a resolvable guid is not treated as absence of the file. The node just cannot be
        // reached by GUID until Unity (re)writes the .meta.
        WriteBinaryAsset("Assets/Orphan.png", guid: null);
        using var db = OpenGraph();

        BinaryAssetIndexer.IndexProject(_projectRoot, db);

        var node = Assert.Single(db.SearchByName("Orphan"));
        Assert.Null(node.Guid);
    }

    [Fact]
    public void DeletingTheFileRemovesItsNodeOnTheNextFullIndex()
    {
        WriteBinaryAsset("Assets/Gone.png", "11111111111111111111111111111111");
        WriteBinaryAsset("Assets/Stay.png", "22222222222222222222222222222222");
        using var db = OpenGraph();
        BinaryAssetIndexer.IndexProject(_projectRoot, db);
        Assert.Single(db.SearchByName("Gone"));

        File.Delete(Path.Combine(_projectRoot, "Assets/Gone.png"));
        File.Delete(Path.Combine(_projectRoot, "Assets/Gone.png.meta"));
        BinaryAssetIndexer.IndexProject(_projectRoot, db);

        Assert.Empty(db.SearchByName("Gone"));
        Assert.Single(db.SearchByName("Stay"));
    }

    [Fact]
    public void IndexFilesPrunesExactlyTheNamedDeletionThroughFileState()
    {
        // Mirrors ProjectService.SyncChanges' own contract: deletions are handled by the caller,
        // by explicit path (DeleteNodesForPath), never by a sweep scoped to a partial batch.
        WriteBinaryAsset("Assets/Gone.wav", "33333333333333333333333333333333");
        WriteBinaryAsset("Assets/Stay.wav", "44444444444444444444444444444444");
        using var db = OpenGraph();
        BinaryAssetIndexer.IndexProject(_projectRoot, db);

        File.Delete(Path.Combine(_projectRoot, "Assets/Gone.wav"));
        File.Delete(Path.Combine(_projectRoot, "Assets/Gone.wav.meta"));
        db.DeleteNodesForPath("Assets/Gone.wav");

        Assert.Empty(db.SearchByName("Gone"));
        Assert.Single(db.SearchByName("Stay"));
    }

    [Fact]
    public void RenamingAFileMovesItsNodeToTheNewPathUnderTheSameGuid()
    {
        const string guid = "55555555555555555555555555555555";
        WriteBinaryAsset("Assets/Old.png", guid);
        using var db = OpenGraph();
        BinaryAssetIndexer.IndexProject(_projectRoot, db);

        File.Delete(Path.Combine(_projectRoot, "Assets/Old.png"));
        File.Delete(Path.Combine(_projectRoot, "Assets/Old.png.meta"));
        WriteBinaryAsset("Assets/New.png", guid);
        BinaryAssetIndexer.IndexProject(_projectRoot, db);

        Assert.Empty(db.SearchByName("Old"));
        var node = Assert.Single(db.SearchByName("New"));
        Assert.Equal("Assets/New.png", node.Path);
        Assert.Equal(guid, node.Guid);
    }

    [Fact]
    public void DoesNotSweepNodesBelongingToTheOtherTwoIndexers()
    {
        // Three indexers now share one graph and one set of path prefixes. Without per-extension
        // sweep ownership (ImportedAssetKind.Extensions passed as ownedExtensions), whichever
        // indexer's full reindex ran would delete the other two's nodes entirely — the same
        // failure mode AssetIndexerTests.DoesNotSweepScriptNodesBelongingToTheOtherIndexer guards
        // for the ScriptIndexer/AssetIndexer pair.
        Write("Assets/Code.cs", "public class Code { }");
        WriteBinaryAsset("Assets/Texture.png", "66666666666666666666666666666666");
        Write("Assets/Scene.unity",
            "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1 &1\nGameObject:\n  m_Name: Thing\n");
        using var db = OpenGraph();

        ScriptIndexer.IndexProject(_projectRoot, db);
        AssetIndexer.IndexProject(_projectRoot, db); // also runs BinaryAssetIndexer internally

        Assert.Single(db.SearchByName("Code"));
        Assert.Single(db.SearchByName("Thing"));
        Assert.Single(db.SearchByName("Texture"));
    }

    [Fact]
    public void IndexesBinaryAssetsInsideLocalFilePackages()
    {
        var external = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(external, "Runtime"));
        File.WriteAllText(Path.Combine(external, "Runtime", "Ext.png"), "not real png bytes");
        File.WriteAllText(Path.Combine(external, "Runtime", "Ext.png.meta"),
            "fileFormatVersion: 2\nguid: 77777777777777777777777777777777\n");
        Write("Packages/manifest.json", $"{{\"dependencies\":{{\"com.example.pkg\":\"file:{external}\"}}}}");
        using var db = OpenGraph();

        BinaryAssetIndexer.IndexProject(_projectRoot, db);

        Assert.Equal("Packages/com.example.pkg/Runtime/Ext.png",
            Assert.Single(db.SearchByName("Ext")).Path);

        Directory.Delete(external, recursive: true);
    }

    [Fact]
    public void ReindexingAFileWithChangedContentKeepsExactlyOneNode()
    {
        // Delete-then-insert per file: re-running the indexer over an unchanged tree must not
        // accumulate duplicate rows.
        WriteBinaryAsset("Assets/Rock.png", "88888888888888888888888888888888");
        using var db = OpenGraph();
        BinaryAssetIndexer.IndexProject(_projectRoot, db);
        BinaryAssetIndexer.IndexProject(_projectRoot, db);

        Assert.Single(db.SearchByName("Rock"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }
}
