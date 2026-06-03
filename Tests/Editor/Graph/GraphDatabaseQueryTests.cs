// Tests/Editor/Graph/GraphDatabaseQueryTests.cs
using System.IO;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;

namespace ArcForge.Hades.Editor.Tests.Graph
{
    public class GraphDatabaseQueryTests
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
            SeedTestData();
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

        void SeedTestData()
        {
            var project = _db.InsertNode(new NodeRecord("Project") { Name = "TestProject" });
            var scene = _db.InsertNode(new NodeRecord("Scene", "scene_guid") { Name = "MainMenu", Path = "Assets/Scenes/MainMenu.unity" });
            var go1 = _db.InsertNode(new NodeRecord("GameObject") { Name = "Canvas" });
            var go2 = _db.InsertNode(new NodeRecord("GameObject") { Name = "Player" });
            var comp = _db.InsertNode(new NodeRecord("Component") { Name = "PlayerController" });
            var scriptType = _db.InsertNode(new NodeRecord("ScriptType", "script_guid") { Name = "PlayerController", Path = "Assets/Scripts/PlayerController.cs" });
            var prefab = _db.InsertNode(new NodeRecord("Prefab", "prefab_guid") { Name = "PlayerPrefab", Path = "Assets/Prefabs/Player.prefab" });

            _db.InsertEdge(scene, go1, "contains");
            _db.InsertEdge(scene, go2, "contains");
            _db.InsertEdge(go2, comp, "contains");
            _db.InsertEdge(comp, scriptType, "instance_of");
            _db.InsertEdge(prefab, scriptType, "references");
        }

        [Test]
        public void SearchByName_FindsMatches()
        {
            var results = _db.SearchByName("Player%");
            Assert.GreaterOrEqual(results.Count, 2);
        }

        [Test]
        public void SearchByName_WithTypeFilter()
        {
            var results = _db.SearchByName("Player%", "Prefab");
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("PlayerPrefab", results[0].Name);
        }

        [Test]
        public void SearchByName_NoMatch_ReturnsEmpty()
        {
            var results = _db.SearchByName("Nonexistent%");
            Assert.AreEqual(0, results.Count);
        }

        [Test]
        public void GetNodeCount_ByType()
        {
            var count = _db.GetNodeCount("Scene");
            Assert.AreEqual(1, count);
        }

        [Test]
        public void GetNodeCount_AllNodes()
        {
            var count = _db.GetNodeCount();
            Assert.AreEqual(7, count);
        }

        [Test]
        public void GetEdgeCount_AllEdges()
        {
            var count = _db.GetEdgeCount();
            Assert.AreEqual(5, count);
        }

        [Test]
        public void GetRecentlyChanged_ReturnsAll_WhenRecent()
        {
            var results = _db.GetRecentlyChanged(1);
            Assert.AreEqual(7, results.Count);
        }

        [Test]
        public void TraverseDependencies_OneHop()
        {
            var scene = _db.FindNodeByGuid("scene_guid");
            var deps = _db.TraverseDependencies(scene.Id, maxDepth: 1);
            Assert.AreEqual(2, deps.Count);
        }

        [Test]
        public void TraverseDependencies_MultiHop()
        {
            var scene = _db.FindNodeByGuid("scene_guid");
            var deps = _db.TraverseDependencies(scene.Id, maxDepth: 3);
            Assert.GreaterOrEqual(deps.Count, 3);
        }

        [Test]
        public void TraverseDependencies_MaxDepth_Respected()
        {
            var scene = _db.FindNodeByGuid("scene_guid");
            var shallow = _db.TraverseDependencies(scene.Id, maxDepth: 1);
            var deep = _db.TraverseDependencies(scene.Id, maxDepth: 10);
            Assert.LessOrEqual(shallow.Count, deep.Count);
        }

        // --- C1: defines-edge exclusion from dependency traversal ---

        [Test]
        public void TraverseDependencies_ScriptType_DoesNotReturnOwnMethods()
        {
            // Script S -defines-> ScriptType T -defines-> ScriptMethod M
            // T -code_references-> ScriptType U
            // Tracing from T should return U, NOT M.
            var scriptS   = _db.InsertNode(new NodeRecord("Script")     { Name = "Foo.cs",      Path = "Assets/Scripts/Foo.cs" });
            var typeT     = _db.InsertNode(new NodeRecord("ScriptType") { Name = "Foo",          Path = "Assets/Scripts/Foo.cs" });
            var methodM   = _db.InsertNode(new NodeRecord("ScriptMethod") { Name = "Foo.DoThing" });
            var typeU     = _db.InsertNode(new NodeRecord("ScriptType") { Name = "Bar",          Path = "Assets/Scripts/Bar.cs" });

            _db.InsertEdge(scriptS, typeT,   "defines");
            _db.InsertEdge(typeT,   methodM, "defines");
            _db.InsertEdge(typeT,   typeU,   "code_references");

            var deps = _db.TraverseDependencies(typeT, maxDepth: 5);

            var names = deps.Select(n => n.Name).ToList();
            Assert.IsTrue(names.Contains("Bar"),    "Expected real dependency 'Bar' in results");
            Assert.IsFalse(names.Contains("Foo.DoThing"), "Own method 'Foo.DoThing' must not appear in deps");
        }

        [Test]
        public void TraverseDependencies_Script_DoesNotReturnOwnTypesOrMethods()
        {
            // Script S -defines-> ScriptType T -defines-> ScriptMethod M
            // T -code_references-> ScriptType U
            // Tracing from S should return neither T, M (all via defines), but DOES return U
            // once T is excluded, U is never reached — so deps should be empty (defines skipped,
            // T never visited, U never reached). Confirm M is absent.
            var scriptS   = _db.InsertNode(new NodeRecord("Script")     { Name = "Baz.cs",      Path = "Assets/Scripts/Baz.cs" });
            var typeT     = _db.InsertNode(new NodeRecord("ScriptType") { Name = "Baz",          Path = "Assets/Scripts/Baz.cs" });
            var methodM   = _db.InsertNode(new NodeRecord("ScriptMethod") { Name = "Baz.Run" });
            var typeU     = _db.InsertNode(new NodeRecord("ScriptType") { Name = "Qux",          Path = "Assets/Scripts/Qux.cs" });

            _db.InsertEdge(scriptS, typeT,   "defines");
            _db.InsertEdge(typeT,   methodM, "defines");
            _db.InsertEdge(typeT,   typeU,   "code_references");

            var deps = _db.TraverseDependencies(scriptS, maxDepth: 5);

            var names = deps.Select(n => n.Name).ToList();
            Assert.IsFalse(names.Contains("Baz"),     "Own type 'Baz' must not appear via defines");
            Assert.IsFalse(names.Contains("Baz.Run"), "Own method 'Baz.Run' must not appear via defines");
        }

        [Test]
        public void TraverseDependencies_AssetDepsStillTraverse()
        {
            // Prefab P -references-> Material M -uses_texture-> Texture T
            // All of these should be returned — no defines edges involved.
            var prefabP   = _db.InsertNode(new NodeRecord("Prefab")    { Name = "HeroPrefab",   Path = "Assets/Prefabs/Hero.prefab",     Guid = "asset_p_guid" });
            var materialM = _db.InsertNode(new NodeRecord("Material")  { Name = "HeroMat",      Path = "Assets/Materials/HeroMat.mat",   Guid = "asset_m_guid" });
            var textureT  = _db.InsertNode(new NodeRecord("Texture")   { Name = "HeroTex",      Path = "Assets/Textures/HeroTex.png",    Guid = "asset_t_guid" });

            _db.InsertEdge(prefabP,   materialM, "references");
            _db.InsertEdge(materialM, textureT,  "uses_texture");

            var deps = _db.TraverseDependencies(prefabP, maxDepth: 5);

            var names = deps.Select(n => n.Name).ToList();
            Assert.IsTrue(names.Contains("HeroMat"), "Material must appear as dependency of prefab");
            Assert.IsTrue(names.Contains("HeroTex"), "Texture must appear as transitive dependency");
        }

        [Test]
        public void TraverseDependencies_AllEdgeTypes_WhenExclusionSetEmpty()
        {
            // Passing an empty exclusion set should restore the old all-edges behaviour
            // and return nodes reached via defines.
            var script   = _db.InsertNode(new NodeRecord("Script")     { Name = "Old.cs" });
            var type     = _db.InsertNode(new NodeRecord("ScriptType") { Name = "Old" });
            var method   = _db.InsertNode(new NodeRecord("ScriptMethod") { Name = "Old.Go" });

            _db.InsertEdge(script, type,   "defines");
            _db.InsertEdge(type,   method, "defines");

            var deps = _db.TraverseDependencies(script, maxDepth: 5,
                excludedEdgeTypes: new HashSet<string>());

            var names = deps.Select(n => n.Name).ToList();
            Assert.IsTrue(names.Contains("Old"),    "Type must appear when defines is not excluded");
            Assert.IsTrue(names.Contains("Old.Go"), "Method must appear when defines is not excluded");
        }

        [Test]
        public void FindNodesWithEdgeTo_FindsReverseReferences()
        {
            var scriptType = _db.FindNodeByGuid("script_guid");
            var refs = _db.FindNodesWithEdgeTo(scriptType.Id, "references");
            Assert.AreEqual(1, refs.Count);
            Assert.AreEqual("PlayerPrefab", refs[0].Name);
        }

        [Test]
        public void FindNodesWithEdgeTo_InstanceOf()
        {
            var scriptType = _db.FindNodeByGuid("script_guid");
            var instances = _db.FindNodesWithEdgeTo(scriptType.Id, "instance_of");
            Assert.AreEqual(1, instances.Count);
            Assert.AreEqual("PlayerController", instances[0].Name);
        }

        [Test]
        public void GetTypeCounts_ReturnsCounts()
        {
            var counts = _db.GetTypeCounts();
            Assert.AreEqual(1, counts["Scene"]);
            Assert.AreEqual(2, counts["GameObject"]);
            Assert.AreEqual(1, counts["Component"]);
        }
    }
}
