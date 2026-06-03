using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;
using ArcForge.Hades.Editor.MCP;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.Tests.MCP.Tools
{
    public class GraphQueryToolsProjectTests
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
            _db.InsertNode(new NodeRecord("Project") { Name = "TestProject" });
            _db.InsertNode(new NodeRecord("Scene", "s1") { Name = "MainMenu", Path = "Assets/Scenes/MainMenu.unity" });
            _db.InsertNode(new NodeRecord("Scene", "s2") { Name = "Gameplay", Path = "Assets/Scenes/Gameplay.unity" });
            _db.InsertNode(new NodeRecord("Prefab", "p1") { Name = "Player", Path = "Assets/Prefabs/Player.prefab" });
            _db.InsertNode(new NodeRecord("Script", "sc1") { Name = "PlayerController.cs", Path = "Assets/Scripts/PlayerController.cs" });
            _db.InsertNode(new NodeRecord("ScriptType", "st1") { Name = "PlayerController" });
        }

        [Test]
        public void GetProjectSummary_ReturnsResult()
        {
            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.GetProjectSummary("shallow");
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            Assert.IsNotNull(obj["result"]);
            Assert.IsNotNull(obj["confidence"]);
        }

        [Test]
        public void GetProjectSummary_IncludesCounts()
        {
            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.GetProjectSummary("shallow");
            var obj = JObject.Parse(result.Text);

            var resultData = obj["result"];
            Assert.AreEqual(2, resultData["scene_count"].Value<int>());
            Assert.AreEqual(1, resultData["prefab_count"].Value<int>());
            Assert.AreEqual(1, resultData["script_count"].Value<int>());
        }

        [Test]
        public void HadesStatus_ReturnsGraphInfo()
        {
            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.HadesStatus();
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            Assert.IsNotNull(obj["result"]);
        }
    }
}
