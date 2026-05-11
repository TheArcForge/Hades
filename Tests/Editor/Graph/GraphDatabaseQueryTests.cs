// Tests/Editor/Graph/GraphDatabaseQueryTests.cs
using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;

namespace ArcForge.Hades.Editor.Tests.Graph
{
    public class GraphDatabaseQueryTests
    {
        string _testDbPath;
        GraphDatabase _db;

        [SetUp]
        public void SetUp()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"hades_test_{System.Guid.NewGuid()}.db");
            _db = new GraphDatabase(_testDbPath);
            SeedTestData();
        }

        [TearDown]
        public void TearDown()
        {
            _db?.Dispose();
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
