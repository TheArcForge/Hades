// Tests/Editor/Graph/GraphDatabaseCrudTests.cs
using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;

namespace ArcForge.Hades.Editor.Tests.Graph
{
    public class GraphDatabaseCrudTests
    {
        string _testDbPath;
        GraphDatabase _db;

        [SetUp]
        public void SetUp()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"hades_test_{System.Guid.NewGuid()}.db");
            _db = new GraphDatabase(_testDbPath);
        }

        [TearDown]
        public void TearDown()
        {
            _db?.Dispose();
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
            if (File.Exists(_testDbPath + "-wal")) File.Delete(_testDbPath + "-wal");
            if (File.Exists(_testDbPath + "-shm")) File.Delete(_testDbPath + "-shm");
        }

        [Test]
        public void InsertNode_ReturnsId()
        {
            var node = new NodeRecord("Scene", "guid1") { Name = "MainMenu", Path = "Assets/Scenes/MainMenu.unity" };
            var id = _db.InsertNode(node);
            Assert.Greater(id, 0);
        }

        [Test]
        public void InsertNode_ThenFindByGuid()
        {
            var node = new NodeRecord("Scene", "guid1") { Name = "MainMenu", Path = "Assets/Scenes/MainMenu.unity" };
            _db.InsertNode(node);

            var found = _db.FindNodeByGuid("guid1");
            Assert.IsNotNull(found);
            Assert.AreEqual("Scene", found.Type);
            Assert.AreEqual("MainMenu", found.Name);
        }

        [Test]
        public void FindNodeByGuid_NotFound_ReturnsNull()
        {
            var found = _db.FindNodeByGuid("nonexistent");
            Assert.IsNull(found);
        }

        [Test]
        public void InsertEdge_ReturnsId()
        {
            var n1 = _db.InsertNode(new NodeRecord("Scene", "guid1") { Name = "Scene1" });
            var n2 = _db.InsertNode(new NodeRecord("GameObject") { Name = "Player" });

            var edgeId = _db.InsertEdge(n1, n2, "contains");
            Assert.Greater(edgeId, 0);
        }

        [Test]
        public void FindEdgesFrom_ReturnsEdges()
        {
            var n1 = _db.InsertNode(new NodeRecord("Scene", "guid1") { Name = "Scene1" });
            var n2 = _db.InsertNode(new NodeRecord("GameObject") { Name = "Player" });
            var n3 = _db.InsertNode(new NodeRecord("GameObject") { Name = "Enemy" });
            _db.InsertEdge(n1, n2, "contains");
            _db.InsertEdge(n1, n3, "contains");

            var edges = _db.FindEdgesFrom(n1, "contains");
            Assert.AreEqual(2, edges.Count);
        }

        [Test]
        public void FindEdgesTo_ReturnsEdges()
        {
            var n1 = _db.InsertNode(new NodeRecord("Scene", "guid1") { Name = "Scene1" });
            var n2 = _db.InsertNode(new NodeRecord("GameObject") { Name = "Player" });
            _db.InsertEdge(n1, n2, "contains");

            var edges = _db.FindEdgesTo(n2, "contains");
            Assert.AreEqual(1, edges.Count);
            Assert.AreEqual(n1, edges[0].SourceNodeId);
        }

        [Test]
        public void DeleteNodesByGuid_CascadesEdges()
        {
            var n1 = _db.InsertNode(new NodeRecord("Scene", "guid1") { Name = "Scene1" });
            var n2 = _db.InsertNode(new NodeRecord("GameObject", "guid2") { Name = "Player" });
            _db.InsertEdge(n1, n2, "contains");

            _db.DeleteNodesByGuid("guid1");

            Assert.IsNull(_db.FindNodeByGuid("guid1"));
            var edges = _db.FindEdgesTo(n2);
            Assert.AreEqual(0, edges.Count);
        }

        [Test]
        public void UpdateNodePath_ChangesPath()
        {
            var node = new NodeRecord("Scene", "guid1") { Name = "Scene1", Path = "Assets/Old.unity" };
            var id = _db.InsertNode(node);

            _db.UpdateNodePath(id, "Assets/New.unity");

            var found = _db.FindNodeByGuid("guid1");
            Assert.AreEqual("Assets/New.unity", found.Path);
        }

        [Test]
        public void InsertNode_WithProperties_Preserved()
        {
            var node = new NodeRecord("Component", "guid1")
            {
                Name = "Light",
                Properties = new Dictionary<string, object> { { "intensity", 1.5 } }
            };
            _db.InsertNode(node);

            var found = _db.FindNodeByGuid("guid1");
            Assert.IsNotNull(found.Properties);
            Assert.IsTrue(found.Properties.ContainsKey("intensity"));
        }

        [Test]
        public void Transaction_CommitPersists()
        {
            _db.RunInTransaction(() =>
            {
                _db.InsertNode(new NodeRecord("Scene", "guid_tx") { Name = "TxScene" });
            });
            Assert.IsNotNull(_db.FindNodeByGuid("guid_tx"));
        }

        [Test]
        public void RecordScannedAsset_AndCheck()
        {
            _db.RecordScannedAsset("guid1", "hash123", 1);
            var hash = _db.GetScannedAssetHash("guid1");
            Assert.AreEqual("hash123", hash);
        }

        [Test]
        public void GetScannedAssetHash_Missing_ReturnsNull()
        {
            var hash = _db.GetScannedAssetHash("nonexistent");
            Assert.IsNull(hash);
        }

        [Test]
        public void GetScannedAssetScannerVersion_ReturnsVersion()
        {
            _db.RecordScannedAsset("guid1", "hash123", 2);
            var version = _db.GetScannedAssetScannerVersion("guid1");
            Assert.AreEqual(2, version);
        }
    }
}
