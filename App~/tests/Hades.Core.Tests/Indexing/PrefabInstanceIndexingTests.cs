using Hades.Core.Graph;
using Hades.Core.Indexing;

namespace Hades.Core.Tests.Indexing;

public class PrefabInstanceIndexingTests : IDisposable
{
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";
    const string SourceGuid = "cccccccccccccccccccccccccccccccc";

    void WriteAsset(string relativePath, string body, string guid)
    {
        var full = Path.Combine(_projectRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, Header + body);
        File.WriteAllText(full + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
    }

    void WriteSceneWithInstance() => WriteAsset("Assets/Main.unity", """
        --- !u!1001 &100
        PrefabInstance:
          serializedVersion: 2
          m_Modification:
            m_TransformParent: {fileID: 200}
            m_Modifications:
            - target: {fileID: 300, guid: cccccccccccccccccccccccccccccccc, type: 3}
              propertyPath: m_Name
              value: Renamed
              objectReference: {fileID: 0}
            - target: {fileID: 301, guid: cccccccccccccccccccccccccccccccc, type: 3}
              propertyPath: m_Sprite
              value: 
              objectReference: {fileID: 21300000, guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb, type: 3}
            m_RemovedComponents: []
          m_SourcePrefab: {fileID: 100100000, guid: cccccccccccccccccccccccccccccccc, type: 3}
        """, guid: "11111111111111111111111111111111");

    GraphDatabase OpenGraph() => GraphDatabase.Open(Path.Combine(_projectRoot, "graph.db"));

    [Fact]
    public void APrefabInstanceProducesAnInstanceOfEdge()
    {
        WriteSceneWithInstance();
        using var db = OpenGraph();

        AssetIndexer.IndexProject(_projectRoot, db);

        var edge = Assert.Single(db.EdgesFrom("Assets/Main.unity", 100), e => e.Kind == "instance_of");
        Assert.Equal(SourceGuid, edge.ToGuid);
    }

    [Fact]
    public void EveryUserOfAPrefabIsFindableFromItsGuid()
    {
        // The query this whole plan exists for: "which scenes instantiate this prefab".
        WriteSceneWithInstance();
        using var db = OpenGraph();

        AssetIndexer.IndexProject(_projectRoot, db);

        var users = db.EdgesTo(SourceGuid).Where(e => e.Kind == "instance_of").ToList();
        Assert.Equal("Assets/Main.unity", Assert.Single(users).FromPath);
    }

    [Fact]
    public void AReferenceOverrideBecomesAReferenceEdgeNamingTheProperty()
    {
        WriteSceneWithInstance();
        using var db = OpenGraph();

        AssetIndexer.IndexProject(_projectRoot, db);

        var edge = Assert.Single(db.EdgesFrom("Assets/Main.unity", 100),
            e => e.PropertyPath.StartsWith("m_Modifications["));
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", edge.ToGuid);
        Assert.Equal("m_Modifications[m_Sprite]", edge.PropertyPath);
    }

    [Fact]
    public void ValueOverridesProduceNoEdge()
    {
        // 43,784 of 44,576 real overrides set a scalar. Storing them would inflate the graph
        // ~55x and answer nothing a reference graph is asked.
        WriteSceneWithInstance();
        using var db = OpenGraph();

        AssetIndexer.IndexProject(_projectRoot, db);

        Assert.DoesNotContain(db.EdgesFrom("Assets/Main.unity", 100),
            e => e.PropertyPath.Contains("m_Name"));
    }

    [Fact]
    public void AStrippedObjectLinksToItsSource()
    {
        WriteAsset("Assets/Stripped.prefab",
            "--- !u!1 &6346727972004658377 stripped\nGameObject:\n"
            + "  m_CorrespondingSourceObject: {fileID: 3952813215589545985, guid: dddddddddddddddddddddddddddddddd,\n    type: 3}\n",
            guid: "22222222222222222222222222222222");
        using var db = OpenGraph();

        AssetIndexer.IndexProject(_projectRoot, db);

        var edge = Assert.Single(db.EdgesFrom("Assets/Stripped.prefab", 6346727972004658377),
            e => e.Kind == "corresponds_to");
        Assert.Equal("dddddddddddddddddddddddddddddddd", edge.ToGuid);
    }

    [Fact]
    public void LegacyPreUnity2018PrefabMarkersProduceNoInstanceEdge()
    {
        // Class 1001 covers two formats. "Prefab:" with m_IsPrefabParent is the pre-2018.3 marker
        // for a prefab ASSET, not an instance of one — 15 exist in the real corpus, all from
        // third-party packages never re-saved. It instantiates nothing.
        WriteAsset("Assets/Legacy.prefab", """
            --- !u!1001 &100100000
            Prefab:
              m_ObjectHideFlags: 1
              serializedVersion: 2
              m_Modification:
                m_TransformParent: {fileID: 0}
                m_Modifications: []
                m_RemovedComponents: []
              m_ParentPrefab: {fileID: 0}
              m_RootGameObject: {fileID: 100000}
              m_IsPrefabParent: 1
            """, guid: "33333333333333333333333333333333");
        using var db = OpenGraph();

        AssetIndexer.IndexProject(_projectRoot, db);

        Assert.DoesNotContain(db.EdgesFrom("Assets/Legacy.prefab", 100100000), e => e.Kind == "instance_of");
    }

    [Fact]
    public void ReindexingAfterDeletionRemovesInstanceEdges()
    {
        WriteSceneWithInstance();
        using var db = OpenGraph();
        AssetIndexer.IndexProject(_projectRoot, db);
        Assert.NotEmpty(db.EdgesTo(SourceGuid));

        File.Delete(Path.Combine(_projectRoot, "Assets/Main.unity"));
        File.Delete(Path.Combine(_projectRoot, "Assets/Main.unity.meta"));
        AssetIndexer.IndexProject(_projectRoot, db);

        Assert.Empty(db.EdgesTo(SourceGuid));
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }
}
