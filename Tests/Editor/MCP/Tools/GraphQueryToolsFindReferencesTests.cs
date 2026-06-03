// Tests/Editor/MCP/Tools/GraphQueryToolsFindReferencesTests.cs
using System.IO;
using System.Linq;
using NUnit.Framework;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.Tests.MCP.Tools
{
    /// <summary>
    /// EditMode tests for FindReferencesTo (Task C2):
    ///   - Prefab over-count: structural/nesting edges excluded; direct component-field
    ///     references still counted; C# inheritance referrers preserved.
    ///   - .cs sibling inflation: types A and B in one Script node; referrer of B is NOT
    ///     reported for a query targeting A.
    /// </summary>
    public class GraphQueryToolsFindReferencesTests
    {
        string _testDbPath;
        GraphDatabase _db;
        GraphDatabase _savedInstance;

        [SetUp]
        public void SetUp()
        {
            _savedInstance = GraphDatabase.Instance;
            _testDbPath = Path.Combine(Path.GetTempPath(), $"hades_test_{System.Guid.NewGuid()}.db");
            _db = new GraphDatabase(_testDbPath);
        }

        [TearDown]
        public void TearDown()
        {
            _db?.Dispose();
            GraphDatabase.RestoreInstanceForTests(_savedInstance);
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
            if (File.Exists(_testDbPath + "-wal")) File.Delete(_testDbPath + "-wal");
            if (File.Exists(_testDbPath + "-shm")) File.Delete(_testDbPath + "-shm");
        }

        // -----------------------------------------------------------------------
        // Prefab over-count: nests_prefab chain must not inflate referrer count
        // -----------------------------------------------------------------------

        [Test]
        public void FindReferencesTo_Prefab_NestsPrefabChain_DoesNotCountTransitiveNestors()
        {
            // Prefab P (target)
            // ParentPrefab -nests_prefab-> P     (nesting = structural, NOT a referrer)
            // GrandparentPrefab -nests_prefab-> ParentPrefab  (purely transitive, not a referrer)
            //
            // Neither ParentPrefab nor GrandparentPrefab should appear in find_references_to(P).

            var p = _db.InsertNode(new NodeRecord("Prefab", "p_guid")
                { Name = "TargetPrefab", Path = "Assets/Prefabs/TargetPrefab.prefab" });
            var parent = _db.InsertNode(new NodeRecord("Prefab", "parent_guid")
                { Name = "ParentPrefab", Path = "Assets/Prefabs/ParentPrefab.prefab" });
            var grandparent = _db.InsertNode(new NodeRecord("Prefab", "gp_guid")
                { Name = "GrandparentPrefab", Path = "Assets/Prefabs/GrandparentPrefab.prefab" });

            _db.InsertEdge(parent, p, "nests_prefab");
            _db.InsertEdge(grandparent, parent, "nests_prefab");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Prefabs/TargetPrefab.prefab");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var refs = obj["result"]["references"] as JArray;

            var names = refs.Select(r => r["name"].ToString()).ToList();
            Assert.IsFalse(names.Contains("ParentPrefab"),
                "ParentPrefab nests TargetPrefab via nests_prefab — must NOT be a direct referrer");
            Assert.IsFalse(names.Contains("GrandparentPrefab"),
                "GrandparentPrefab nests transitively — must NOT appear as a direct referrer");
        }

        [Test]
        public void FindReferencesTo_Prefab_ComponentFieldReference_IsCounted()
        {
            // A prefab that references P through a serialized field (a real 'references' edge)
            // MUST still appear in results.

            var p = _db.InsertNode(new NodeRecord("Prefab", "p2_guid")
                { Name = "Widget", Path = "Assets/Prefabs/Widget.prefab" });
            var user = _db.InsertNode(new NodeRecord("Prefab", "user_guid")
                { Name = "Dashboard", Path = "Assets/Prefabs/Dashboard.prefab" });

            _db.InsertEdge(user, p, "references");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Prefabs/Widget.prefab");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var refs = obj["result"]["references"] as JArray;

            var names = refs.Select(r => r["name"].ToString()).ToList();
            Assert.IsTrue(names.Contains("Dashboard"),
                "Dashboard has a real 'references' edge to Widget — must appear as a referrer");
        }

        [Test]
        public void FindReferencesTo_Prefab_PrefabVariantInheritsFrom_IsExcluded()
        {
            // A PrefabVariant that inherits_from a base prefab should NOT be counted as a
            // direct referrer — that is structural/transitive inheritance, not a real reference.

            var basePrefab = _db.InsertNode(new NodeRecord("Prefab", "base_guid")
                { Name = "BasePrefab", Path = "Assets/Prefabs/BasePrefab.prefab" });
            var variant = _db.InsertNode(new NodeRecord("PrefabVariant", "var_guid")
                { Name = "VariantPrefab", Path = "Assets/Prefabs/VariantPrefab.prefab" });

            _db.InsertEdge(variant, basePrefab, "inherits_from");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Prefabs/BasePrefab.prefab");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var refs = obj["result"]["references"] as JArray;

            var names = refs.Select(r => r["name"].ToString()).ToList();
            Assert.IsFalse(names.Contains("VariantPrefab"),
                "PrefabVariant inherits_from is structural — must NOT count as a direct referrer");
        }

        // -----------------------------------------------------------------------
        // C# inheritance referrers must NOT be regressed
        // -----------------------------------------------------------------------

        [Test]
        public void FindReferencesTo_ScriptType_Subclass_InheritsFrom_IsIncluded()
        {
            // A subclass B that inherits_from base class A IS a legitimate referrer.
            // find_references_to(A.cs) must include B.

            var script = _db.InsertNode(new NodeRecord("Script", "base_script_guid")
                { Name = "Animal.cs", Path = "Assets/Scripts/Animal.cs" });
            var baseType = _db.InsertNode(new NodeRecord("ScriptType", "base_type_guid")
                { Name = "Animal", Path = "Assets/Scripts/Animal.cs" });
            var subType = _db.InsertNode(new NodeRecord("ScriptType", "sub_type_guid")
                { Name = "Dog", Path = "Assets/Scripts/Dog.cs" });

            _db.InsertEdge(script, baseType, "defines");
            _db.InsertEdge(subType, baseType, "inherits_from");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Scripts/Animal.cs");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var refs = obj["result"]["references"] as JArray;

            var names = refs.Select(r => r["name"].ToString()).ToList();
            Assert.IsTrue(names.Contains("Dog"),
                "C# subclass Dog inherits_from Animal — must appear as a legitimate referrer");
        }

        // -----------------------------------------------------------------------
        // .cs sibling inflation: referrer of sibling type must not pollute results
        // -----------------------------------------------------------------------

        [Test]
        public void FindReferencesTo_Script_SiblingType_ReferrerNotInflated()
        {
            // Two types in one file: TypeA (name matches file stem) and TypeB (sibling).
            // Referrer X only references TypeB.
            // find_references_to("Assets/Scripts/TypeA.cs") must NOT list X.

            var script = _db.InsertNode(new NodeRecord("Script", "multi_script_guid")
                { Name = "TypeA.cs", Path = "Assets/Scripts/TypeA.cs" });
            var typeA = _db.InsertNode(new NodeRecord("ScriptType", "typeA_guid")
                { Name = "TypeA", Path = "Assets/Scripts/TypeA.cs" });
            var typeB = _db.InsertNode(new NodeRecord("ScriptType", "typeB_guid")
                { Name = "TypeB", Path = "Assets/Scripts/TypeA.cs" });
            var referrerOfB = _db.InsertNode(new NodeRecord("ScriptType", "xb_guid")
                { Name = "XRefersB", Path = "Assets/Scripts/XRefersB.cs" });

            _db.InsertEdge(script, typeA, "defines");
            _db.InsertEdge(script, typeB, "defines");
            _db.InsertEdge(referrerOfB, typeB, "code_references");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Scripts/TypeA.cs");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var refs = obj["result"]["references"] as JArray;

            var names = refs.Select(r => r["name"].ToString()).ToList();
            Assert.IsFalse(names.Contains("XRefersB"),
                "XRefersB only references TypeB (sibling) — must NOT appear in results for TypeA");
        }

        [Test]
        public void FindReferencesTo_Script_PrimaryType_ReferrerIsIncluded()
        {
            // Two types in one file: TypeA (name matches file stem) and TypeB (sibling).
            // Referrer Y only references TypeA (the primary type).
            // find_references_to("Assets/Scripts/TypeA.cs") MUST include Y.

            var script = _db.InsertNode(new NodeRecord("Script", "multi2_script_guid")
                { Name = "TypeA2.cs", Path = "Assets/Scripts/TypeA2.cs" });
            var typeA = _db.InsertNode(new NodeRecord("ScriptType", "typeA2_guid")
                { Name = "TypeA2", Path = "Assets/Scripts/TypeA2.cs" });
            var typeB = _db.InsertNode(new NodeRecord("ScriptType", "typeB2_guid")
                { Name = "TypeB2", Path = "Assets/Scripts/TypeA2.cs" });
            var referrerOfA = _db.InsertNode(new NodeRecord("ScriptType", "ya_guid")
                { Name = "YRefersA2", Path = "Assets/Scripts/YRefersA2.cs" });

            _db.InsertEdge(script, typeA, "defines");
            _db.InsertEdge(script, typeB, "defines");
            _db.InsertEdge(referrerOfA, typeA, "code_references");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Scripts/TypeA2.cs");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var refs = obj["result"]["references"] as JArray;

            var names = refs.Select(r => r["name"].ToString()).ToList();
            Assert.IsTrue(names.Contains("YRefersA2"),
                "YRefersA2 references TypeA2 (the primary type) — must appear in results");
        }

        // -----------------------------------------------------------------------
        // .cs utility file: type name differs from file stem — must not return false-empty
        // -----------------------------------------------------------------------

        [Test]
        public void FindReferencesTo_Script_TypeNameDiffersFromStem_FallsBackToAllColocatedTypes()
        {
            // A file "Helpers.cs" whose only defined type is "StringHelpers" (name ≠ stem).
            // The stem "Helpers" matches no ScriptType, so the fallback keeps ALL co-located
            // ScriptTypes. find_references_to("Assets/Scripts/Helpers.cs") must still return
            // referrers of StringHelpers — it must NOT produce a false-empty result.

            var script = _db.InsertNode(new NodeRecord("Script", "helpers_script_guid")
                { Name = "Helpers.cs", Path = "Assets/Scripts/Helpers.cs" });
            var utilType = _db.InsertNode(new NodeRecord("ScriptType", "strhelpers_guid")
                { Name = "StringHelpers", Path = "Assets/Scripts/Helpers.cs" });
            var consumer = _db.InsertNode(new NodeRecord("ScriptType", "consumer_guid")
                { Name = "SomeConsumer", Path = "Assets/Scripts/SomeConsumer.cs" });

            _db.InsertEdge(script, utilType, "defines");
            _db.InsertEdge(consumer, utilType, "code_references");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Scripts/Helpers.cs");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var refs = obj["result"]["references"] as JArray;

            var names = refs.Select(r => r["name"].ToString()).ToList();
            Assert.IsTrue(names.Contains("SomeConsumer"),
                "SomeConsumer references StringHelpers (type name ≠ file stem fallback) — must appear in results");
        }

        // -----------------------------------------------------------------------
        // StructuralEdgeTypes constant: canonical set excludes instantiates / addressable_for
        // -----------------------------------------------------------------------

        [Test]
        public void StructuralEdgeTypes_DoesNotContain_InstantiatesOrAddressableFor()
        {
            // Confirm that the edge types reserved for Tasks C4 and D1 are treated as
            // REAL referrers, not structural edges, so those tasks need no changes here.
            Assert.IsFalse(GraphDatabase.StructuralEdgeTypes.Contains("instantiates"),
                "'instantiates' (Task C4) must be a real referrer — not in StructuralEdgeTypes");
            Assert.IsFalse(GraphDatabase.StructuralEdgeTypes.Contains("addressable_for"),
                "'addressable_for' (Task D1) must be a real referrer — not in StructuralEdgeTypes");
        }

        [Test]
        public void StructuralEdgeTypes_Contains_ExpectedStructuralEdges()
        {
            Assert.IsTrue(GraphDatabase.StructuralEdgeTypes.Contains("defines"),
                "'defines' is structural and must be in StructuralEdgeTypes");
            Assert.IsTrue(GraphDatabase.StructuralEdgeTypes.Contains("contains"),
                "'contains' is structural and must be in StructuralEdgeTypes");
            Assert.IsTrue(GraphDatabase.StructuralEdgeTypes.Contains("nests_prefab"),
                "'nests_prefab' is structural and must be in StructuralEdgeTypes");
        }

        // -----------------------------------------------------------------------
        // Task C4: scene→prefab 'instantiates' edge — real referrer (not structural)
        // -----------------------------------------------------------------------

        [Test]
        public void FindReferencesTo_Prefab_SceneInstantiatesEdge_SceneSurfacesAsReferrer()
        {
            // A scene that contains a prefab instance should appear in find_references_to(prefab).
            // SceneScanner emits: scene -[instantiates]-> prefab
            // 'instantiates' is NOT in StructuralEdgeTypes, so it must count as a real referrer.

            var prefab = _db.InsertNode(new NodeRecord("Prefab", "decorTile_guid")
                { Name = "DecorTile", Path = "Assets/Prefabs/DecorTile.prefab" });
            var scene = _db.InsertNode(new NodeRecord("Scene", "gameplay_scene_guid")
                { Name = "Gameplay", Path = "Assets/Scenes/Gameplay.unity" });

            _db.InsertEdge(scene, prefab, "instantiates");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Prefabs/DecorTile.prefab");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var refs = obj["result"]["references"] as JArray;

            var names = refs.Select(r => r["name"].ToString()).ToList();
            Assert.IsTrue(names.Contains("Gameplay"),
                "Scene 'Gameplay' instantiates the prefab — must appear as a referrer via 'instantiates' edge");
        }

        [Test]
        public void FindReferencesTo_Prefab_MultipleSceneInstances_DeduplicatedPerScene()
        {
            // Two scenes both instantiate the same prefab.
            // Each emits one 'instantiates' edge to the prefab (per-scene deduplication in
            // SceneScanner means at most one edge per unique prefab per scene).
            // Both scenes must be returned as independent referrers.

            var prefab = _db.InsertNode(new NodeRecord("Prefab", "coin_guid")
                { Name = "Coin", Path = "Assets/Prefabs/Coin.prefab" });
            var sceneA = _db.InsertNode(new NodeRecord("Scene", "sceneA_guid")
                { Name = "Level1", Path = "Assets/Scenes/Level1.unity" });
            var sceneB = _db.InsertNode(new NodeRecord("Scene", "sceneB_guid")
                { Name = "Level2", Path = "Assets/Scenes/Level2.unity" });

            _db.InsertEdge(sceneA, prefab, "instantiates");
            _db.InsertEdge(sceneB, prefab, "instantiates");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Prefabs/Coin.prefab");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var refs = obj["result"]["references"] as JArray;

            var names = refs.Select(r => r["name"].ToString()).ToList();
            Assert.IsTrue(names.Contains("Level1"), "Level1 instantiates Coin — must be a referrer");
            Assert.IsTrue(names.Contains("Level2"), "Level2 instantiates Coin — must be a referrer");
        }

        [Test]
        public void FindReferencesTo_Prefab_InstantiatesEdge_NotExcludedByStructuralFilter()
        {
            // Explicit regression guard: 'instantiates' must never be filtered out as structural.
            // This test inserts only an 'instantiates' edge and confirms a referrer is returned
            // (if it were in StructuralEdgeTypes the result would be empty).

            var prefab = _db.InsertNode(new NodeRecord("Prefab", "guard_prefab_guid")
                { Name = "GuardPrefab", Path = "Assets/Prefabs/GuardPrefab.prefab" });
            var scene = _db.InsertNode(new NodeRecord("Scene", "guard_scene_guid")
                { Name = "GuardScene", Path = "Assets/Scenes/GuardScene.unity" });

            _db.InsertEdge(scene, prefab, "instantiates");

            // No nests_prefab, no contains, no inherits_from — only 'instantiates'.
            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Prefabs/GuardPrefab.prefab");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var refs = obj["result"]["references"] as JArray;
            Assert.AreNotEqual(0, refs.Count,
                "'instantiates' edge must produce at least one referrer — it is not in StructuralEdgeTypes");
        }

        // -----------------------------------------------------------------------
        // Task D1: AddressableGroup→member 'addressable_for' edge surfaces the group
        // -----------------------------------------------------------------------

        [Test]
        public void FindReferencesTo_Asset_AddressableGroupEdge_GroupSurfacesAsReferrer()
        {
            // AddressablesScanner (Task D1) emits:
            //   group -[addressable_for]-> member
            // 'addressable_for' is NOT in StructuralEdgeTypes, so find_references_to(member)
            // must include the AddressableGroup node.

            var member = _db.InsertNode(new NodeRecord("Texture", "member_asset_guid")
                { Name = "MyTexture", Path = "Assets/Textures/MyTexture.png" });
            var group = _db.InsertNode(new NodeRecord("AddressableGroup", "addr_group_guid")
                { Name = "UI Group" });

            _db.InsertEdge(group, member, "addressable_for");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Textures/MyTexture.png");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var refs = obj["result"]["references"] as JArray;

            var names = refs.Select(r => r["name"].ToString()).ToList();
            Assert.IsTrue(names.Contains("UI Group"),
                "AddressableGroup has an 'addressable_for' edge to the member — must appear as a referrer");
        }

        [Test]
        public void FindReferencesTo_Asset_AddressableBothEntryAndGroup_BothSurfaceAsReferrers()
        {
            // AddressablesScanner emits BOTH:
            //   entry -[addressable_for]-> member   (pre-existing, Task D0/d7fb740)
            //   group -[addressable_for]-> member   (Task D1)
            // find_references_to(member) must include both the AddressableEntry and the
            // AddressableGroup — neither edge is structural.

            var member = _db.InsertNode(new NodeRecord("Prefab", "d1_member_guid")
                { Name = "EnemyPrefab", Path = "Assets/Prefabs/EnemyPrefab.prefab" });
            var group = _db.InsertNode(new NodeRecord("AddressableGroup", "d1_group_guid")
                { Name = "Enemies" });
            var entry = _db.InsertNode(new NodeRecord("AddressableEntry", "addr_entry:d1_group_guid:d1_entry_guid")
                { Name = "enemies/enemy", Path = "Assets/Prefabs/EnemyPrefab.prefab" });

            _db.InsertEdge(group, entry, "contains");         // structural — must NOT count
            _db.InsertEdge(entry, member, "addressable_for"); // entry→member (pre-existing)
            _db.InsertEdge(group, member, "addressable_for"); // group→member (Task D1)

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Prefabs/EnemyPrefab.prefab");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var refs = obj["result"]["references"] as JArray;

            var names = refs.Select(r => r["name"].ToString()).ToList();
            Assert.IsTrue(names.Contains("enemies/enemy"),
                "AddressableEntry must appear as a referrer via 'addressable_for'");
            Assert.IsTrue(names.Contains("Enemies"),
                "AddressableGroup must appear as a referrer via 'addressable_for' (Task D1)");
        }

        [Test]
        public void FindReferencesTo_Asset_AddressableContainsEdge_GroupNotCountedAsReferrer()
        {
            // The group→entry 'contains' edge is STRUCTURAL and must NOT make the group a referrer.
            // Only the new group→member 'addressable_for' edge (Task D1) should surface the group.
            // This test validates isolation: with only a 'contains' edge the group must NOT appear.

            var entry = _db.InsertNode(new NodeRecord("AddressableEntry", "addr_entry:isolation_g:isolation_e")
                { Name = "props/chair", Path = "Assets/Prefabs/Chair.prefab" });
            var group = _db.InsertNode(new NodeRecord("AddressableGroup", "isolation_group_guid")
                { Name = "Props" });

            _db.InsertEdge(group, entry, "contains"); // structural — must not count

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Prefabs/Chair.prefab");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var refs = obj["result"]["references"] as JArray;

            var names = refs.Select(r => r["name"].ToString()).ToList();
            Assert.IsFalse(names.Contains("Props"),
                "AddressableGroup connected only via 'contains' must NOT be a referrer of the member asset");
        }

        // -----------------------------------------------------------------------
        // nested_by bucket: direct structural parents must surface even when
        // reference_count is 0 (delete-safety for directly-nested prefabs)
        // -----------------------------------------------------------------------

        [Test]
        public void FindReferencesTo_Prefab_DirectNestingParent_SurfacedInNestedBy_NotInReferences()
        {
            // Inner.prefab is directly nested in Outer.prefab via nests_prefab.
            // No other edges exist.
            // find_references_to(Inner.prefab) must return:
            //   reference_count == 0  (Outer is NOT a direct referrer)
            //   nested_by contains Outer with relationship "nests_prefab"

            var inner = _db.InsertNode(new NodeRecord("Prefab", "inner_guid")
                { Name = "Inner", Path = "Assets/Prefabs/Inner.prefab" });
            var outer = _db.InsertNode(new NodeRecord("Prefab", "outer_guid")
                { Name = "Outer", Path = "Assets/Prefabs/Outer.prefab" });

            _db.InsertEdge(outer, inner, "nests_prefab");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Prefabs/Inner.prefab");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);

            // reference_count must stay 0 — Outer is structural, not a direct referrer.
            Assert.AreEqual(0, (int)obj["result"]["reference_count"],
                "reference_count must be 0 — nests_prefab parent is not a direct referrer");

            var refs = obj["result"]["references"] as JArray;
            var refNames = refs.Select(r => r["name"].ToString()).ToList();
            Assert.IsFalse(refNames.Contains("Outer"),
                "Outer must NOT appear in references — nests_prefab is structural");

            // nested_by must surface Outer.
            var nestedBy = obj["result"]["nested_by"] as JArray;
            Assert.IsNotNull(nestedBy, "nested_by array must be present");
            var nestedNames = nestedBy.Select(n => n["name"].ToString()).ToList();
            Assert.IsTrue(nestedNames.Contains("Outer"),
                "Outer nests Inner via nests_prefab — must appear in nested_by");

            var outerEntry = nestedBy.First(n => n["name"].ToString() == "Outer");
            Assert.AreEqual("nests_prefab", outerEntry["relationship"].ToString(),
                "nested_by entry for Outer must have relationship 'nests_prefab'");
        }

        [Test]
        public void FindReferencesTo_Prefab_PrefabVariant_SurfacedInNestedBy_NotInReferences()
        {
            // VariantPrefab inherits_from BasePrefab.
            // find_references_to(BasePrefab.prefab) must return:
            //   reference_count == 0  (variant is structural, not a direct referrer)
            //   nested_by contains VariantPrefab with relationship "inherits_from"

            var basePrefab = _db.InsertNode(new NodeRecord("Prefab", "nb_base_guid")
                { Name = "BasePrefab", Path = "Assets/Prefabs/BasePrefab.prefab" });
            var variant = _db.InsertNode(new NodeRecord("PrefabVariant", "nb_var_guid")
                { Name = "VariantPrefab", Path = "Assets/Prefabs/VariantPrefab.prefab" });

            _db.InsertEdge(variant, basePrefab, "inherits_from");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Prefabs/BasePrefab.prefab");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);

            Assert.AreEqual(0, (int)obj["result"]["reference_count"],
                "reference_count must be 0 — inherits_from variant is not a direct referrer");

            var refs = obj["result"]["references"] as JArray;
            var refNames = refs.Select(r => r["name"].ToString()).ToList();
            Assert.IsFalse(refNames.Contains("VariantPrefab"),
                "VariantPrefab must NOT appear in references — inherits_from is structural for prefab targets");

            // nested_by must surface the variant.
            var nestedBy = obj["result"]["nested_by"] as JArray;
            Assert.IsNotNull(nestedBy, "nested_by array must be present");
            var nestedNames = nestedBy.Select(n => n["name"].ToString()).ToList();
            Assert.IsTrue(nestedNames.Contains("VariantPrefab"),
                "VariantPrefab derives from BasePrefab — must appear in nested_by");

            var variantEntry = nestedBy.First(n => n["name"].ToString() == "VariantPrefab");
            Assert.AreEqual("inherits_from", variantEntry["relationship"].ToString(),
                "nested_by entry for VariantPrefab must have relationship 'inherits_from'");
        }

        [Test]
        public void FindReferencesTo_Prefab_NestAndRealRef_NestedByAndReferencesAreDisjoint()
        {
            // Inner is nested in Outer (structural) AND referenced by Loader (real referrer).
            // reference_count == 1 (Loader only), nested_by == [Outer], no overlap.

            var inner = _db.InsertNode(new NodeRecord("Prefab", "nr_inner_guid")
                { Name = "Inner", Path = "Assets/Prefabs/Inner.prefab" });
            var outer = _db.InsertNode(new NodeRecord("Prefab", "nr_outer_guid")
                { Name = "Outer", Path = "Assets/Prefabs/Outer.prefab" });
            var loader = _db.InsertNode(new NodeRecord("Script", "nr_loader_guid")
                { Name = "Loader", Path = "Assets/Scripts/Loader.cs" });

            _db.InsertEdge(outer, inner, "nests_prefab");
            _db.InsertEdge(loader, inner, "references");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Prefabs/Inner.prefab");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);

            Assert.AreEqual(1, (int)obj["result"]["reference_count"],
                "reference_count must be 1 — only Loader has a real references edge");

            var refNames = (obj["result"]["references"] as JArray)
                .Select(r => r["name"].ToString()).ToList();
            Assert.IsTrue(refNames.Contains("Loader"), "Loader must be in references");
            Assert.IsFalse(refNames.Contains("Outer"), "Outer must NOT be in references");

            var nestedNames = (obj["result"]["nested_by"] as JArray)
                .Select(n => n["name"].ToString()).ToList();
            Assert.IsTrue(nestedNames.Contains("Outer"), "Outer must be in nested_by");
            Assert.IsFalse(nestedNames.Contains("Loader"), "Loader must NOT be in nested_by");
        }

        [Test]
        public void FindReferencesTo_ScriptType_InheritsFrom_NotAffectedByNestedByLogic()
        {
            // For ScriptType targets, inherits_from referrers remain in references (not nested_by).
            // nested_by logic for inherits_from must only activate for Prefab/PrefabVariant targets.

            var baseScript = _db.InsertNode(new NodeRecord("Script", "nb_base_script_guid")
                { Name = "BaseClass.cs", Path = "Assets/Scripts/BaseClass.cs" });
            var baseType = _db.InsertNode(new NodeRecord("ScriptType", "nb_base_type_guid")
                { Name = "BaseClass", Path = "Assets/Scripts/BaseClass.cs" });
            var subType = _db.InsertNode(new NodeRecord("ScriptType", "nb_sub_type_guid")
                { Name = "SubClass", Path = "Assets/Scripts/SubClass.cs" });

            _db.InsertEdge(baseScript, baseType, "defines");
            _db.InsertEdge(subType, baseType, "inherits_from");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Scripts/BaseClass.cs");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);

            // SubClass must appear in references — not hidden in nested_by.
            var refNames = (obj["result"]["references"] as JArray)
                .Select(r => r["name"].ToString()).ToList();
            Assert.IsTrue(refNames.Contains("SubClass"),
                "C# subclass SubClass inherits_from BaseClass — must be in references, not nested_by");

            // nested_by must NOT contain SubClass.
            var nestedBy = obj["result"]["nested_by"] as JArray;
            var nestedNames = nestedBy.Select(n => n["name"].ToString()).ToList();
            Assert.IsFalse(nestedNames.Contains("SubClass"),
                "SubClass must NOT appear in nested_by — inherits_from on ScriptType targets stays in references");
        }
    }
}
