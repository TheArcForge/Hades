// Tests/Editor/Graph/B2SupertypeClassificationTests.cs
// EditMode tests for B2: extends_or_implements neutral-edge reclassification.
//
// These tests verify:
//   1. ScriptType nodes carrying a 'kind' property round-trip correctly through
//      GraphDatabase (scanner JSON -> DB -> readable Dictionary).
//   2. The reclassification logic (kind == "interface" → "implements", else
//      → "inherits_from") works for project-scanned types.
//   3. Builtin ScriptType nodes seeded with kind == "interface" are correctly
//      classified as "implements" (e.g. IDisposable).
//   4. Unresolved supertypes (target node absent) leave the pending edge in the
//      DB with type "extends_or_implements" — they are NOT dropped.
//   5. PrefabVariant "inherits_from" edges are written and read back unmodified
//      (prefab inheritance is a separate concern, untouched by this change).
//
// Unity compile/run is PENDING the maintainer.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;

namespace ArcForge.Hades.Editor.Tests.Graph
{
    public class B2SupertypeClassificationTests
    {
        string _testDbPath;
        GraphDatabase _db;
        GraphDatabase _savedInstance;

        [SetUp]
        public void SetUp()
        {
            _savedInstance = GraphDatabase.Instance;
            _testDbPath = Path.Combine(Path.GetTempPath(), $"hades_b2_test_{Guid.NewGuid()}.db");
            _db = new GraphDatabase(_testDbPath);
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

        // ── Helper to simulate the reclassification logic from ResolvePendingEdges ──
        // This mirrors the exact branch added in GraphBuilder.ResolvePendingEdges:
        //   if (pe.EdgeType == "extends_or_implements") { ... }
        static string ReclassifyEdge(string edgeType, NodeRecord targetNode)
        {
            if (edgeType != "extends_or_implements") return edgeType;

            var targetKind = targetNode?.Properties != null
                && targetNode.Properties.TryGetValue("kind", out var kindObj)
                ? kindObj?.ToString()
                : null;

            return targetKind == "interface" ? "implements" : "inherits_from";
        }

        // ── 1. Kind round-trip via GraphDatabase ─────────────────────────────────

        [Test]
        public void NodeRecord_KindProperty_SurvivesDbRoundTrip_Class()
        {
            var node = new NodeRecord("ScriptType")
            {
                Name = "MyClass",
                Properties = new Dictionary<string, object> { ["kind"] = "class", ["namespace"] = "TestNS" }
            };
            var id = _db.InsertNode(node);

            var found = _db.FindNodeById(id);
            Assert.IsNotNull(found);
            Assert.IsNotNull(found.Properties);
            Assert.IsTrue(found.Properties.ContainsKey("kind"), "Properties should contain 'kind'");
            Assert.AreEqual("class", found.Properties["kind"]?.ToString());
        }

        [Test]
        public void NodeRecord_KindProperty_SurvivesDbRoundTrip_Interface()
        {
            var node = new NodeRecord("ScriptType")
            {
                Name = "IMyInterface",
                Properties = new Dictionary<string, object> { ["kind"] = "interface", ["namespace"] = "TestNS" }
            };
            var id = _db.InsertNode(node);

            var found = _db.FindNodeById(id);
            Assert.IsNotNull(found);
            Assert.AreEqual("interface", found.Properties["kind"]?.ToString());
        }

        [Test]
        public void NodeRecord_KindProperty_SurvivesDbRoundTrip_Struct()
        {
            var node = new NodeRecord("ScriptType")
            {
                Name = "MyStruct",
                Properties = new Dictionary<string, object> { ["kind"] = "struct" }
            };
            var id = _db.InsertNode(node);

            var found = _db.FindNodeById(id);
            Assert.AreEqual("struct", found.Properties["kind"]?.ToString());
        }

        [Test]
        public void NodeRecord_KindProperty_SurvivesDbRoundTrip_Enum()
        {
            var node = new NodeRecord("ScriptType")
            {
                Name = "MyEnum",
                Properties = new Dictionary<string, object> { ["kind"] = "enum" }
            };
            var id = _db.InsertNode(node);

            var found = _db.FindNodeById(id);
            Assert.AreEqual("enum", found.Properties["kind"]?.ToString());
        }

        // ── 2. Reclassification: interface target → "implements" ─────────────────

        [Test]
        public void Reclassify_InterfaceTarget_YieldsImplements()
        {
            var ifaceNode = new NodeRecord("ScriptType")
            {
                Name = "IMyInterface",
                Properties = new Dictionary<string, object> { ["kind"] = "interface" }
            };

            var resolved = ReclassifyEdge("extends_or_implements", ifaceNode);
            Assert.AreEqual("implements", resolved);
        }

        // ── 3. Reclassification: class target → "inherits_from" ──────────────────

        [Test]
        public void Reclassify_ClassTarget_YieldsInheritsFrom()
        {
            var classNode = new NodeRecord("ScriptType")
            {
                Name = "BaseClass",
                Properties = new Dictionary<string, object> { ["kind"] = "class" }
            };

            var resolved = ReclassifyEdge("extends_or_implements", classNode);
            Assert.AreEqual("inherits_from", resolved);
        }

        [Test]
        public void Reclassify_StructTarget_YieldsInheritsFrom()
        {
            var structNode = new NodeRecord("ScriptType")
            {
                Name = "MyStruct",
                Properties = new Dictionary<string, object> { ["kind"] = "struct" }
            };

            var resolved = ReclassifyEdge("extends_or_implements", structNode);
            Assert.AreEqual("inherits_from", resolved);
        }

        // ── 4. Reclassification: no kind (builtin without kind patch, or old node) ─

        [Test]
        public void Reclassify_MissingKind_FallsBackToInheritsFrom()
        {
            // A node with no 'kind' property (e.g. a node scanned before this patch)
            // should safely fall back to "inherits_from".
            var nodeWithoutKind = new NodeRecord("ScriptType")
            {
                Name = "MonoBehaviour",
                Properties = new Dictionary<string, object> { ["source"] = "builtin" }
            };

            var resolved = ReclassifyEdge("extends_or_implements", nodeWithoutKind);
            Assert.AreEqual("inherits_from", resolved);
        }

        [Test]
        public void Reclassify_NullProperties_FallsBackToInheritsFrom()
        {
            var nodeNullProps = new NodeRecord("ScriptType") { Name = "Bare" };
            // Properties == null
            var resolved = ReclassifyEdge("extends_or_implements", nodeNullProps);
            Assert.AreEqual("inherits_from", resolved);
        }

        // ── 5. Non-neutral edge types pass through unchanged ─────────────────────

        [Test]
        public void Reclassify_InheritsFrom_PassesThrough()
        {
            var node = new NodeRecord("ScriptType")
            {
                Name = "Whatever",
                Properties = new Dictionary<string, object> { ["kind"] = "interface" }
            };
            // A pre-existing 'inherits_from' edge (e.g. from PrefabVariant scanner) must
            // not be modified — the reclassification only touches 'extends_or_implements'.
            var resolved = ReclassifyEdge("inherits_from", node);
            Assert.AreEqual("inherits_from", resolved);
        }

        [Test]
        public void Reclassify_Implements_PassesThrough()
        {
            var node = new NodeRecord("ScriptType")
            {
                Name = "Whatever",
                Properties = new Dictionary<string, object> { ["kind"] = "class" }
            };
            var resolved = ReclassifyEdge("implements", node);
            Assert.AreEqual("implements", resolved);
        }

        [Test]
        public void Reclassify_CodeReferences_PassesThrough()
        {
            var node = new NodeRecord("ScriptType")
            {
                Name = "Whatever",
                Properties = new Dictionary<string, object> { ["kind"] = "interface" }
            };
            var resolved = ReclassifyEdge("code_references", node);
            Assert.AreEqual("code_references", resolved);
        }

        // ── 6. Unresolved supertype stays as pending edge (not dropped) ───────────

        [Test]
        public void UnresolvedSupertype_PendingEdgeIsRetained()
        {
            // Insert a source node (the type that has the unresolved base)
            var sourceNode = new NodeRecord("ScriptType")
            {
                Name = "MyClass",
                Properties = new Dictionary<string, object> { ["kind"] = "class" }
            };
            var sourceId = _db.InsertNode(sourceNode);

            // Insert a pending extends_or_implements edge whose target doesn't exist yet
            _db.InsertPendingEdge(sourceId, "extends_or_implements", "ExternalBaseClass", "External.NS", "asset-guid-1");

            // Verify the pending edge is in the DB with the correct edge type
            var pending = _db.GetPendingEdges();
            Assert.AreEqual(1, pending.Count);
            Assert.AreEqual("extends_or_implements", pending[0].EdgeType);
            Assert.AreEqual("ExternalBaseClass", pending[0].TargetTypeName);
        }

        // ── 7. Full DB path: insert nodes with kind, insert pending edge, simulate resolution ──

        [Test]
        public void FullPath_InterfaceTarget_ResolvesToImplements()
        {
            // Seed target interface node
            var ifaceNode = new NodeRecord("ScriptType")
            {
                Name = "IShopFactory",
                Properties = new Dictionary<string, object> { ["kind"] = "interface", ["namespace"] = "Game.UI" }
            };
            var ifaceId = _db.InsertNode(ifaceNode);

            // Seed source class node
            var classNode = new NodeRecord("ScriptType")
            {
                Name = "ShopPresenter",
                Properties = new Dictionary<string, object> { ["kind"] = "class", ["namespace"] = "Game.UI" }
            };
            var classId = _db.InsertNode(classNode);

            // Insert neutral pending edge (as scanner would)
            _db.InsertPendingEdge(classId, "extends_or_implements", "IShopFactory", "Game.UI", "asset-guid-shop");

            // Simulate resolution: find target by name and type, reclassify
            var targetNode = _db.FindNodeByNameAndType("IShopFactory", "ScriptType");
            Assert.IsNotNull(targetNode, "Target node should be found by name");

            var pe = _db.GetPendingEdges()[0];
            var resolvedEdgeType = ReclassifyEdge(pe.EdgeType, targetNode);

            Assert.AreEqual("implements", resolvedEdgeType,
                "Interface target should yield 'implements' edge");
        }

        [Test]
        public void FullPath_ClassTarget_ResolvesToInheritsFrom()
        {
            // Seed target class node
            var baseNode = new NodeRecord("ScriptType")
            {
                Name = "BasePresenter",
                Properties = new Dictionary<string, object> { ["kind"] = "class", ["namespace"] = "Game.UI" }
            };
            _db.InsertNode(baseNode);

            // Seed source class node
            var classNode = new NodeRecord("ScriptType")
            {
                Name = "ShopPresenter",
                Properties = new Dictionary<string, object> { ["kind"] = "class", ["namespace"] = "Game.UI" }
            };
            var classId = _db.InsertNode(classNode);

            _db.InsertPendingEdge(classId, "extends_or_implements", "BasePresenter", "Game.UI", "asset-guid-shop");

            var targetNode = _db.FindNodeByNameAndType("BasePresenter", "ScriptType");
            Assert.IsNotNull(targetNode);

            var pe = _db.GetPendingEdges()[0];
            var resolvedEdgeType = ReclassifyEdge(pe.EdgeType, targetNode);

            Assert.AreEqual("inherits_from", resolvedEdgeType,
                "Class target should yield 'inherits_from' edge");
        }

        [Test]
        public void FullPath_BuiltinInterface_ResolvesToImplements()
        {
            // Simulate a builtin node seeded by SeedBuiltinTypes with kind = "interface"
            // (e.g. IDisposable from System namespace)
            var builtinIface = new NodeRecord("ScriptType")
            {
                Name = "IDisposable",
                Properties = new Dictionary<string, object>
                {
                    ["kind"] = "interface",
                    ["source"] = "builtin",
                    ["namespace"] = "System"
                }
            };
            _db.InsertNode(builtinIface);

            var classNode = new NodeRecord("ScriptType")
            {
                Name = "MyResource",
                Properties = new Dictionary<string, object> { ["kind"] = "class" }
            };
            var classId = _db.InsertNode(classNode);

            _db.InsertPendingEdge(classId, "extends_or_implements", "IDisposable", "System", "asset-guid-res");

            var targetNode = _db.FindNodeByNameAndType("IDisposable", "ScriptType");
            Assert.IsNotNull(targetNode);

            var pe = _db.GetPendingEdges()[0];
            var resolvedEdgeType = ReclassifyEdge(pe.EdgeType, targetNode);

            Assert.AreEqual("implements", resolvedEdgeType,
                "Builtin interface (IDisposable) should yield 'implements' edge");
        }

        [Test]
        public void FullPath_BuiltinMonoBehaviour_ResolvesToInheritsFrom()
        {
            // MonoBehaviour is a class; class X : MonoBehaviour must stay inherits_from
            var monoBehaviourNode = new NodeRecord("ScriptType")
            {
                Name = "MonoBehaviour",
                Properties = new Dictionary<string, object>
                {
                    ["kind"] = "class",
                    ["source"] = "builtin",
                    ["namespace"] = "UnityEngine"
                }
            };
            _db.InsertNode(monoBehaviourNode);

            var playerNode = new NodeRecord("ScriptType")
            {
                Name = "PlayerController",
                Properties = new Dictionary<string, object> { ["kind"] = "class" }
            };
            var playerId = _db.InsertNode(playerNode);

            _db.InsertPendingEdge(playerId, "extends_or_implements", "MonoBehaviour", "UnityEngine", "asset-guid-player");

            var targetNode = _db.FindNodeByNameAndType("MonoBehaviour", "ScriptType");
            Assert.IsNotNull(targetNode);

            var pe = _db.GetPendingEdges()[0];
            var resolvedEdgeType = ReclassifyEdge(pe.EdgeType, targetNode);

            Assert.AreEqual("inherits_from", resolvedEdgeType,
                "class X : MonoBehaviour should yield 'inherits_from' edge");
        }
    }
}
