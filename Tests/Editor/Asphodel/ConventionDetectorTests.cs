using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;
using ArcForge.Hades.Editor.Asphodel.Conventions;

namespace ArcForge.Hades.Editor.Tests.Asphodel
{
    public class ConventionDetectorTests
    {
        GraphDatabase _db;
        GraphDatabase _saved;
        string _dbPath;

        [SetUp]
        public void SetUp()
        {
            _saved = GraphDatabase.Instance;
            _dbPath = Path.Combine(Path.GetTempPath(), $"hades_conv_{System.Guid.NewGuid()}.db");
            _db = new GraphDatabase(_dbPath);
        }

        [TearDown]
        public void TearDown()
        {
            _db?.Dispose();
            GraphDatabase.RestoreInstanceForTests(_saved);
            foreach (var e in new[] { "", "-wal", "-shm" }) if (File.Exists(_dbPath + e)) File.Delete(_dbPath + e);
        }

        // Helpers
        long Node(string type, string name, string guid = null, string propsJson = null)
        {
            var n = new NodeRecord(type, guid) { Name = name };
            if (propsJson != null) n.PropertiesJson = propsJson;
            return _db.InsertNode(n);
        }

        // ---- EventChannelDetector (B3) ----

        [Test]
        public void EventChannel_FiresOnReferencedChannels()
        {
            // 3 channel SOs of 2 types, each referenced by a component.
            var inv = Node("ScriptableObject", "InventoryUpdated", "so1", "{\"so_type\":\"Game.InventoryEventChannel\"}");
            var hp  = Node("ScriptableObject", "HealthChanged",    "so2", "{\"so_type\":\"Game.IntEventChannel\"}");
            var dmg = Node("ScriptableObject", "DamageDealt",      "so3", "{\"so_type\":\"Game.IntEventChannel\"}");
            var c1 = Node("Component", "InventoryUI");
            var c2 = Node("Component", "Hud");
            var c3 = Node("Component", "CombatSystem");
            _db.InsertEdge(c1, inv, "references");
            _db.InsertEdge(c2, hp, "references");
            _db.InsertEdge(c3, dmg, "references");

            var r = new EventChannelDetector().Detect(_db);
            Assert.IsTrue(r.Fired);
            Assert.AreEqual("patterns", r.TargetFile);
            StringAssert.Contains("event channel", r.Statement.ToLowerInvariant());
        }

        [Test]
        public void EventChannel_SilentWithoutChannels()
        {
            Node("ScriptableObject", "LevelConfig", "so1", "{\"so_type\":\"Game.LevelConfig\"}");
            Assert.IsFalse(new EventChannelDetector().Detect(_db).Fired);
        }

        [Test]
        public void EventChannel_SilentWhenUnreferenced()
        {
            // Channel types exist but nothing references them → not an established comms pattern.
            Node("ScriptableObject", "A", "so1", "{\"so_type\":\"Game.AEventChannel\"}");
            Node("ScriptableObject", "B", "so2", "{\"so_type\":\"Game.BEventChannel\"}");
            Assert.IsFalse(new EventChannelDetector().Detect(_db).Fired);
        }

        // ---- AddressablesDetector (B4) ----

        [Test]
        public void Addressables_FiresOnEntryVolume()
        {
            Node("AddressableGroup", "Default");
            for (int i = 0; i < 12; i++) Node("AddressableEntry", $"entry{i}", $"addr_entry:g:{i}");
            var r = new AddressablesDetector().Detect(_db);
            Assert.IsTrue(r.Fired);
            Assert.AreEqual("conventions", r.TargetFile);
            StringAssert.Contains("Addressables", r.Statement);
        }

        [Test]
        public void Addressables_SilentBelowThreshold()
        {
            Node("AddressableGroup", "Default");
            for (int i = 0; i < 3; i++) Node("AddressableEntry", $"entry{i}", $"addr_entry:g:{i}");
            Assert.IsFalse(new AddressablesDetector().Detect(_db).Fired);
        }

        // ---- PrefabVariantDetector (B5) ----

        [Test]
        public void PrefabVariants_FiresWhenVariantHeavy()
        {
            for (int i = 0; i < 6; i++) Node("Prefab", $"p{i}", $"pf{i}");
            for (int i = 0; i < 4; i++) Node("PrefabVariant", $"v{i}", $"pv{i}");
            var r = new PrefabVariantDetector().Detect(_db);
            Assert.IsTrue(r.Fired);
            Assert.AreEqual("patterns", r.TargetFile);
        }

        [Test]
        public void PrefabVariants_SilentWhenFew()
        {
            for (int i = 0; i < 6; i++) Node("Prefab", $"p{i}", $"pf{i}");
            Node("PrefabVariant", "v0", "pv0"); // 1/7 ≈ 14% < 20%
            Assert.IsFalse(new PrefabVariantDetector().Detect(_db).Fired);
        }

        // ---- ScriptableObjectConfigDetector (B6) ----

        [Test]
        public void SoConfig_FiresOnConfigVolume()
        {
            // 3 config types, ≥2 each, ≥10 total.
            string[] types = { "Game.ItemConfig", "Game.WeaponConfig", "Game.EnemyConfig" };
            int n = 0;
            foreach (var t in types)
                for (int i = 0; i < 4; i++)
                    Node("ScriptableObject", $"{t}_{i}", $"so{n++}", $"{{\"so_type\":\"{t}\"}}");
            var r = new ScriptableObjectConfigDetector().Detect(_db);
            Assert.IsTrue(r.Fired);
            Assert.AreEqual("patterns", r.TargetFile);
        }

        [Test]
        public void SoConfig_IgnoresChannels()
        {
            for (int i = 0; i < 12; i++)
                Node("ScriptableObject", $"ch{i}", $"so{i}", "{\"so_type\":\"Game.IntEventChannel\"}");
            Assert.IsFalse(new ScriptableObjectConfigDetector().Detect(_db).Fired,
                "channel SOs belong to the event-channel detector, not config");
        }

        // ---- NamingConventionDetector (B7) ----

        [Test]
        public void Naming_FiresOnStrongSuffixBucket()
        {
            foreach (var n in new[] { "AudioManager", "InputManager", "SceneManager", "PoolManager", "SaveManager" })
                Node("ScriptType", n, guid: null);                 // tier project (default)
            Node("ScriptType", "MonoBehaviour", "b", null);        // will be builtin below
            _db.InsertNode(new NodeRecord("ScriptType", "b2") { Name = "Object" }, "builtin");
            var r = new NamingConventionDetector().Detect(_db);
            Assert.IsTrue(r.Fired);
            Assert.AreEqual("conventions", r.TargetFile);
            StringAssert.Contains("Manager", r.Statement);
        }

        [Test]
        public void Naming_SilentWithoutBucket()
        {
            foreach (var n in new[] { "Foo", "Bar", "Baz" }) Node("ScriptType", n);
            Assert.IsFalse(new NamingConventionDetector().Detect(_db).Fired);
        }

        // ---- RenderPipelineDetector (B8) ----

        [Test]
        public void RenderPipeline_FiresOnUrp()
        {
            Node("RenderPipelineAsset", "UniversalRP", "rp1", "{\"pipeline_type\":\"UniversalRenderPipelineAsset\"}");
            var r = new RenderPipelineDetector().Detect(_db);
            Assert.IsTrue(r.Fired);
            Assert.AreEqual("conventions", r.TargetFile);
            StringAssert.Contains("URP", r.Statement);
        }

        [Test]
        public void RenderPipeline_SilentWhenBuiltIn()
        {
            Assert.IsFalse(new RenderPipelineDetector().Detect(_db).Fired);
        }
    }
}
