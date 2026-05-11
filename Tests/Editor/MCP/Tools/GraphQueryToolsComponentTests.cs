using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.Tests.MCP.Tools
{
    public class GraphQueryToolsComponentTests
    {
        string _testDbPath;
        GraphDatabase _db;

        [SetUp]
        public void SetUp()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"hades_test_{System.Guid.NewGuid()}.db");
            _db = new GraphDatabase(_testDbPath);
            SeedData();
        }

        [TearDown]
        public void TearDown()
        {
            _db?.Dispose();
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
            if (File.Exists(_testDbPath + "-wal")) File.Delete(_testDbPath + "-wal");
            if (File.Exists(_testDbPath + "-shm")) File.Delete(_testDbPath + "-shm");
        }

        void SeedData()
        {
            var prefab1 = _db.InsertNode(new NodeRecord("Prefab", "p1") { Name = "Player", Path = "Assets/Prefabs/Player.prefab" });
            var prefab2 = _db.InsertNode(new NodeRecord("Prefab", "p2") { Name = "Enemy", Path = "Assets/Prefabs/Enemy.prefab" });
            var go1 = _db.InsertNode(new NodeRecord("GameObject") { Name = "PlayerGO" });
            var go2 = _db.InsertNode(new NodeRecord("GameObject") { Name = "EnemyGO" });
            var comp1 = _db.InsertNode(new NodeRecord("Component") { Name = "PlayerHealth" });
            var comp2 = _db.InsertNode(new NodeRecord("Component") { Name = "EnemyHealth" });
            var scriptType = _db.InsertNode(new NodeRecord("ScriptType", "st_health") { Name = "PlayerHealth", Path = "Assets/Scripts/PlayerHealth.cs" });
            var orphanScript = _db.InsertNode(new NodeRecord("ScriptType", "st_orphan") { Name = "UnusedScript", Path = "Assets/Scripts/Unused.cs" });
            var script1 = _db.InsertNode(new NodeRecord("Script", "s1") { Name = "PlayerHealth.cs", Path = "Assets/Scripts/PlayerHealth.cs" });
            var script2 = _db.InsertNode(new NodeRecord("Script", "s2") { Name = "Unused.cs", Path = "Assets/Scripts/Unused.cs" });

            _db.InsertEdge(prefab1, go1, "contains");
            _db.InsertEdge(prefab2, go2, "contains");
            _db.InsertEdge(go1, comp1, "contains");
            _db.InsertEdge(go2, comp2, "contains");
            _db.InsertEdge(comp1, scriptType, "instance_of");
            _db.InsertEdge(comp2, scriptType, "instance_of");
            _db.InsertEdge(script1, scriptType, "defines");
            _db.InsertEdge(script2, orphanScript, "defines");
        }

        [Test]
        public void FindPrefabsWithComponent_FindsResults()
        {
            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindPrefabsWithComponent("PlayerHealth");
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            var prefabs = obj["result"]["prefabs"] as JArray;
            Assert.GreaterOrEqual(prefabs.Count, 1);
        }

        [Test]
        public void FindReferencesTo_FindsResults()
        {
            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo("Assets/Scripts/PlayerHealth.cs");
            Assert.IsFalse(result.IsError);
        }

        [Test]
        public void FindOrphanScripts_FindsUnused()
        {
            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindOrphanScripts();
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            var orphans = obj["result"]["orphan_scripts"] as JArray;
            Assert.GreaterOrEqual(orphans.Count, 1);
        }
    }
}
