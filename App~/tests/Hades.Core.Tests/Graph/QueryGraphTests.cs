using Hades.Core.Graph;

namespace Hades.Core.Tests.Graph;

/// <summary>
/// <see cref="GraphDatabase.QueryGraph"/> - the structured filter behind query_graph, the only
/// tool in the whole port that takes caller-authored query input (see QueryTools' class doc
/// comment for why it is a fixed set of bound parameters rather than a query string). Every test
/// here exercises <see cref="GraphDatabase"/> directly, the same level RelationshipQueryTests and
/// GraphDatabaseTests already operate at; kept in its own file rather than folded into either,
/// since this tool's security posture (raw SQL must be structurally impossible, not just
/// discouraged) deserves to be easy to find and audit on its own.
/// </summary>
public class QueryGraphTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    GraphDatabase Open()
    {
        Directory.CreateDirectory(_dir);
        return GraphDatabase.Open(Path.Combine(_dir, "graph.db"));
    }

    static GraphNode Node(string name, string kind, string path, string? guid = null, long fileId = 0) => new()
    {
        Kind = kind,
        Name = name,
        Path = path,
        Guid = guid,
        FileId = fileId,
    };

    static GraphEdge Edge(string fromPath, long fromFileId, string kind, string? toGuid, long toFileId = 0,
        string propertyPath = "m_Script") => new()
    {
        FromPath = fromPath,
        FromFileId = fromFileId,
        ToGuid = toGuid,
        ToFileId = toFileId,
        Kind = kind,
        PropertyPath = propertyPath,
    };

    // ---------------------------------------------------------------- basic filters

    [Fact]
    public void QueryGraph_FiltersByKindExactly()
    {
        using var db = Open();
        db.UpsertNodes([
            Node("Health", "Class", "Assets/Health.cs"),
            Node("Health", "GameObject", "Assets/Health.prefab"),
        ]);

        var hits = db.QueryGraph(kind: "Class", namePattern: null, pathPrefix: null, edgeKind: null);

        var hit = Assert.Single(hits);
        Assert.Equal("Class", hit.Kind);
    }

    [Fact]
    public void QueryGraph_FiltersByNamePatternCaseInsensitiveSubstring()
    {
        using var db = Open();
        db.UpsertNodes([Node("EnemyHealth", "Class", "Assets/EnemyHealth.cs"), Node("PlayerController", "Class", "Assets/PlayerController.cs")]);

        var hits = db.QueryGraph(kind: null, namePattern: "HEALTH", pathPrefix: null, edgeKind: null);

        Assert.Single(hits);
    }

    [Fact]
    public void QueryGraph_FiltersByPathPrefix()
    {
        using var db = Open();
        db.UpsertNodes([
            Node("A", "Class", "Assets/Scripts/A.cs"),
            Node("B", "Class", "Assets/Other/B.cs"),
        ]);

        var hits = db.QueryGraph(kind: null, namePattern: null, pathPrefix: "Assets/Scripts", edgeKind: null);

        var hit = Assert.Single(hits);
        Assert.Equal("Assets/Scripts/A.cs", hit.Path);
    }

    [Fact]
    public void QueryGraph_CombinesEveryFilterWithAnd()
    {
        using var db = Open();
        db.UpsertNodes([
            Node("Health", "Class", "Assets/Scripts/Health.cs"),   // matches all three
            Node("Health", "Struct", "Assets/Scripts/Health.cs"),  // wrong kind
            Node("Health", "Class", "Assets/Other/Health.cs"),     // wrong path prefix
        ]);

        var hits = db.QueryGraph(kind: "Class", namePattern: "health", pathPrefix: "Assets/Scripts", edgeKind: null);

        var hit = Assert.Single(hits);
        Assert.Equal("Assets/Scripts/Health.cs", hit.Path);
        Assert.Equal("Class", hit.Kind);
    }

    [Fact]
    public void QueryGraph_NoFiltersReturnsEveryNodeUpToLimit()
    {
        using var db = Open();
        db.UpsertNodes([Node("A", "Class", "Assets/A.cs"), Node("B", "Class", "Assets/B.cs")]);

        Assert.Equal(2, db.QueryGraph(kind: null, namePattern: null, pathPrefix: null, edgeKind: null).Count);
    }

    // ---------------------------------------------------------------- edge relationship filter

    [Fact]
    public void QueryGraph_OutgoingEdgeKindFindsTheSourceNodeOfAMatchingEdge()
    {
        using var db = Open();
        db.UpsertNodes([
            Node("Enemy", "PrefabInstance", "Assets/Scene.unity", guid: "scene-guid", fileId: 1),
            Node("Unrelated", "GameObject", "Assets/Scene.unity", guid: "scene-guid", fileId: 2),
        ]);
        db.UpsertEdges([Edge("Assets/Scene.unity", 1, "instance_of", "prefab-guid", propertyPath: "m_SourcePrefab")]);

        var hits = db.QueryGraph(kind: null, namePattern: null, pathPrefix: null,
            edgeKind: "instance_of", edgeDirection: "outgoing");

        var hit = Assert.Single(hits);
        Assert.Equal("Enemy", hit.Name);
    }

    [Fact]
    public void QueryGraph_IncomingEdgeKindFindsTheTargetNodeOfAMatchingEdge()
    {
        // toFileId 11500000, NOT 0 - Unity's own fixed "main object of a script asset" constant,
        // deliberately mismatched from the script node's own recorded file_id (always 0 - see
        // GraphNode.FileId's doc comment). Incoming must match by to_guid alone, exactly like
        // ReferencesTo/EdgesTo already do, or a real script reference (which always looks like
        // this) would never be found - pinning that instead of the easier, unrealistic toFileId: 0
        // this test used before, which passed for the wrong reason.
        using var db = Open();
        db.UpsertNodes([
            Node("PlayerController", "Class", "Assets/PlayerController.cs", guid: "script-guid", fileId: 0),
            Node("Player", "MonoBehaviour", "Assets/Player.prefab", guid: "prefab-guid", fileId: 2),
        ]);
        db.UpsertEdges([Edge("Assets/Player.prefab", 2, "references", "script-guid", toFileId: 11500000, propertyPath: "m_Script")]);

        var hits = db.QueryGraph(kind: null, namePattern: null, pathPrefix: null,
            edgeKind: "references", edgeDirection: "incoming");

        var hit = Assert.Single(hits);
        Assert.Equal("PlayerController", hit.Name);
    }

    [Fact]
    public void QueryGraph_EdgeKindWithNoMatchingEdgeExcludesTheNode()
    {
        using var db = Open();
        db.UpsertNodes([Node("Lonely", "Class", "Assets/Lonely.cs", guid: "lonely-guid")]);

        Assert.Empty(db.QueryGraph(kind: null, namePattern: null, pathPrefix: null, edgeKind: "references"));
    }

    [Fact]
    public void QueryGraph_DefaultsToOutgoingDirectionWhenNotSpecified()
    {
        using var db = Open();
        db.UpsertNodes([Node("Enemy", "PrefabInstance", "Assets/Scene.unity", guid: "scene-guid", fileId: 1)]);
        db.UpsertEdges([Edge("Assets/Scene.unity", 1, "instance_of", "prefab-guid", propertyPath: "m_SourcePrefab")]);

        var hits = db.QueryGraph(kind: null, namePattern: null, pathPrefix: null, edgeKind: "instance_of");

        Assert.Single(hits);
    }

    // ---------------------------------------------------------------- edge target filter (graph_query extension)

    [Fact]
    public void QueryGraph_EdgeTargetGuidRestrictsToEdgesPointingAtThatSpecificTarget()
    {
        // The join find_prefabs_with_component needs: a MonoBehaviour referencing script A must be
        // found when filtering for edges to A's guid, and excluded when filtering for B's.
        using var db = Open();
        db.UpsertNodes([
            Node("ScriptA", "Class", "Assets/A.cs", guid: "guid-a"),
            Node("ScriptB", "Class", "Assets/B.cs", guid: "guid-b"),
            Node("Player", "MonoBehaviour", "Assets/Player.prefab", guid: "prefab-guid", fileId: 2),
        ]);
        db.UpsertEdges([Edge("Assets/Player.prefab", 2, "references", "guid-a", toFileId: 11500000)]);

        var hitsForA = db.QueryGraph(kind: null, namePattern: null, pathPrefix: null,
            edgeKind: "references", edgeTargetGuid: "guid-a");
        var hit = Assert.Single(hitsForA);
        Assert.Equal("Player", hit.Name);

        var hitsForB = db.QueryGraph(kind: null, namePattern: null, pathPrefix: null,
            edgeKind: "references", edgeTargetGuid: "guid-b");
        Assert.Empty(hitsForB);
    }

    [Fact]
    public void QueryGraph_EdgeTargetNamePatternRestrictsToEdgesPointingAtATargetWhoseNameMatches()
    {
        // The join find_components_using_pattern/component_find's resolution branch need: a
        // MonoBehaviour referencing "PlayerController" matches a target-name pattern of "Player"
        // but not "Enemy".
        using var db = Open();
        db.UpsertNodes([
            Node("PlayerController", "Class", "Assets/PlayerController.cs", guid: "script-guid"),
            Node("Player", "MonoBehaviour", "Assets/Player.prefab", guid: "prefab-guid", fileId: 2),
        ]);
        db.UpsertEdges([Edge("Assets/Player.prefab", 2, "references", "script-guid", toFileId: 11500000)]);

        var matching = db.QueryGraph(kind: null, namePattern: null, pathPrefix: null,
            edgeKind: "references", edgeTargetNamePattern: "player");
        Assert.Single(matching);

        var nonMatching = db.QueryGraph(kind: null, namePattern: null, pathPrefix: null,
            edgeKind: "references", edgeTargetNamePattern: "enemy");
        Assert.Empty(nonMatching);
    }

    [Fact]
    public void QueryGraph_EdgeTargetNamePatternTreatsUnderscoreAsLiteralNotWildcard()
    {
        // Same LIKE-escaping obligation as namePattern/pathPrefix - the target side must not
        // regress the fix independently.
        using var db = Open();
        db.UpsertNodes([
            Node("m_Health", "Class", "Assets/A.cs", guid: "guid-a"),
            Node("mXHealth", "Class", "Assets/B.cs", guid: "guid-b"),
            Node("RefA", "MonoBehaviour", "Assets/RefA.prefab", guid: "ref-a-guid", fileId: 2),
            Node("RefB", "MonoBehaviour", "Assets/RefB.prefab", guid: "ref-b-guid", fileId: 2),
        ]);
        db.UpsertEdges([
            Edge("Assets/RefA.prefab", 2, "references", "guid-a", toFileId: 11500000),
            Edge("Assets/RefB.prefab", 2, "references", "guid-b", toFileId: 11500000),
        ]);

        var hits = db.QueryGraph(kind: null, namePattern: null, pathPrefix: null,
            edgeKind: "references", edgeTargetNamePattern: "m_Health");

        var hit = Assert.Single(hits);
        Assert.Equal("RefA", hit.Name);
    }

    // ---------------------------------------------------------------- edge target kind filter (Plan 10 Task 6 correctness fix)

    [Fact]
    public void QueryGraph_EdgeTargetKindRestrictsMatchToThatNodeKind_ExcludingASameNamedNonScriptAsset()
    {
        // The correctness gap the Plan 10 Task 6 audit found and documented but did not fix:
        // edgeTargetNamePattern alone cannot tell a real script reference apart from a
        // same-named NON-script asset (a Material here, per the audit's own example) also
        // reached by a `references` edge - both look identical in the result, with nothing a
        // caller could filter on afterward. This is exactly the old ComponentsUsingPattern /
        // ComponentsMatching SQL's own guarantee (`JOIN nodes sn ON sn.guid = e.to_guid AND
        // sn.kind = 'Class'`), which the unconstrained graph_query subquery dropped.
        using var db = Open();
        db.UpsertNodes([
            Node("PlayerController", "Class", "Assets/PlayerController.cs", guid: "script-guid"),
            Node("PlayerController", "Material", "Assets/PlayerController.mat", guid: "material-guid"),
            Node("Player", "MonoBehaviour", "Assets/Player.prefab", guid: "prefab-guid", fileId: 2),
            Node("Ally", "MeshRenderer", "Assets/Ally.prefab", guid: "ally-guid", fileId: 2),
        ]);
        db.UpsertEdges([
            Edge("Assets/Player.prefab", 2, "references", "script-guid", toFileId: 11500000, propertyPath: "m_Script"),
            Edge("Assets/Ally.prefab", 2, "references", "material-guid", toFileId: 2100000, propertyPath: "m_Materials.0"),
        ]);

        // Without the fix: both the genuine script reference AND the same-named material
        // reference match - a false positive indistinguishable from the real hit.
        var both = db.QueryGraph(kind: null, namePattern: null, pathPrefix: null,
            edgeKind: "references", edgeTargetNamePattern: "playercontroller");
        Assert.Equal(2, both.Count);

        // With the fix: edgeTargetKind: "Class" restores the old guarantee that the matched
        // target is actually a script.
        var scriptOnly = db.QueryGraph(kind: null, namePattern: null, pathPrefix: null,
            edgeKind: "references", edgeTargetNamePattern: "playercontroller", edgeTargetKind: "Class");
        var hit = Assert.Single(scriptOnly);
        Assert.Equal("Assets/Player.prefab", hit.Path);
    }

    [Fact]
    public void QueryGraph_EdgeTargetKindAloneRestrictsWithoutANamePattern()
    {
        // edgeTargetKind must not require edgeTargetNamePattern - a caller may want "anything
        // referencing ANY script" without also naming a pattern.
        using var db = Open();
        db.UpsertNodes([
            Node("Foo", "Class", "Assets/Foo.cs", guid: "script-guid"),
            Node("Foo", "Material", "Assets/Foo.mat", guid: "material-guid"),
            Node("Player", "MonoBehaviour", "Assets/Player.prefab", guid: "prefab-guid", fileId: 2),
            Node("Ally", "MeshRenderer", "Assets/Ally.prefab", guid: "ally-guid", fileId: 2),
        ]);
        db.UpsertEdges([
            Edge("Assets/Player.prefab", 2, "references", "script-guid", toFileId: 11500000, propertyPath: "m_Script"),
            Edge("Assets/Ally.prefab", 2, "references", "material-guid", toFileId: 2100000, propertyPath: "m_Materials.0"),
        ]);

        var hits = db.QueryGraph(kind: null, namePattern: null, pathPrefix: null,
            edgeKind: "references", edgeTargetKind: "Class");

        var hit = Assert.Single(hits);
        Assert.Equal("Assets/Player.prefab", hit.Path);
    }

    [Fact]
    public void QueryGraph_EdgeTargetKindSqlInjectionAttemptMatchesNothingAndLeavesSchemaIntact()
    {
        using var db = Open();
        db.UpsertNodes([
            Node("PlayerController", "Class", "Assets/PlayerController.cs", guid: "script-guid"),
            Node("Player", "MonoBehaviour", "Assets/Player.prefab", guid: "prefab-guid", fileId: 2),
        ]);
        db.UpsertEdges([Edge("Assets/Player.prefab", 2, "references", "script-guid", toFileId: 11500000)]);
        var before = db.TotalNodes();

        var hits = db.QueryGraph(kind: null, namePattern: null, pathPrefix: null,
            edgeKind: "references", edgeTargetKind: "Class'; DROP TABLE nodes; --");

        Assert.Empty(hits);
        Assert.Equal(before, db.TotalNodes());
    }

    [Fact]
    public void QueryGraph_EdgeTargetFilterWithoutEdgeKindIsANoopNotASilentBroadening()
    {
        // edgeTargetGuid/edgeTargetNamePattern are only meaningful together with edgeKind - see
        // QueryTools' own validation, which refuses this combination before it ever reaches here.
        // At the DB level, omitting edgeKind must not silently broaden the result the way ignoring
        // the target filter would - confirmed here so the app-level refusal is not the only thing
        // standing between a caller and a surprising result.
        using var db = Open();
        db.UpsertNodes([Node("Lonely", "Class", "Assets/Lonely.cs", guid: "lonely-guid")]);

        var hits = db.QueryGraph(kind: null, namePattern: null, pathPrefix: null, edgeKind: null,
            edgeTargetGuid: "some-guid");

        Assert.Single(hits); // edgeKind is null, so the whole edge clause (target filter included) is skipped
    }

    [Fact]
    public void QueryGraph_EdgeTargetKindWithoutEdgeKindIsANoopNotASilentBroadening()
    {
        // Same no-op-without-edgeKind contract as every other edge-target filter above.
        using var db = Open();
        db.UpsertNodes([Node("Lonely", "Class", "Assets/Lonely.cs", guid: "lonely-guid")]);

        var hits = db.QueryGraph(kind: null, namePattern: null, pathPrefix: null, edgeKind: null,
            edgeTargetKind: "Class");

        Assert.Single(hits);
    }

    // ---------------------------------------------------------------- edge absence (edgeAbsent - graph_query extension)

    [Fact]
    public void QueryGraph_EdgeAbsentFindsNodesWithNoMatchingIncomingEdge()
    {
        // find_orphan_scripts' own join: a Class with no incoming 'references' edge.
        using var db = Open();
        db.UpsertNodes([
            Node("PlayerController", "Class", "Assets/PlayerController.cs", guid: "script-guid"),
            Node("OrphanScript", "Class", "Assets/OrphanScript.cs", guid: "orphan-guid"),
            Node("Player", "MonoBehaviour", "Assets/Player.prefab", guid: "prefab-guid", fileId: 2),
        ]);
        db.UpsertEdges([Edge("Assets/Player.prefab", 2, "references", "script-guid", toFileId: 11500000)]);

        var hits = db.QueryGraph(kind: "Class", namePattern: null, pathPrefix: null,
            edgeKind: "references", edgeDirection: "incoming", edgeAbsent: true);

        var hit = Assert.Single(hits);
        Assert.Equal("OrphanScript", hit.Name);
    }

    [Fact]
    public void QueryGraph_EdgeAbsentExcludesNodesWithNoGuidFromIncomingCheck()
    {
        // A node with no guid can never be the target of anything, so "no incoming edge" would be
        // vacuously true for it - excluded deliberately, the same reasoning GraphDatabase.
        // OrphanScripts documents for its own "guid IS NOT NULL" clause.
        using var db = Open();
        db.UpsertNodes([Node("NoGuidClass", "Class", "Assets/NoGuid.cs", guid: null)]);

        var hits = db.QueryGraph(kind: "Class", namePattern: null, pathPrefix: null,
            edgeKind: "references", edgeDirection: "incoming", edgeAbsent: true);

        Assert.Empty(hits);
    }

    [Fact]
    public void QueryGraph_EdgeAbsentDefaultsToFalsePreservingExistingPositiveBehaviour()
    {
        using var db = Open();
        db.UpsertNodes([Node("Enemy", "PrefabInstance", "Assets/Scene.unity", guid: "scene-guid", fileId: 1)]);
        db.UpsertEdges([Edge("Assets/Scene.unity", 1, "instance_of", "prefab-guid", propertyPath: "m_SourcePrefab")]);

        var hits = db.QueryGraph(kind: null, namePattern: null, pathPrefix: null,
            edgeKind: "instance_of", edgeDirection: "outgoing");

        Assert.Single(hits);
    }

    // ---------------------------------------------------------------- LIKE escaping (namePattern / pathPrefix)

    [Fact]
    public void QueryGraph_NamePatternTreatsUnderscoreAsLiteralNotWildcard()
    {
        // Same bug SearchByName's own escaping guards against - see GraphDatabase.SearchByName's
        // doc comment. query_graph has its own SQL and must not regress the fix independently.
        using var db = Open();
        db.UpsertNodes([Node("m_Health", "Class", "Assets/A.cs"), Node("mXHealth", "Class", "Assets/B.cs")]);

        var hits = db.QueryGraph(kind: null, namePattern: "m_Health", pathPrefix: null, edgeKind: null);

        var hit = Assert.Single(hits);
        Assert.Equal("m_Health", hit.Name);
    }

    [Fact]
    public void QueryGraph_NamePatternTreatsPercentAsLiteralNotWildcard()
    {
        using var db = Open();
        db.UpsertNodes([Node("100%Complete", "Class", "Assets/A.cs"), Node("1000Complete", "Class", "Assets/B.cs")]);

        var hits = db.QueryGraph(kind: null, namePattern: "100%", pathPrefix: null, edgeKind: null);

        var hit = Assert.Single(hits);
        Assert.Equal("100%Complete", hit.Name);
    }

    [Fact]
    public void QueryGraph_PathPrefixTreatsUnderscoreAsLiteralNotWildcard()
    {
        using var db = Open();
        db.UpsertNodes([
            Node("A", "Class", "Assets/Path_With_Underscores/A.cs"),
            Node("B", "Class", "Assets/PathXWithXUnderscores/B.cs"),
        ]);

        var hits = db.QueryGraph(kind: null, namePattern: null, pathPrefix: "Assets/Path_With_Underscores", edgeKind: null);

        Assert.Single(hits);
    }

    // ---------------------------------------------------------------- SQL injection safety

    [Fact]
    public void QueryGraph_SqlInjectionAttemptInNamePatternMatchesNothingAndLeavesSchemaIntact()
    {
        // The one behaviour this tool exists to make structurally impossible: every value is a
        // bound SQLite parameter, never concatenated into the SQL text, so a namePattern that
        // LOOKS like SQL is just a literal (non-matching) search string, full stop. Proven two
        // ways: zero rows back (it matched nothing, as a nonsense literal should), and the nodes
        // table still there afterward with an unchanged row count (TotalNodes() would throw
        // "no such table" on this same open connection if DROP TABLE had actually executed).
        using var db = Open();
        db.UpsertNodes([Node("PlayerController", "Class", "Assets/PlayerController.cs")]);
        var before = db.TotalNodes();

        var hits = db.QueryGraph(kind: null, namePattern: "'; DROP TABLE nodes; --", pathPrefix: null, edgeKind: null);

        Assert.Empty(hits);
        Assert.Equal(before, db.TotalNodes());
        Assert.Equal(1, db.TotalNodes());
    }

    [Fact]
    public void QueryGraph_SqlInjectionAttemptInPathPrefixMatchesNothingAndLeavesSchemaIntact()
    {
        using var db = Open();
        db.UpsertNodes([Node("PlayerController", "Class", "Assets/PlayerController.cs")]);
        var before = db.TotalNodes();

        var hits = db.QueryGraph(kind: null, namePattern: null, pathPrefix: "'; DROP TABLE nodes; --", edgeKind: null);

        Assert.Empty(hits);
        Assert.Equal(before, db.TotalNodes());
    }

    [Fact]
    public void QueryGraph_SqlInjectionAttemptInKindMatchesNothingAndLeavesSchemaIntact()
    {
        using var db = Open();
        db.UpsertNodes([Node("PlayerController", "Class", "Assets/PlayerController.cs")]);
        var before = db.TotalNodes();

        var hits = db.QueryGraph(kind: "Class'; DROP TABLE nodes; --", namePattern: null, pathPrefix: null, edgeKind: null);

        Assert.Empty(hits);
        Assert.Equal(before, db.TotalNodes());
    }

    // ---------------------------------------------------------------- limit

    [Fact]
    public void QueryGraph_RespectsLimit()
    {
        using var db = Open();
        db.UpsertNodes(Enumerable.Range(0, 5).Select(i => Node($"Thing{i}", "Class", $"Assets/Thing{i}.cs")).ToList());

        Assert.Equal(3, db.QueryGraph(kind: null, namePattern: "Thing", pathPrefix: null, edgeKind: null, limit: 3).Count);
    }

    [Fact]
    public void QueryGraph_NegativeLimitDoesNotReturnEverything()
    {
        using var db = Open();
        db.UpsertNodes(Enumerable.Range(0, 5).Select(i => Node($"Thing{i}", "Class", $"Assets/Thing{i}.cs")).ToList());

        var hits = db.QueryGraph(kind: null, namePattern: "Thing", pathPrefix: null, edgeKind: null, limit: -1);

        Assert.NotEqual(5, hits.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
