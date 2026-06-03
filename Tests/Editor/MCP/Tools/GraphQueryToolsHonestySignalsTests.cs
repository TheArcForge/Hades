// Tests/Editor/MCP/Tools/GraphQueryToolsHonestySignalsTests.cs
// EditMode tests for Task D2 — three honesty signals on C# relationship tools:
//   Signal 1: package_scan_status degraded → confidence factor "package_scan: degraded"
//   Signal 2: external-unresolved pending edges → result field "supertypes_external_unresolved"
//   Signal 3: static_analysis_coverage partial → always-on confidence factor
//
// Verifies:
//   - each signal appears under the right condition
//   - each signal is ABSENT under the healthy/normal condition (no spam)
//   - results (references / dependencies) are still returned (additive, not filtering)
//
// Unity compile/run is PENDING the maintainer.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.Tests.MCP.Tools
{
    public class GraphQueryToolsHonestySignalsTests
    {
        string _testDbPath;
        GraphDatabase _db;
        GraphDatabase _savedInstance;

        [SetUp]
        public void SetUp()
        {
            _savedInstance = GraphDatabase.Instance;
            _testDbPath = Path.Combine(Path.GetTempPath(), $"hades_d2_test_{System.Guid.NewGuid()}.db");
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

        // -----------------------------------------------------------------------
        // Signal 3: static_analysis_coverage factor always present on relationship tools
        // -----------------------------------------------------------------------

        [Test]
        public void FindReferencesTo_AlwaysHas_StaticAnalysisCoverageFactor()
        {
            var script = _db.InsertNode(new NodeRecord("Script", "sa_script")
                { Name = "Foo.cs", Path = "Assets/Scripts/Foo.cs" });
            _db.InsertNode(new NodeRecord("ScriptType", "sa_type")
                { Name = "Foo", Path = "Assets/Scripts/Foo.cs" });

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Scripts/Foo.cs");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var factors = obj["confidence"]["factors"] as JArray;
            Assert.IsNotNull(factors, "Response must have confidence.factors array");

            var hasStaticAnalysis = factors.Any(f =>
                f["factor"]?.ToString() == "static_analysis_coverage" &&
                f["value"]?.ToString() == "partial");
            Assert.IsTrue(hasStaticAnalysis,
                "find_references_to must always carry static_analysis_coverage:partial factor");
        }

        [Test]
        public void TraceDependencies_AlwaysHas_StaticAnalysisCoverageFactor()
        {
            var script = _db.InsertNode(new NodeRecord("Script", "td_sa_script")
                { Name = "Bar.cs", Path = "Assets/Scripts/Bar.cs" });

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.TraceDependencies(
                "Assets/Scripts/Bar.cs");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var factors = obj["confidence"]["factors"] as JArray;
            Assert.IsNotNull(factors, "Response must have confidence.factors array");

            var hasStaticAnalysis = factors.Any(f =>
                f["factor"]?.ToString() == "static_analysis_coverage" &&
                f["value"]?.ToString() == "partial");
            Assert.IsTrue(hasStaticAnalysis,
                "trace_dependencies must always carry static_analysis_coverage:partial factor");
        }

        [Test]
        public void FindReferencesTo_StaticAnalysisCoverageFactor_NamesMissedIndirectForms()
        {
            // The blind_spots list must name at least reflection and runtime dispatch so
            // callers understand "no references" ≠ "definitely unused".
            _db.InsertNode(new NodeRecord("Script", "bs_script")
                { Name = "Baz.cs", Path = "Assets/Scripts/Baz.cs" });
            _db.InsertNode(new NodeRecord("ScriptType", "bs_type")
                { Name = "Baz", Path = "Assets/Scripts/Baz.cs" });

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Scripts/Baz.cs");

            var obj = JObject.Parse(result.Text);
            var factors = obj["confidence"]["factors"] as JArray;
            var saFactor = factors?.FirstOrDefault(f =>
                f["factor"]?.ToString() == "static_analysis_coverage");

            Assert.IsNotNull(saFactor, "static_analysis_coverage factor must be present");
            var blindSpots = saFactor["blind_spots"] as JArray;
            Assert.IsNotNull(blindSpots, "static_analysis_coverage factor must list blind_spots");

            var blindSpotValues = blindSpots.Select(b => b.ToString()).ToList();
            Assert.IsTrue(blindSpotValues.Any(s => s.Contains("reflection")),
                "blind_spots must mention reflection");
        }

        // -----------------------------------------------------------------------
        // Signal 1: package_scan degraded → confidence factor present / absent
        // -----------------------------------------------------------------------

        [Test]
        public void FindReferencesTo_PackageScanDegraded_AddsPackageScanFactor()
        {
            // Arrange: mark package scan as degraded in metadata.
            _db.SetMetadata("package_scan_status", "degraded");

            var script = _db.InsertNode(new NodeRecord("Script", "pkg_script")
                { Name = "Widget.cs", Path = "Assets/Scripts/Widget.cs" });
            _db.InsertNode(new NodeRecord("ScriptType", "pkg_type")
                { Name = "Widget", Path = "Assets/Scripts/Widget.cs" });

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Scripts/Widget.cs");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var factors = obj["confidence"]["factors"] as JArray;

            var hasPackageFactor = factors.Any(f =>
                f["factor"]?.ToString() == "package_scan" &&
                f["value"]?.ToString() == "degraded");
            Assert.IsTrue(hasPackageFactor,
                "package_scan:degraded factor must be present when package_scan_status is 'degraded'");
        }

        [Test]
        public void FindReferencesTo_PackageScanOk_NoPackageScanFactor()
        {
            // Arrange: mark package scan as OK.
            _db.SetMetadata("package_scan_status", "ok");

            var script = _db.InsertNode(new NodeRecord("Script", "ok_script")
                { Name = "Gadget.cs", Path = "Assets/Scripts/Gadget.cs" });
            _db.InsertNode(new NodeRecord("ScriptType", "ok_type")
                { Name = "Gadget", Path = "Assets/Scripts/Gadget.cs" });

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Scripts/Gadget.cs");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var factors = obj["confidence"]["factors"] as JArray;

            var hasPackageFactor = factors?.Any(f =>
                f["factor"]?.ToString() == "package_scan") ?? false;
            Assert.IsFalse(hasPackageFactor,
                "package_scan factor must be ABSENT when package_scan_status is 'ok'");
        }

        [Test]
        public void TraceDependencies_PackageScanDegraded_AddsPackageScanFactor()
        {
            _db.SetMetadata("package_scan_status", "degraded");

            var script = _db.InsertNode(new NodeRecord("Script", "td_pkg_script")
                { Name = "Engine.cs", Path = "Assets/Scripts/Engine.cs" });

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.TraceDependencies(
                "Assets/Scripts/Engine.cs");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var factors = obj["confidence"]["factors"] as JArray;

            var hasPackageFactor = factors.Any(f =>
                f["factor"]?.ToString() == "package_scan" &&
                f["value"]?.ToString() == "degraded");
            Assert.IsTrue(hasPackageFactor,
                "package_scan:degraded factor must be present in trace_dependencies when package_scan_status is 'degraded'");
        }

        [Test]
        public void TraceDependencies_PackageScanOk_NoPackageScanFactor()
        {
            _db.SetMetadata("package_scan_status", "ok");

            var script = _db.InsertNode(new NodeRecord("Script", "td_ok_script")
                { Name = "Motor.cs", Path = "Assets/Scripts/Motor.cs" });

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.TraceDependencies(
                "Assets/Scripts/Motor.cs");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var factors = obj["confidence"]["factors"] as JArray;

            var hasPackageFactor = factors?.Any(f =>
                f["factor"]?.ToString() == "package_scan") ?? false;
            Assert.IsFalse(hasPackageFactor,
                "package_scan factor must be ABSENT in trace_dependencies when package_scan_status is 'ok'");
        }

        // -----------------------------------------------------------------------
        // Signal 2: supertypes_external_unresolved field present/absent
        // -----------------------------------------------------------------------

        [Test]
        public void FindReferencesTo_ExternalPendingEdges_SurfacesUnresolvedCount()
        {
            // Arrange: a ScriptType node whose base class (in System namespace) is unresolved.
            // An extends_or_implements edge with namespace "System" qualifies as external.
            var scriptNode = _db.InsertNode(new NodeRecord("Script", "ext_script_a")
                { Name = "Disposer.cs", Path = "Assets/Scripts/Disposer.cs" });
            var typeNode = _db.InsertNode(new NodeRecord("ScriptType", "ext_type_a")
                { Name = "Disposer", Path = "Assets/Scripts/Disposer.cs" });

            // Pending edge from typeNode → unresolved external base (System namespace)
            _db.InsertPendingEdge(typeNode, "extends_or_implements",
                "IDisposable", "System", "ext_script_a");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Scripts/Disposer.cs");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var resultData = obj["result"];
            Assert.IsNotNull(resultData["supertypes_external_unresolved"],
                "supertypes_external_unresolved must be present when external pending edges exist");
            Assert.Greater(resultData["supertypes_external_unresolved"].Value<int>(), 0,
                "supertypes_external_unresolved count must be > 0");
        }

        [Test]
        public void FindReferencesTo_NoPendingEdges_NoUnresolvedField()
        {
            // Arrange: a type with no pending edges at all.
            var scriptNode = _db.InsertNode(new NodeRecord("Script", "clean_script")
                { Name = "Clean.cs", Path = "Assets/Scripts/Clean.cs" });
            var typeNode = _db.InsertNode(new NodeRecord("ScriptType", "clean_type")
                { Name = "Clean", Path = "Assets/Scripts/Clean.cs" });

            // No pending edges inserted.

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Scripts/Clean.cs");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var resultData = obj["result"];
            Assert.IsNull(resultData["supertypes_external_unresolved"],
                "supertypes_external_unresolved must be ABSENT when there are no external pending edges");
        }

        [Test]
        public void TraceDependencies_ExternalPendingEdges_SurfacesUnresolvedCount()
        {
            // Arrange: a ScriptType node whose base class in UnityEngine is unresolved.
            var scriptNode = _db.InsertNode(new NodeRecord("Script", "mb_script")
                { Name = "Player.cs", Path = "Assets/Scripts/Player.cs" });
            var typeNode = _db.InsertNode(new NodeRecord("ScriptType", "mb_type")
                { Name = "Player", Path = "Assets/Scripts/Player.cs" });

            // Pending edge → MonoBehaviour (UnityEngine namespace) — external by namespace
            _db.InsertPendingEdge(typeNode, "extends_or_implements",
                "MonoBehaviour", "UnityEngine", "mb_script");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.TraceDependencies(
                "Assets/Scripts/Player.cs");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var resultData = obj["result"];
            Assert.IsNotNull(resultData["supertypes_external_unresolved"],
                "supertypes_external_unresolved must be present when external pending edges exist");
            Assert.Greater(resultData["supertypes_external_unresolved"].Value<int>(), 0,
                "supertypes_external_unresolved count must be > 0");
        }

        [Test]
        public void TraceDependencies_NoPendingEdges_NoUnresolvedField()
        {
            var scriptNode = _db.InsertNode(new NodeRecord("Script", "td_clean_script")
                { Name = "Utility.cs", Path = "Assets/Scripts/Utility.cs" });

            // No pending edges inserted.

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.TraceDependencies(
                "Assets/Scripts/Utility.cs");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var resultData = obj["result"];
            Assert.IsNull(resultData["supertypes_external_unresolved"],
                "supertypes_external_unresolved must be ABSENT when there are no external pending edges");
        }

        [Test]
        public void FindReferencesTo_ProjectPendingEdges_DoNotCountAsExternal()
        {
            // A pending edge with no known-external namespace should NOT inflate the count.
            // This verifies the classification is selective (external only, not all unresolved).
            var scriptNode = _db.InsertNode(new NodeRecord("Script", "user_script")
                { Name = "Controller.cs", Path = "Assets/Scripts/Controller.cs" });
            var typeNode = _db.InsertNode(new NodeRecord("ScriptType", "user_type")
                { Name = "Controller", Path = "Assets/Scripts/Controller.cs" });

            // A pending edge with a user-namespace — this is a resolvable user-code type,
            // not a known-external. The namespace "Game.Core" is not in ExternalNamespaceRoots.
            _db.InsertPendingEdge(typeNode, "extends_or_implements",
                "BaseController", "Game.Core", "user_script");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Scripts/Controller.cs");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var resultData = obj["result"];
            Assert.IsNull(resultData["supertypes_external_unresolved"],
                "supertypes_external_unresolved must be ABSENT for user-code (non-external) pending edges");
        }

        // -----------------------------------------------------------------------
        // Additive: real references/dependencies still returned alongside signals
        // -----------------------------------------------------------------------

        [Test]
        public void FindReferencesTo_WithSignals_StillReturnsReferrers()
        {
            // Signals must be additive — real referrers are still in the result.
            _db.SetMetadata("package_scan_status", "degraded");

            var target = _db.InsertNode(new NodeRecord("ScriptType", "additive_target")
                { Name = "Service", Path = "Assets/Scripts/Service.cs" });
            var referrer = _db.InsertNode(new NodeRecord("ScriptType", "additive_referrer")
                { Name = "Consumer", Path = "Assets/Scripts/Consumer.cs" });
            _db.InsertEdge(referrer, target, "code_references");

            // Also add an external pending edge so signal 2 fires too.
            _db.InsertPendingEdge(target, "extends_or_implements",
                "IDisposable", "System", "some_guid");

            var result = ArcForge.Hades.Editor.MCP.Tools.GraphQueryTools.FindReferencesTo(
                "Assets/Scripts/Service.cs");

            Assert.IsFalse(result.IsError);
            var obj = JObject.Parse(result.Text);
            var refs = obj["result"]["references"] as JArray;
            var names = refs.Select(r => r["name"].ToString()).ToList();

            Assert.IsTrue(names.Contains("Consumer"),
                "Real referrers must still be returned even when honesty signals are present");
            Assert.IsNotNull(obj["result"]["supertypes_external_unresolved"],
                "Signal 2 must fire alongside the real referrers");
            var factors = obj["confidence"]["factors"] as JArray;
            Assert.IsTrue(factors.Any(f => f["factor"]?.ToString() == "package_scan"),
                "Signal 1 must fire alongside the real referrers");
            Assert.IsTrue(factors.Any(f => f["factor"]?.ToString() == "static_analysis_coverage"),
                "Signal 3 must fire alongside the real referrers");
        }

        // -----------------------------------------------------------------------
        // GetPendingEdgesForNode: basic DB round-trip
        // -----------------------------------------------------------------------

        [Test]
        public void GetPendingEdgesForNode_ReturnsOnlyEdgesForThatNode()
        {
            var nodeA = _db.InsertNode(new NodeRecord("ScriptType", "pending_a")
                { Name = "Alpha", Path = "Assets/Scripts/Alpha.cs" });
            var nodeB = _db.InsertNode(new NodeRecord("ScriptType", "pending_b")
                { Name = "Beta", Path = "Assets/Scripts/Beta.cs" });

            _db.InsertPendingEdge(nodeA, "extends_or_implements", "IBase", "System", "guid_a");
            _db.InsertPendingEdge(nodeB, "extends_or_implements", "IOther", "System", "guid_b");

            var edgesForA = _db.GetPendingEdgesForNode(nodeA);
            Assert.AreEqual(1, edgesForA.Count, "Should return only edges for nodeA");
            Assert.AreEqual(nodeA, edgesForA[0].SourceNodeId);
            Assert.AreEqual("IBase", edgesForA[0].TargetTypeName);

            var edgesForB = _db.GetPendingEdgesForNode(nodeB);
            Assert.AreEqual(1, edgesForB.Count, "Should return only edges for nodeB");
            Assert.AreEqual(nodeB, edgesForB[0].SourceNodeId);
        }

        [Test]
        public void GetPendingEdgesForNode_NoEdges_ReturnsEmptyList()
        {
            var node = _db.InsertNode(new NodeRecord("ScriptType", "no_pending")
                { Name = "Bare" });

            var edges = _db.GetPendingEdgesForNode(node);
            Assert.IsNotNull(edges);
            Assert.AreEqual(0, edges.Count);
        }
    }
}
