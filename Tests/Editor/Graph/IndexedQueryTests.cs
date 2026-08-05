// Tests/Editor/Graph/IndexedQueryTests.cs
using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;

namespace ArcForge.Hades.Editor.Tests.Graph
{
    /// <summary>
    /// Phase A (felt-performance #2): lazy NodeRecord.Properties parsing and the indexed
    /// path / name+type lookups that replaced the SearchByName(null,null) full-table scans.
    /// </summary>
    public class IndexedQueryTests
    {
        // ---- Lazy Properties ----

        [Test]
        public void PropertiesJson_RoundTripsWithoutEagerParse()
        {
            var n = new NodeRecord("Texture", "g");
            n.PropertiesJson = "{\"source\":\"meta\"}";
            // Round-trips the raw JSON verbatim (no serialize/deserialize)...
            Assert.AreEqual("{\"source\":\"meta\"}", n.PropertiesJson);
            // ...and still parses correctly on demand.
            Assert.AreEqual("meta", n.Properties["source"].ToString());
        }

        [Test]
        public void Properties_SetterClearsRawJson()
        {
            var n = new NodeRecord("X");
            n.PropertiesJson = "{\"a\":1}";
            n.Properties = new Dictionary<string, object> { { "b", 2 } };
            Assert.IsTrue(n.Properties.ContainsKey("b"));
            Assert.IsFalse(n.Properties.ContainsKey("a"));
        }

        [Test]
        public void PropertiesJson_Null_StaysNull()
        {
            var n = new NodeRecord("X");
            Assert.IsNull(n.PropertiesJson);
            Assert.IsNull(n.Properties);
            n.PropertiesJson = null;
            Assert.IsNull(n.Properties);
        }

        // ---- Indexed lookups ----

        [Test]
        public void FindNodesByPath_UsesExactPath()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"hades_idx_{System.Guid.NewGuid()}.db");
            var saved = GraphDatabase.Instance;
            var db = new GraphDatabase(dbPath);
            try
            {
                db.InsertNode(new NodeRecord("Texture", "g1") { Name = "hero", Path = "Assets/hero.png" });
                db.InsertNode(new NodeRecord("Texture", "g2") { Name = "villain", Path = "Assets/villain.png" });

                var hits = db.FindNodesByPath("Assets/hero.png");
                Assert.AreEqual(1, hits.Count);
                Assert.AreEqual("g1", hits[0].Guid);
                Assert.AreEqual(0, db.FindNodesByPath("Assets/nope.png").Count);
            }
            finally
            {
                db.Dispose();
                GraphDatabase.RestoreInstanceForTests(saved);
                foreach (var e in new[] { "", "-wal", "-shm" }) if (File.Exists(dbPath + e)) File.Delete(dbPath + e);
            }
        }

        [Test]
        public void FindNodesByPath_ReturnsAllCoLocatedNodes()
        {
            // A Script and its ScriptType share one .cs path — both must come back (matches the
            // old SearchByName(null,null).Where(PathMatches) result set the .cs tools rely on).
            var dbPath = Path.Combine(Path.GetTempPath(), $"hades_idx_{System.Guid.NewGuid()}.db");
            var saved = GraphDatabase.Instance;
            var db = new GraphDatabase(dbPath);
            try
            {
                db.InsertNode(new NodeRecord("Script", "s1") { Name = "Foo.cs", Path = "Assets/Foo.cs" });
                db.InsertNode(new NodeRecord("ScriptType", "t1") { Name = "Foo", Path = "Assets/Foo.cs" });

                var hits = db.FindNodesByPath("Assets/Foo.cs");
                Assert.AreEqual(2, hits.Count);
                // ORDER BY name: the ScriptType ("Foo") must sort before the Script ("Foo.cs").
                // trace_dependencies starts from hits[0] and must key off the type node.
                Assert.AreEqual("Foo", hits[0].Name);
            }
            finally
            {
                db.Dispose();
                GraphDatabase.RestoreInstanceForTests(saved);
                foreach (var e in new[] { "", "-wal", "-shm" }) if (File.Exists(dbPath + e)) File.Delete(dbPath + e);
            }
        }

        [Test]
        public void FindNodesByNameAndTypeAll_FiltersOnNameAndType()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"hades_idx_{System.Guid.NewGuid()}.db");
            var saved = GraphDatabase.Instance;
            var db = new GraphDatabase(dbPath);
            try
            {
                db.InsertNode(new NodeRecord("Component") { Name = "Health" });
                db.InsertNode(new NodeRecord("Component") { Name = "Health" });
                db.InsertNode(new NodeRecord("Component") { Name = "Other" });
                db.InsertNode(new NodeRecord("ScriptType", "st") { Name = "Health", Path = "Assets/Health.cs" });

                var hits = db.FindNodesByNameAndTypeAll("Health", "Component");
                Assert.AreEqual(2, hits.Count, "two Component nodes named Health; the ScriptType is excluded by type");
                Assert.AreEqual(0, db.FindNodesByNameAndTypeAll("Missing", "Component").Count);
            }
            finally
            {
                db.Dispose();
                GraphDatabase.RestoreInstanceForTests(saved);
                foreach (var e in new[] { "", "-wal", "-shm" }) if (File.Exists(dbPath + e)) File.Delete(dbPath + e);
            }
        }

        [Test]
        public void FindNodesByTypeAndTier_ScopesToTier()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"hades_tier_{System.Guid.NewGuid()}.db");
            var saved = GraphDatabase.Instance;
            var db = new GraphDatabase(dbPath);
            try
            {
                db.InsertNode(new NodeRecord("ScriptType", "p1") { Name = "PlayerManager" });          // tier "project" (default)
                db.InsertNode(new NodeRecord("ScriptType", "b1") { Name = "MonoBehaviour" }, "builtin"); // tier "builtin"

                var project = db.FindNodesByTypeAndTier("ScriptType", "project");
                Assert.AreEqual(1, project.Count);
                Assert.AreEqual("PlayerManager", project[0].Name);
                Assert.AreEqual(1, db.FindNodesByTypeAndTier("ScriptType", "builtin").Count);
            }
            finally
            {
                db.Dispose();
                GraphDatabase.RestoreInstanceForTests(saved);
                foreach (var e in new[] { "", "-wal", "-shm" }) if (File.Exists(dbPath + e)) File.Delete(dbPath + e);
            }
        }
    }
}
