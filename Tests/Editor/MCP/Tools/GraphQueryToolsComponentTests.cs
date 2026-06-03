using System.Collections.Generic;
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

        // ---------------------------------------------------------------
        // FindComponentsUsingPattern — supertypes property
        // ---------------------------------------------------------------

        void SeedPatternData()
        {
            // SingletonBehaviour<T> : base class match via supertypes
            _db.InsertNode(new NodeRecord("ScriptType", "st_singleton")
            {
                Name = "GameManager",
                Path = "Assets/Scripts/GameManager.cs",
                Properties = new Dictionary<string, object>
                {
                    ["kind"] = "class",
                    ["supertypes"] = new JArray(
                        new JObject { ["name"] = "SingletonBehaviour", ["genericArgs"] = new JArray("GameManager") }
                    )
                }
            });

            // Implements IEventChannel interface — match via supertypes
            _db.InsertNode(new NodeRecord("ScriptType", "st_event")
            {
                Name = "HealthChangedChannel",
                Path = "Assets/Scripts/HealthChangedChannel.cs",
                Properties = new Dictionary<string, object>
                {
                    ["kind"] = "class",
                    ["supertypes"] = new JArray(
                        new JObject { ["name"] = "ScriptableObject" },
                        new JObject { ["name"] = "IEventChannel" }
                    )
                }
            });

            // No supertypes — name match only
            _db.InsertNode(new NodeRecord("ScriptType", "st_singleton_named")
            {
                Name = "SingletonHelper",
                Path = "Assets/Scripts/SingletonHelper.cs",
                Properties = new Dictionary<string, object> { ["kind"] = "class" }
            });

            // Older node: no properties at all — must not throw
            _db.InsertNode(new NodeRecord("ScriptType", "st_legacy")
            {
                Name = "LegacyScript",
                Path = "Assets/Scripts/Legacy.cs"
            });
        }

        [Test]
        public void FindComponentsUsingPattern_MatchesBySupertypeBaseClass()
        {
            SeedPatternData();
            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindComponentsUsingPattern("Singleton");
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            var matches = obj["result"]["matches"] as JArray;

            // GameManager (supertypes has SingletonBehaviour) + SingletonHelper (name match)
            Assert.GreaterOrEqual(matches.Count, 2);
            var names = new System.Collections.Generic.HashSet<string>();
            foreach (JObject m in matches) names.Add(m["name"].ToString());
            Assert.IsTrue(names.Contains("GameManager"), "Expected GameManager matched via supertypes");
            Assert.IsTrue(names.Contains("SingletonHelper"), "Expected SingletonHelper matched via name");
        }

        [Test]
        public void FindComponentsUsingPattern_MatchesBySupertypeInterface()
        {
            SeedPatternData();
            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindComponentsUsingPattern("EventChannel");
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            var matches = obj["result"]["matches"] as JArray;

            Assert.GreaterOrEqual(matches.Count, 1);
            var names = new System.Collections.Generic.HashSet<string>();
            foreach (JObject m in matches) names.Add(m["name"].ToString());
            Assert.IsTrue(names.Contains("HealthChangedChannel"), "Expected HealthChangedChannel matched via IEventChannel supertype");
        }

        [Test]
        public void FindComponentsUsingPattern_NoSupertypesDoesNotThrow()
        {
            SeedPatternData();
            // LegacyScript has null Properties — must not throw, and must not appear in results
            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindComponentsUsingPattern("Legacy");
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            var matches = obj["result"]["matches"] as JArray;
            // LegacyScript matches by name
            Assert.GreaterOrEqual(matches.Count, 1);
        }

        [Test]
        public void FindComponentsUsingPattern_OutputContainsSupertypesField()
        {
            SeedPatternData();
            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindComponentsUsingPattern("Singleton");
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            var matches = obj["result"]["matches"] as JArray;
            foreach (JObject m in matches)
            {
                Assert.IsTrue(m.ContainsKey("supertypes"), $"Match '{m["name"]}' missing 'supertypes' field");
                Assert.IsInstanceOf<JArray>(m["supertypes"], $"Match '{m["name"]}' supertypes should be array");
            }
        }

        // ---------------------------------------------------------------
        // FindPrefabsWithComponent — full chain walk + variant de-dup
        // ---------------------------------------------------------------

        // Seeds a three-level hierarchy: Prefab → rootGO → childGO → Component
        long SeedDeepPrefab(string prefabGuid, string prefabName, string prefabPath,
            string goRootName, string goChildName, string compName)
        {
            var prefabId = _db.InsertNode(new NodeRecord("Prefab", prefabGuid) { Name = prefabName, Path = prefabPath });
            var rootGoId = _db.InsertNode(new NodeRecord("GameObject") { Name = goRootName });
            var childGoId = _db.InsertNode(new NodeRecord("GameObject") { Name = goChildName });
            var compId = _db.InsertNode(new NodeRecord("Component") { Name = compName });

            _db.InsertEdge(prefabId, rootGoId, "contains");
            _db.InsertEdge(rootGoId, childGoId, "contains");
            _db.InsertEdge(childGoId, compId, "contains");

            return prefabId;
        }

        [Test]
        public void FindPrefabsWithComponent_DeepNesting_FindsPrefab()
        {
            // Component is TWO hops below the prefab root (Prefab→rootGO→childGO→Component).
            // The old two-hop walk would miss it; the full chain walk must find it.
            SeedDeepPrefab("p_deep", "DeepPrefab", "Assets/Prefabs/DeepPrefab.prefab",
                "Root", "ChildGO", "DeepComponent");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindPrefabsWithComponent("DeepComponent");
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            var prefabs = obj["result"]["prefabs"] as JArray;
            Assert.IsNotNull(prefabs, "prefabs array must be present");
            Assert.AreEqual(1, prefabs.Count, "Expected exactly one prefab to be found");

            var hit = prefabs[0] as JObject;
            Assert.AreEqual("DeepPrefab", hit["name"]?.ToString());
            Assert.AreEqual("Assets/Prefabs/DeepPrefab.prefab", hit["path"]?.ToString());
        }

        [Test]
        public void FindPrefabsWithComponent_DirectChild_StillFound()
        {
            // Regression guard: component directly under the prefab root GO (old case) still works.
            // This is already covered by the base SeedData() wiring (Prefab→GO→Component), but
            // we add an explicit assertion here to guard the direct-child path.
            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindPrefabsWithComponent("PlayerHealth");
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            var prefabs = obj["result"]["prefabs"] as JArray;
            Assert.IsNotNull(prefabs);
            Assert.GreaterOrEqual(prefabs.Count, 1, "Direct-child component must still be found");

            bool foundPlayer = false;
            foreach (JObject p in prefabs)
                if (p["name"]?.ToString() == "Player") foundPlayer = true;
            Assert.IsTrue(foundPlayer, "Player prefab must be in results");
        }

        [Test]
        public void FindPrefabsWithComponent_Variant_InheritedOnly_LabelledInherited()
        {
            // Base prefab has the component; variant inherits it.
            // Expected: base hit is "direct"; variant hit is "inherited".
            var basePrefabId = _db.InsertNode(new NodeRecord("Prefab", "p_base") { Name = "BasePrefab", Path = "Assets/Prefabs/BasePrefab.prefab" });
            var baseGoId = _db.InsertNode(new NodeRecord("GameObject") { Name = "BaseGO" });
            var baseCompId = _db.InsertNode(new NodeRecord("Component") { Name = "SharedComp" });
            _db.InsertEdge(basePrefabId, baseGoId, "contains");
            _db.InsertEdge(baseGoId, baseCompId, "contains");

            // Variant: inherits_from base, AND has its own copy of the same component
            // (because LoadPrefabContents on a variant surfaces inherited components).
            var variantPrefabId = _db.InsertNode(new NodeRecord("PrefabVariant", "p_variant") { Name = "VariantPrefab", Path = "Assets/Prefabs/VariantPrefab.prefab" });
            var variantGoId = _db.InsertNode(new NodeRecord("GameObject") { Name = "VariantGO" });
            var variantCompId = _db.InsertNode(new NodeRecord("Component") { Name = "SharedComp" });
            _db.InsertEdge(variantPrefabId, variantGoId, "contains");
            _db.InsertEdge(variantGoId, variantCompId, "contains");
            _db.InsertEdge(variantPrefabId, basePrefabId, "inherits_from");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindPrefabsWithComponent("SharedComp");
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            var prefabs = obj["result"]["prefabs"] as JArray;
            Assert.IsNotNull(prefabs);

            JObject baseHit = null, variantHit = null;
            foreach (JObject p in prefabs)
            {
                if (p["name"]?.ToString() == "BasePrefab") baseHit = p;
                if (p["name"]?.ToString() == "VariantPrefab") variantHit = p;
            }

            Assert.IsNotNull(baseHit, "Base prefab must appear in results");
            Assert.IsNotNull(variantHit, "Variant prefab must appear in results");
            Assert.AreEqual("direct", baseHit["source"]?.ToString(), "Base must be labelled direct");
            Assert.AreEqual("inherited", variantHit["source"]?.ToString(), "Variant must be labelled inherited (base is in results)");

            // Headline count must not double-count: only the direct base should count.
            int directCount = (int)obj["result"]["count"];
            Assert.AreEqual(1, directCount, "Headline count must exclude inherited variants");
        }

        [Test]
        public void FindPrefabsWithComponent_Variant_OwnComponent_LabelledDirect()
        {
            // Variant adds a component that does NOT exist on its base.
            // Expected: variant is "direct"; base is NOT in results.
            var basePrefabId2 = _db.InsertNode(new NodeRecord("Prefab", "p_base2") { Name = "BaseOnly", Path = "Assets/Prefabs/BaseOnly.prefab" });
            // base has NO component of type "ExclusiveComp"

            var variantId = _db.InsertNode(new NodeRecord("PrefabVariant", "p_variant2") { Name = "VariantWithOwn", Path = "Assets/Prefabs/VariantWithOwn.prefab" });
            var vGoId = _db.InsertNode(new NodeRecord("GameObject") { Name = "VGO" });
            var vCompId = _db.InsertNode(new NodeRecord("Component") { Name = "ExclusiveComp" });
            _db.InsertEdge(variantId, vGoId, "contains");
            _db.InsertEdge(vGoId, vCompId, "contains");
            _db.InsertEdge(variantId, basePrefabId2, "inherits_from");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindPrefabsWithComponent("ExclusiveComp");
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            var prefabs = obj["result"]["prefabs"] as JArray;
            Assert.IsNotNull(prefabs);
            Assert.AreEqual(1, prefabs.Count, "Only variant should appear (base has no such component)");

            var hit = prefabs[0] as JObject;
            Assert.AreEqual("VariantWithOwn", hit["name"]?.ToString());
            Assert.AreEqual("direct", hit["source"]?.ToString(), "Variant with own component must be labelled direct");

            Assert.AreEqual(1, (int)obj["result"]["count"], "Headline count must include this direct variant hit");
        }
    }
}
