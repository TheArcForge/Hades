using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.Tests.MCP.Tools
{
    public class GraphQueryToolsSearchTests
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
            SeedData();
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

        void SeedData()
        {
            var scene = _db.InsertNode(new NodeRecord("Scene", "s1") { Name = "Gameplay", Path = "Assets/Scenes/Gameplay.unity" });
            var go = _db.InsertNode(new NodeRecord("GameObject") { Name = "PlayerObject" });
            var comp = _db.InsertNode(new NodeRecord("Component") { Name = "PlayerController" });
            var script = _db.InsertNode(new NodeRecord("ScriptType", "st1") { Name = "PlayerController", Path = "Assets/Scripts/PlayerController.cs" });
            var mat = _db.InsertNode(new NodeRecord("Material", "m1") { Name = "PlayerMat", Path = "Assets/Materials/Player.mat" });

            _db.InsertEdge(scene, go, "contains");
            _db.InsertEdge(go, comp, "contains");
            _db.InsertEdge(comp, script, "instance_of");
            _db.InsertEdge(comp, mat, "uses_material");
        }

        [Test]
        public void SearchByName_FindsResults()
        {
            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.SearchByName("Player%", "");
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            var matches = obj["result"]["matches"] as JArray;
            Assert.GreaterOrEqual(matches.Count, 1);
        }

        [Test]
        public void SearchByName_WithTypeFilter()
        {
            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.SearchByName("Player%", "Material");
            var obj = JObject.Parse(result.Text);
            var matches = obj["result"]["matches"] as JArray;
            Assert.AreEqual(1, matches.Count);
        }

        [Test]
        public void TraceDependencies_ReturnsChain()
        {
            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.TraceDependencies("Assets/Scenes/Gameplay.unity", 3);
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            var deps = obj["result"]["dependencies"] as JArray;
            Assert.GreaterOrEqual(deps.Count, 1);
        }

        [Test]
        public void GetRecentlyChanged_ReturnsResults()
        {
            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.GetRecentlyChanged(24);
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            Assert.Greater(obj["result"]["count"].Value<int>(), 0);
        }

        [Test]
        public void QueryGraph_BasicQuery()
        {
            var query = @"{""from"":{""type"":""Scene""},""select"":[""name"",""path""],""limit"":10}";
            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.QueryGraph(query);
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            var rows = obj["result"]["rows"] as JArray;
            Assert.AreEqual(1, rows.Count);
        }
    }
}
