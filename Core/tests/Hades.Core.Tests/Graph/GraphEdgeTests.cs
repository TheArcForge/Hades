using Hades.Core.Graph;

namespace Hades.Core.Tests.Graph;

public class GraphEdgeTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    GraphDatabase Open()
    {
        Directory.CreateDirectory(_dir);
        return GraphDatabase.Open(Path.Combine(_dir, "graph.db"));
    }

    static GraphEdge Edge(string fromPath, long fromFileId, string? toGuid, long toFileId,
        string propertyPath = "m_Script") => new()
    {
        FromPath = fromPath, FromFileId = fromFileId,
        ToGuid = toGuid, ToFileId = toFileId,
        Kind = "references", PropertyPath = propertyPath,
    };

    [Fact]
    public void SchemaIsCurrentAndSupportsEdges()
    {
        // Deliberately not pinned to a literal version: doing so made this fail on every
        // legitimate schema bump while testing nothing about edges.
        using var db = Open();

        Assert.Equal(GraphSchema.Version, db.SchemaVersion);
        db.UpsertEdges([Edge("Assets/A.prefab", 1, "bbb", 2)]);
        Assert.Single(db.EdgesFrom("Assets/A.prefab", 1));
    }

    [Fact]
    public void StoresAndReadsBackAnEdge()
    {
        using var db = Open();
        db.UpsertEdges([Edge("Assets/Player.prefab", 100, "bbb", 11500000)]);

        var edge = Assert.Single(db.EdgesFrom("Assets/Player.prefab", 100));
        Assert.Equal("bbb", edge.ToGuid);
        Assert.Equal("m_Script", edge.PropertyPath);
    }

    [Fact]
    public void EdgeUpsertIsIdempotent()
    {
        using var db = Open();
        db.UpsertEdges([Edge("Assets/A.prefab", 1, "bbb", 2)]);
        db.UpsertEdges([Edge("Assets/A.prefab", 1, "bbb", 2)]);

        Assert.Single(db.EdgesFrom("Assets/A.prefab", 1));
    }

    [Fact]
    public void LocalEdgesWithNullGuidStillDeduplicate()
    {
        // SQLite treats every NULL as distinct in a unique index, so the identity index uses
        // COALESCE(to_guid, ''). Without it, every local reference would duplicate per reindex.
        using var db = Open();
        db.UpsertEdges([Edge("Assets/A.prefab", 1, null, 222, "m_GameObject")]);
        db.UpsertEdges([Edge("Assets/A.prefab", 1, null, 222, "m_GameObject")]);
        db.UpsertEdges([Edge("Assets/A.prefab", 1, null, 222, "m_GameObject")]);

        Assert.Single(db.EdgesFrom("Assets/A.prefab", 1));
    }

    [Fact]
    public void DeletingAPathRemovesItsEdgesToo()
    {
        using var db = Open();
        db.UpsertEdges([Edge("Assets/A.prefab", 1, "bbb", 2)]);

        db.DeleteNodesForPath("Assets/A.prefab");

        Assert.Empty(db.EdgesFrom("Assets/A.prefab", 1));
        Assert.Equal(0, db.TotalEdges());
    }

    [Fact]
    public void FindsEverythingReferencingAGuid()
    {
        using var db = Open();
        db.UpsertEdges([
            Edge("Assets/A.prefab", 1, "target", 0),
            Edge("Assets/B.prefab", 2, "target", 0),
            Edge("Assets/C.prefab", 3, "other", 0),
        ]);

        Assert.Equal(2, db.EdgesTo("target").Count);
    }

    [Fact]
    public void NodesCarryGuidAndFileId()
    {
        using var db = Open();
        db.UpsertNodes([new GraphNode
        {
            Kind = "GameObject", Name = "Player", Path = "Assets/Player.prefab",
            Guid = "aaa", FileId = 12345,
        }]);

        var node = Assert.Single(db.SearchByName("Player"));
        Assert.Equal("aaa", node.Guid);
        Assert.Equal(12345L, node.FileId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
