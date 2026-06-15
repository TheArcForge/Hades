using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;

namespace ArcForge.Hades.Editor.Tests.Graph
{
    public class OwnerGuidModelTests
    {
        string _dbPath;
        GraphDatabase _db;
        GraphDatabase _saved;

        [SetUp]
        public void SetUp()
        {
            _saved = GraphDatabase.Instance;
            _dbPath = Path.Combine(Path.GetTempPath(), $"hades_owner_{System.Guid.NewGuid()}.db");
            _db = new GraphDatabase(_dbPath);
        }

        [TearDown]
        public void TearDown()
        {
            _db?.Dispose();
            GraphDatabase.RestoreInstanceForTests(_saved);
            foreach (var ext in new[] { "", "-wal", "-shm" })
                if (File.Exists(_dbPath + ext)) File.Delete(_dbPath + ext);
        }

        [Test]
        public void InsertNode_PersistsOwnerGuid()
        {
            var id = _db.InsertNode(new NodeRecord("GameObject")
            {
                Name = "Player",
                FileId = 12345,
                OwnerGuid = "scene_guid"
            });

            var read = _db.FindNodeById(id);
            Assert.AreEqual("scene_guid", read.OwnerGuid);
            Assert.IsNull(read.Guid); // child keeps null own-guid
        }

        [Test]
        public void DeleteNodesByOwnerGuid_RemovesRootAndChildren()
        {
            var sceneId = _db.InsertNode(new NodeRecord("Scene", "scene_guid")
                { Name = "Main", Path = "Assets/Main.unity", OwnerGuid = "scene_guid" });
            var goId = _db.InsertNode(new NodeRecord("GameObject")
                { Name = "Player", FileId = 1, OwnerGuid = "scene_guid" });
            var compId = _db.InsertNode(new NodeRecord("Component")
                { Name = "Rigidbody", FileId = 2, OwnerGuid = "scene_guid" });
            _db.InsertEdge(sceneId, goId, "contains");
            _db.InsertEdge(goId, compId, "contains");

            _db.DeleteNodesByOwnerGuid("scene_guid");

            Assert.IsNull(_db.FindNodeById(sceneId));
            Assert.IsNull(_db.FindNodeById(goId));
            Assert.IsNull(_db.FindNodeById(compId));
            Assert.AreEqual(0, _db.GetEdgeCount()); // edges cascade
        }

        [Test]
        public void ReinsertingSceneByOwner_DoesNotAccumulateChildNodes()
        {
            // Simulate two incremental re-scans of the same scene: delete-by-owner, re-insert.
            void WriteScene()
            {
                _db.InsertNode(new NodeRecord("Scene", "scene_guid")
                    { Name = "Main", Path = "Assets/Main.unity", OwnerGuid = "scene_guid" });
                _db.InsertNode(new NodeRecord("GameObject")
                    { Name = "Player", FileId = 1, OwnerGuid = "scene_guid" });
                _db.InsertNode(new NodeRecord("Component")
                    { Name = "Rigidbody", FileId = 2, OwnerGuid = "scene_guid" });
            }

            WriteScene();
            var after1 = _db.GetNodeCount();

            _db.DeleteNodesByOwnerGuid("scene_guid"); // incremental re-scan deletes owned set
            WriteScene();
            var after2 = _db.GetNodeCount();

            Assert.AreEqual(after1, after2, "child nodes accumulated across re-scan (#7)");
        }
    }
}
