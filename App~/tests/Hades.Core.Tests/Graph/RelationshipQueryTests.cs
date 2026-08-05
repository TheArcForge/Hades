using Hades.Core.Graph;

namespace Hades.Core.Tests.Graph;

/// <summary>
/// The relationship queries behind trace_dependencies, find_prefabs_with_component,
/// find_components_using_pattern, find_orphan_scripts, and component_find. Exercised directly
/// against <see cref="GraphDatabase"/>, the same level GraphEdgeTests and GraphDatabaseTests
/// already operate at — HTTP-level wiring is verified separately against the real project (see
/// GraphTools/ProjectService and the Task 2 Step 3 verification notes).
/// </summary>
public class RelationshipQueryTests : IDisposable
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

    static GraphEdge Reference(string fromPath, long fromFileId, string? toGuid, long toFileId = 0,
        string propertyPath = "m_Script") => new()
    {
        FromPath = fromPath,
        FromFileId = fromFileId,
        ToGuid = toGuid,
        ToFileId = toFileId,
        Kind = "references",
        PropertyPath = propertyPath,
    };

    // ---------------------------------------------------------------- PathForGuid

    [Fact]
    public void PathForGuid_ResolvesTheOwningPath()
    {
        using var db = Open();
        db.UpsertNodes([Node("Player", "GameObject", "Assets/Player.prefab", guid: "aaaa", fileId: 1)]);

        Assert.Equal("Assets/Player.prefab", db.PathForGuid("aaaa"));
    }

    [Fact]
    public void PathForGuid_ReturnsNullForAnUnknownGuid()
    {
        using var db = Open();

        Assert.Null(db.PathForGuid("does-not-exist"));
    }

    [Fact]
    public void PathForGuid_IsTheInverseOfGuidForPath()
    {
        using var db = Open();
        db.UpsertNodes([Node("Enemy", "GameObject", "Assets/Enemy.prefab", guid: "bbbb", fileId: 1)]);

        var guid = db.GuidForPath("Assets/Enemy.prefab");

        Assert.NotNull(guid);
        Assert.Equal("Assets/Enemy.prefab", db.PathForGuid(guid!));
    }

    // ---------------------------------------------------------------- TraceDependencies

    [Fact]
    public void TraceDependencies_Depth1ReturnsOnlyDirectDependencies()
    {
        // A -> B -> C chain. Depth 1 from A must see B only, never C.
        using var db = Open();
        db.UpsertNodes([
            Node("B", "GameObject", "Assets/B.prefab", guid: "bbbb"),
            Node("C", "GameObject", "Assets/C.prefab", guid: "cccc"),
        ]);
        db.UpsertEdges([
            Reference("Assets/A.prefab", 1, "bbbb"),
            Reference("Assets/B.prefab", 1, "cccc"),
        ]);

        var hits = db.TraceDependencies("Assets/A.prefab", maxDepth: 1);

        var hit = Assert.Single(hits);
        Assert.Equal("Assets/B.prefab", hit.Path);
        Assert.Equal(1, hit.Depth);
    }

    [Fact]
    public void TraceDependencies_WalksMultipleHopsUpToMaxDepth()
    {
        using var db = Open();
        db.UpsertNodes([
            Node("B", "GameObject", "Assets/B.prefab", guid: "bbbb"),
            Node("C", "GameObject", "Assets/C.prefab", guid: "cccc"),
        ]);
        db.UpsertEdges([
            Reference("Assets/A.prefab", 1, "bbbb"),
            Reference("Assets/B.prefab", 1, "cccc"),
        ]);

        var hits = db.TraceDependencies("Assets/A.prefab", maxDepth: 5);

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Path == "Assets/B.prefab" && h.Depth == 1);
        Assert.Contains(hits, h => h.Path == "Assets/C.prefab" && h.Depth == 2);
    }

    [Fact]
    public void TraceDependencies_TerminatesOnATwoFileCycleInsteadOfHanging()
    {
        // A references B, B references A right back — exactly the "prefab referencing a manager
        // that references the prefab" shape called out in the reference implementation's doc
        // comment. Without a visited set this recurses forever; this call returning at all,
        // promptly, with the correct result, is the regression guard.
        using var db = Open();
        db.UpsertEdges([
            Reference("Assets/A.prefab", 1, "bbbb"),
            Reference("Assets/B.prefab", 1, "aaaa"),
        ]);
        db.UpsertNodes([
            Node("A", "GameObject", "Assets/A.prefab", guid: "aaaa"),
            Node("B", "GameObject", "Assets/B.prefab", guid: "bbbb"),
        ]);

        var hits = db.TraceDependencies("Assets/A.prefab", maxDepth: 10);

        // Walking back to the root must not re-report it as its own dependency.
        var hit = Assert.Single(hits);
        Assert.Equal("Assets/B.prefab", hit.Path);
        Assert.Equal(1, hit.Depth);
    }

    [Fact]
    public void TraceDependencies_ThreeFileCycleVisitsEachNodeExactlyOnce()
    {
        using var db = Open();
        db.UpsertNodes([
            Node("A", "GameObject", "Assets/A.prefab", guid: "aaaa"),
            Node("B", "GameObject", "Assets/B.prefab", guid: "bbbb"),
            Node("C", "GameObject", "Assets/C.prefab", guid: "cccc"),
        ]);
        db.UpsertEdges([
            Reference("Assets/A.prefab", 1, "bbbb"),
            Reference("Assets/B.prefab", 1, "cccc"),
            Reference("Assets/C.prefab", 1, "aaaa"),
        ]);

        var hits = db.TraceDependencies("Assets/A.prefab", maxDepth: 10);

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Path == "Assets/B.prefab" && h.Depth == 1);
        Assert.Contains(hits, h => h.Path == "Assets/C.prefab" && h.Depth == 2);
    }

    [Fact]
    public void TraceDependencies_IgnoresEdgesThatAreNotKindReferences()
    {
        // instance_of and corresponds_to are not "depends on" in the sense this tool answers —
        // only `references` edges count outward, per the reference implementation's doc comment.
        using var db = Open();
        db.UpsertEdges([
            new GraphEdge
            {
                FromPath = "Assets/A.prefab", FromFileId = 1, ToGuid = "bbbb", ToFileId = 0,
                Kind = "instance_of", PropertyPath = "m_SourcePrefab",
            },
        ]);

        Assert.Empty(db.TraceDependencies("Assets/A.prefab", maxDepth: 3));
    }

    [Fact]
    public void TraceDependencies_UnresolvableGuidIsDroppedRatherThanFailing()
    {
        // A reference to a GUID nothing in this project's graph owns — a Unity builtin resource
        // (e.g. the default cube mesh), or an asset this graph never indexed. PathForGuid
        // returns null for it; the hop is silently skipped rather than surfaced as a broken node.
        using var db = Open();
        db.UpsertEdges([Reference("Assets/A.prefab", 1, "0000000000000000e000000000000000")]);

        Assert.Empty(db.TraceDependencies("Assets/A.prefab", maxDepth: 3));
    }

    [Fact]
    public void TraceDependencies_AggregatesAcrossEveryNodeInTheFile_NotJustOneFileId()
    {
        // A prefab's GameObject and its MonoBehaviour are separate nodes with separate file_ids.
        // "Everything this FILE depends on" must combine both — see EdgesFromPath.
        using var db = Open();
        db.UpsertNodes([
            Node("Player", "GameObject", "Assets/Player.prefab", guid: "aaaa", fileId: 1),
            Node("MonoBehaviour", "MonoBehaviour", "Assets/Player.prefab", guid: "aaaa", fileId: 2),
            Node("PlayerController", "Class", "Assets/PlayerController.cs", guid: "cccc"),
        ]);
        db.UpsertEdges([Reference("Assets/Player.prefab", 2, "cccc")]);

        var hit = Assert.Single(db.TraceDependencies("Assets/Player.prefab", maxDepth: 1));
        Assert.Equal("Assets/PlayerController.cs", hit.Path);
    }

    [Fact]
    public void TraceDependencies_ClampsAnOutOfRangeDepthRatherThanMisbehaving()
    {
        using var db = Open();
        db.UpsertNodes([Node("B", "GameObject", "Assets/B.prefab", guid: "bbbb")]);
        db.UpsertEdges([Reference("Assets/A.prefab", 1, "bbbb")]);

        // 0 (and negative) must not mean "no hops" or "unlimited" — it is clamped up to 1.
        var hit = Assert.Single(db.TraceDependencies("Assets/A.prefab", maxDepth: 0));
        Assert.Equal("Assets/B.prefab", hit.Path);
    }

    // ---------------------------------------------------------------- PrefabsReferencing

    [Fact]
    public void PrefabsReferencing_ReturnsPrefabsButNotScenes()
    {
        using var db = Open();
        db.UpsertEdges([
            Reference("Assets/Enemy.prefab", 1, "ssss"),
            Reference("Assets/Main.unity", 1, "ssss"),
        ]);

        var prefabs = db.PrefabsReferencing("ssss");

        var hit = Assert.Single(prefabs);
        Assert.Equal("Assets/Enemy.prefab", hit.Path);
    }

    [Fact]
    public void PrefabsReferencing_CountsMultipleComponentsInOnePrefab()
    {
        using var db = Open();
        db.UpsertEdges([
            Reference("Assets/Enemy.prefab", 1, "ssss"),
            Reference("Assets/Enemy.prefab", 2, "ssss"),
        ]);

        var hit = Assert.Single(db.PrefabsReferencing("ssss"));
        Assert.Equal(2, hit.References);
    }

    [Fact]
    public void PrefabsReferencing_ReturnsEmptyWhenNoPrefabUsesIt()
    {
        using var db = Open();
        db.UpsertEdges([Reference("Assets/Main.unity", 1, "ssss")]);

        Assert.Empty(db.PrefabsReferencing("ssss"));
    }

    [Fact]
    public void PrefabsReferencing_IgnoresNonReferencesEdgeKinds()
    {
        using var db = Open();
        db.UpsertEdges([
            new GraphEdge
            {
                FromPath = "Assets/Enemy.prefab", FromFileId = 1, ToGuid = "ssss", ToFileId = 0,
                Kind = "instance_of", PropertyPath = "m_SourcePrefab",
            },
        ]);

        Assert.Empty(db.PrefabsReferencing("ssss"));
    }

    // ---------------------------------------------------------------- OrphanScripts

    [Fact]
    public void OrphanScripts_ReturnsAClassWithNoIncomingReference()
    {
        using var db = Open();
        db.UpsertNodes([new GraphNode { Kind = "Class", Name = "Unused", Path = "Assets/Unused.cs", Guid = "dddd" }]);

        var hit = Assert.Single(db.OrphanScripts());
        Assert.Equal("Unused", hit.Name);
    }

    [Fact]
    public void OrphanScripts_ExcludesAClassThatAPrefabReferences()
    {
        using var db = Open();
        db.UpsertNodes([new GraphNode { Kind = "Class", Name = "Used", Path = "Assets/Used.cs", Guid = "eeee" }]);
        db.UpsertEdges([Reference("Assets/Enemy.prefab", 1, "eeee")]);

        Assert.Empty(db.OrphanScripts());
    }

    [Fact]
    public void OrphanScripts_ReportsAScriptOnlyUsedFromCode_TheDocumentedSupersetLimitation()
    {
        // A MonoBehaviour attached exclusively via AddComponent<T>() in C# produces no edge at
        // all: code-level references are not tracked yet (today's edge kinds are exactly
        // corresponds_to, instance_of, references — all sourced from Unity YAML, none from C#).
        // This script is NOT dead code, but the current edge set cannot tell it apart from one
        // that genuinely is, so it must appear here. This test pins that documented gap, not a
        // bug: if it ever starts failing because code-level edges were added, that is progress —
        // update it and find_orphan_scripts' [Description] together.
        using var db = Open();
        db.UpsertNodes([new GraphNode
        {
            Kind = "Class", Name = "AddedViaCode", Path = "Assets/AddedViaCode.cs", Guid = "ffff",
        }]);

        Assert.Single(db.OrphanScripts());
    }

    [Fact]
    public void OrphanScripts_ExcludesNonClassKinds()
    {
        // Interfaces/structs/enums/records are never the target of a Unity m_Script reference,
        // so including them would report 100% of them as "orphaned" regardless of real usage —
        // a bigger honesty problem than the code-usage gap this tool already discloses.
        using var db = Open();
        db.UpsertNodes([
            new GraphNode { Kind = "Interface", Name = "IDamageable", Path = "Assets/IDamageable.cs", Guid = "gggg" },
            new GraphNode { Kind = "Struct", Name = "Coords", Path = "Assets/Coords.cs", Guid = "hhhh" },
        ]);

        Assert.Empty(db.OrphanScripts());
    }

    [Fact]
    public void OrphanScripts_RespectsLimit()
    {
        using var db = Open();
        db.UpsertNodes(Enumerable.Range(0, 5)
            .Select(i => new GraphNode { Kind = "Class", Name = $"Orphan{i}", Path = $"Assets/Orphan{i}.cs", Guid = $"g{i}" })
            .ToList());

        Assert.Equal(3, db.OrphanScripts(limit: 3).Count);
    }

    // ---------------------------------------------------------------- ComponentsUsingPattern

    [Fact]
    public void ComponentsUsingPattern_FindsFilesReferencingAMatchingScript_AcrossPrefabsAndScenes()
    {
        // Broader than PrefabsReferencing on purpose: fuzzy name match, and scenes count too —
        // see GraphTools' find_components_using_pattern description for why.
        using var db = Open();
        db.UpsertNodes([new GraphNode { Kind = "Class", Name = "EnemyHealth", Path = "Assets/EnemyHealth.cs", Guid = "iiii" }]);
        db.UpsertEdges([
            Reference("Assets/Enemy.prefab", 1, "iiii"),
            Reference("Assets/Main.unity", 1, "iiii"),
        ]);

        var hits = db.ComponentsUsingPattern("health");

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.ComponentPath == "Assets/Enemy.prefab" && h.ScriptName == "EnemyHealth");
        Assert.Contains(hits, h => h.ComponentPath == "Assets/Main.unity" && h.ScriptName == "EnemyHealth");
    }

    [Fact]
    public void ComponentsUsingPattern_IsCaseInsensitive()
    {
        using var db = Open();
        db.UpsertNodes([new GraphNode { Kind = "Class", Name = "EnemyHealth", Path = "Assets/EnemyHealth.cs", Guid = "jjjj" }]);
        db.UpsertEdges([Reference("Assets/Enemy.prefab", 1, "jjjj")]);

        Assert.Single(db.ComponentsUsingPattern("HEALTH"));
    }

    [Fact]
    public void ComponentsUsingPattern_DoesNotMatchUnrelatedScripts()
    {
        using var db = Open();
        db.UpsertNodes([new GraphNode { Kind = "Class", Name = "PlayerController", Path = "Assets/PlayerController.cs", Guid = "kkkk" }]);
        db.UpsertEdges([Reference("Assets/Player.prefab", 1, "kkkk")]);

        Assert.Empty(db.ComponentsUsingPattern("health"));
    }

    [Fact]
    public void ComponentsUsingPattern_DeduplicatesMultipleComponentsInTheSameFileUsingTheSameScript()
    {
        using var db = Open();
        db.UpsertNodes([new GraphNode { Kind = "Class", Name = "EnemyHealth", Path = "Assets/EnemyHealth.cs", Guid = "llll" }]);
        db.UpsertEdges([
            Reference("Assets/Enemy.prefab", 1, "llll"),
            Reference("Assets/Enemy.prefab", 2, "llll"),
        ]);

        Assert.Single(db.ComponentsUsingPattern("health"));
    }

    // ---------------------------------------------------------------- ComponentsMatching

    [Fact]
    public void ComponentsMatching_FindsABuiltinComponentByItsUnityClassName()
    {
        using var db = Open();
        db.UpsertNodes([
            Node("Enemy", "GameObject", "Assets/Enemy.prefab", fileId: 1),
            Node("Rigidbody", "Rigidbody", "Assets/Enemy.prefab", fileId: 3),
        ]);

        var hit = Assert.Single(db.ComponentsMatching("rigid"));
        Assert.Equal("Assets/Enemy.prefab", hit.Path);
        Assert.Equal(3, hit.FileId);
        Assert.Equal("Rigidbody", hit.TypeName);
    }

    [Fact]
    public void ComponentsMatching_FindsAMonoBehaviourByItsScriptNameNotTheBareMonoBehaviourKind()
    {
        using var db = Open();
        db.UpsertNodes([new GraphNode { Kind = "Class", Name = "EnemyHealth", Path = "Assets/EnemyHealth.cs", Guid = "mmmm" }]);
        db.UpsertEdges([Reference("Assets/Enemy.prefab", 4, "mmmm")]);

        var hit = Assert.Single(db.ComponentsMatching("health"));
        Assert.Equal("Assets/Enemy.prefab", hit.Path);
        Assert.Equal(4, hit.FileId);
        Assert.Equal("EnemyHealth", hit.TypeName);
    }

    [Fact]
    public void ComponentsMatching_IsCaseInsensitive()
    {
        using var db = Open();
        db.UpsertNodes([Node("BoxCollider", "BoxCollider", "Assets/Enemy.prefab", fileId: 2)]);

        Assert.Single(db.ComponentsMatching("BOXCOLLIDER"));
    }

    [Fact]
    public void ComponentsMatching_ExcludesGameObjectsPrefabInstancesAndTheBareMonoBehaviourKind()
    {
        // These are containers/placeholders, not components - a GameObject or PrefabInstance
        // happening to be NAMED "Health" must not leak into a component type-name search, and
        // "MonoBehaviour" alone names nothing a caller could have meant (see the script-name test
        // above for how a custom component actually resolves).
        using var db = Open();
        db.UpsertNodes([
            Node("Health", "GameObject", "Assets/Health.prefab", fileId: 1),
            Node("Health", "PrefabInstance", "Assets/Nested.prefab", fileId: 2),
            Node("MonoBehaviour", "MonoBehaviour", "Assets/Other.prefab", fileId: 3),
        ]);

        Assert.Empty(db.ComponentsMatching("health"));
        Assert.Empty(db.ComponentsMatching("monobehaviour"));
    }

    [Fact]
    public void ComponentsMatching_RespectsLimit()
    {
        using var db = Open();
        db.UpsertNodes(Enumerable.Range(0, 5)
            .Select(i => Node("Rigidbody", "Rigidbody", $"Assets/Enemy{i}.prefab", fileId: i))
            .ToList());

        Assert.Equal(3, db.ComponentsMatching("rigid", limit: 3).Count);
    }

    // ---------------------------------------------------------------- FindUnityEvents

    [Fact]
    public void FindUnityEvents_FindsAWiredListenerGroupedByItsEventField()
    {
        using var db = Open();
        db.UpsertEdges([
            Reference("Assets/Menu.prefab", 2, "aaaa", toFileId: 1,
                propertyPath: "m_OnClick.m_PersistentCalls.m_Calls.m_Target"),
        ]);

        var hit = Assert.Single(db.FindUnityEvents());
        Assert.Equal("Assets/Menu.prefab", hit.Path);
        Assert.Equal(2, hit.FileId);
        Assert.Equal("m_OnClick", hit.EventField);
        Assert.Equal(1, hit.ListenerCount);
    }

    [Fact]
    public void FindUnityEvents_CountsMultipleListenersOnTheSameEventFieldAsOneGroupedRow()
    {
        using var db = Open();
        db.UpsertEdges([
            Reference("Assets/Menu.prefab", 2, "aaaa", toFileId: 1,
                propertyPath: "m_OnClick.m_PersistentCalls.m_Calls.m_Target"),
            Reference("Assets/Menu.prefab", 2, "bbbb", toFileId: 1,
                propertyPath: "m_OnClick.m_PersistentCalls.m_Calls.m_Target"),
        ]);

        var hit = Assert.Single(db.FindUnityEvents());
        Assert.Equal(2, hit.ListenerCount);
    }

    [Fact]
    public void FindUnityEvents_TwoListenersToTheSameTargetCollapseIntoOneCount_TheDocumentedUndercountLimitation()
    {
        // Two DIFFERENT persistent calls on the same event field, both targeting fileId 1 - the
        // same shape AssetIndexer would produce for two listeners on one Button.onClick that both
        // happen to call methods on the same target GameObject. They collide under the edges
        // table's own (from_path, from_file_id, to_guid, to_file_id, property_path) uniqueness and
        // upsert into ONE row, so ListenerCount reports 1, not 2 - a real, disclosed lower bound
        // (see FindUnityEvents' "HONEST LIMITATION #2"), not a hypothetical. This test pins that
        // gap so it cannot regress into silently claiming to be exact.
        using var db = Open();
        db.UpsertEdges([
            Reference("Assets/Menu.prefab", 2, "aaaa", toFileId: 1,
                propertyPath: "m_OnClick.m_PersistentCalls.m_Calls.m_Target"),
            Reference("Assets/Menu.prefab", 2, "aaaa", toFileId: 1,
                propertyPath: "m_OnClick.m_PersistentCalls.m_Calls.m_Target"),
        ]);

        var hit = Assert.Single(db.FindUnityEvents());
        Assert.Equal(1, hit.ListenerCount);
    }

    [Fact]
    public void FindUnityEvents_DistinguishesDifferentEventFieldsOnTheSameComponent()
    {
        using var db = Open();
        db.UpsertEdges([
            Reference("Assets/Toggle.prefab", 2, "aaaa", toFileId: 1,
                propertyPath: "m_OnValueChanged.m_PersistentCalls.m_Calls.m_Target"),
            Reference("Assets/Toggle.prefab", 2, "bbbb", toFileId: 1,
                propertyPath: "onDamage.m_PersistentCalls.m_Calls.m_Target"),
        ]);

        var hits = db.FindUnityEvents();

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.EventField == "m_OnValueChanged");
        Assert.Contains(hits, h => h.EventField == "onDamage");
    }

    [Fact]
    public void FindUnityEvents_IgnoresOrdinaryReferencesNotShapedLikeAPersistentCall()
    {
        using var db = Open();
        db.UpsertEdges([Reference("Assets/Enemy.prefab", 2, "aaaa", propertyPath: "m_Script")]);

        Assert.Empty(db.FindUnityEvents());
    }

    [Fact]
    public void FindUnityEvents_IgnoresNonReferencesEdgeKinds()
    {
        using var db = Open();
        db.UpsertEdges([
            new GraphEdge
            {
                FromPath = "Assets/Enemy.prefab", FromFileId = 2, ToGuid = "aaaa", ToFileId = 1,
                Kind = "instance_of", PropertyPath = "m_OnClick.m_PersistentCalls.m_Calls.m_Target",
            },
        ]);

        Assert.Empty(db.FindUnityEvents());
    }

    [Fact]
    public void FindUnityEvents_RespectsLimit()
    {
        using var db = Open();
        db.UpsertEdges(Enumerable.Range(0, 5)
            .Select(i => Reference($"Assets/Menu{i}.prefab", 2, "aaaa", toFileId: 1,
                propertyPath: "m_OnClick.m_PersistentCalls.m_Calls.m_Target"))
            .ToList());

        Assert.Equal(3, db.FindUnityEvents(limit: 3).Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
