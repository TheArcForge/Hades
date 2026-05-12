// Tests/Editor/Charon/RegressionRunnerTests.cs
using System;
using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Charon;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;
using ArcForge.Hades.Editor.MCP;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.Tests.Charon
{
    public class RegressionRunnerTests
    {
        string _testDbPath;
        CharonDatabase _db;

        [SetUp]
        public void SetUp()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"regression_test_{Guid.NewGuid()}.db");
            _db = new CharonDatabase(_testDbPath);
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
        public void EvalDatasetTables_ExistAfterInit()
        {
            Assert.IsTrue(_db.TableExists("eval_datasets"));
            Assert.IsTrue(_db.TableExists("eval_dataset_members"));
        }

        [Test]
        public void InsertAndGetEvalDataset()
        {
            var dataset = new EvalDataset
            {
                DatasetId = "ds_001",
                Name = "Phase 1 Regression",
                Description = "Happy path scenario 1",
                CreatedAt = 1000
            };

            _db.InsertEvalDataset(dataset);
            var retrieved = _db.GetEvalDataset("ds_001");

            Assert.IsNotNull(retrieved);
            Assert.AreEqual("ds_001", retrieved.DatasetId);
            Assert.AreEqual("Phase 1 Regression", retrieved.Name);
            Assert.AreEqual("Happy path scenario 1", retrieved.Description);
        }

        [Test]
        public void GetEvalDataset_ReturnsNullForUnknown()
        {
            var retrieved = _db.GetEvalDataset("nonexistent");
            Assert.IsNull(retrieved);
        }

        [Test]
        public void ListEvalDatasets_ReturnsAllInReverseChronological()
        {
            _db.InsertEvalDataset(new EvalDataset { DatasetId = "ds_old", Name = "Old", CreatedAt = 1000 });
            _db.InsertEvalDataset(new EvalDataset { DatasetId = "ds_new", Name = "New", CreatedAt = 2000 });

            var list = _db.ListEvalDatasets();
            Assert.AreEqual(2, list.Count);
            Assert.AreEqual("ds_new", list[0].DatasetId);
            Assert.AreEqual("ds_old", list[1].DatasetId);
        }

        [Test]
        public void InsertAndGetEvalDatasetMembers()
        {
            _db.InsertEvalDataset(new EvalDataset { DatasetId = "ds_001", Name = "Test", CreatedAt = 1000 });
            _db.InsertEvalDatasetMember(new EvalDatasetMember
            {
                DatasetId = "ds_001",
                TraceId = null,
                ToolName = "hades_ping",
                InputJson = "{}",
                ExpectedOutputJson = "{\"pong\":true}",
                Notes = "basic ping"
            });

            var members = _db.GetEvalDatasetMembers("ds_001");
            Assert.AreEqual(1, members.Count);
            Assert.AreEqual("hades_ping", members[0].ToolName);
            Assert.AreEqual("{\"pong\":true}", members[0].ExpectedOutputJson);
        }

        [Test]
        public void DeleteEvalDataset_RemovesDatasetAndMembers()
        {
            _db.InsertEvalDataset(new EvalDataset { DatasetId = "ds_001", Name = "Test", CreatedAt = 1000 });
            _db.InsertEvalDatasetMember(new EvalDatasetMember
            {
                DatasetId = "ds_001",
                ToolName = "hades_ping",
                InputJson = "{}",
                ExpectedOutputJson = "{\"pong\":true}"
            });

            _db.DeleteEvalDataset("ds_001");

            Assert.IsNull(_db.GetEvalDataset("ds_001"));
            Assert.AreEqual(0, _db.GetEvalDatasetMembers("ds_001").Count);
        }

        [Test]
        public void Record_CreatesDatasetWithMembers()
        {
            var runner = new RegressionRunner(_db);
            var snapshots = new[]
            {
                new ToolCallSnapshot("hades_ping", "{}", "{\"pong\":true}"),
                new ToolCallSnapshot("hades_charon_status", "{}", "{\"enabled\":true,\"buffer_count\":0}")
            };

            var datasetId = runner.Record("Phase 1 regression", snapshots);

            var dataset = _db.GetEvalDataset(datasetId);
            Assert.IsNotNull(dataset);
            Assert.AreEqual("Phase 1 regression", dataset.Name);

            var members = _db.GetEvalDatasetMembers(datasetId);
            Assert.AreEqual(2, members.Count);
        }

        [Test]
        public void Record_StoresToolNameAndInputOutput()
        {
            var runner = new RegressionRunner(_db);
            var snapshots = new[]
            {
                new ToolCallSnapshot("hades_ping", "{\"key\":\"val\"}", "{\"pong\":true}")
            };

            var datasetId = runner.Record("test", snapshots);
            var members = _db.GetEvalDatasetMembers(datasetId);

            Assert.AreEqual("hades_ping", members[0].ToolName);
            Assert.AreEqual("{\"key\":\"val\"}", members[0].InputJson);
            Assert.AreEqual("{\"pong\":true}", members[0].ExpectedOutputJson);
        }

        [Test]
        public void Replay_PassesWhenOutputMatches()
        {
            CharonEmitter.Initialize(_db);
            var dispatcher = new MCPDispatcher();

            var runner = new RegressionRunner(_db);

            var pingResult = dispatcher.CallTool("hades_ping", new JObject());
            var snapshots = new[]
            {
                new ToolCallSnapshot("hades_ping", "{}", pingResult.Text)
            };

            var datasetId = runner.Record("replay test", snapshots);
            var report = runner.Replay(datasetId, dispatcher);

            Assert.AreEqual(1, report.Total);
            Assert.AreEqual(1, report.Passed);
            Assert.AreEqual(0, report.Failed);
            Assert.IsTrue(report.Results[0].Passed);

            CharonEmitter.Shutdown();
        }

        [Test]
        public void Replay_FailsWhenOutputDiffers()
        {
            CharonEmitter.Initialize(_db);
            var dispatcher = new MCPDispatcher();

            var runner = new RegressionRunner(_db);
            var snapshots = new[]
            {
                new ToolCallSnapshot("hades_ping", "{}", "{\"completely\":\"wrong\"}")
            };

            var datasetId = runner.Record("fail test", snapshots);
            var report = runner.Replay(datasetId, dispatcher);

            Assert.AreEqual(1, report.Total);
            Assert.AreEqual(0, report.Passed);
            Assert.AreEqual(1, report.Failed);
            Assert.IsFalse(report.Results[0].Passed);
            Assert.IsNotNull(report.Results[0].Diff);

            CharonEmitter.Shutdown();
        }

        [Test]
        public void Replay_ReturnsNullForUnknownDataset()
        {
            var runner = new RegressionRunner(_db);
            var dispatcher = new MCPDispatcher();

            var report = runner.Replay("nonexistent", dispatcher);

            Assert.IsNull(report);
        }

        [Test]
        public void Replay_HandlesMultipleSnapshots()
        {
            CharonEmitter.Initialize(_db);
            var dispatcher = new MCPDispatcher();

            var runner = new RegressionRunner(_db);

            var pingResult = dispatcher.CallTool("hades_ping", new JObject());
            var statusResult = dispatcher.CallTool("hades_charon_status", new JObject());

            var snapshots = new[]
            {
                new ToolCallSnapshot("hades_ping", "{}", pingResult.Text),
                new ToolCallSnapshot("hades_charon_status", "{}", statusResult.Text)
            };

            var datasetId = runner.Record("multi test", snapshots);
            var report = runner.Replay(datasetId, dispatcher);

            Assert.AreEqual(2, report.Total);
            Assert.AreEqual(2, report.Passed);

            CharonEmitter.Shutdown();
        }

        [Test]
        public void RecordFromTrace_ExtractsToolCallsFromSpans()
        {
            CharonEmitter.Initialize(_db);
            var dispatcher = new MCPDispatcher();

            // Execute a tool call with tracing so it generates a trace
            dispatcher.CallToolWithTracing("hades_ping", new JObject());
            CharonEmitter.Flush();

            // Get the trace that was just created
            var traces = _db.ListTraces(1, namePattern: "%hades_ping%");
            Assert.AreEqual(1, traces.Count, "Expected a trace from the hades_ping call");

            var runner = new RegressionRunner(_db);
            var datasetId = runner.RecordFromTrace(traces[0].TraceId, "from trace test", dispatcher);

            Assert.IsNotNull(datasetId);
            var members = _db.GetEvalDatasetMembers(datasetId);
            Assert.GreaterOrEqual(members.Count, 1);
            Assert.AreEqual("hades_ping", members[0].ToolName);

            CharonEmitter.Shutdown();
        }

        [Test]
        public void RecordFromTrace_ReturnsNullForUnknownTrace()
        {
            var runner = new RegressionRunner(_db);
            var dispatcher = new MCPDispatcher();

            var result = runner.RecordFromTrace("nonexistent", "test", dispatcher);
            Assert.IsNull(result);
        }

        [Test]
        public void Phase1HappyPath_GetProjectSummary_RegressionPasses()
        {
            // Set up graph with test data
            var graphDbPath = Path.Combine(Path.GetTempPath(), $"hades_graph_test_{Guid.NewGuid()}.db");
            var graphDb = new GraphDatabase(graphDbPath);

            var scene = graphDb.InsertNode(new NodeRecord("Scene", "s1") { Name = "MainScene", Path = "Assets/Scenes/Main.unity" });
            var go = graphDb.InsertNode(new NodeRecord("GameObject") { Name = "Player" });
            var comp = graphDb.InsertNode(new NodeRecord("Component") { Name = "PlayerController" });
            var script = graphDb.InsertNode(new NodeRecord("ScriptType", "st1") { Name = "PlayerController", Path = "Assets/Scripts/PlayerController.cs" });
            graphDb.InsertEdge(scene, go, "contains");
            graphDb.InsertEdge(go, comp, "contains");
            graphDb.InsertEdge(comp, script, "instance_of");

            CharonEmitter.Initialize(_db);
            var dispatcher = new MCPDispatcher();

            try
            {
                // Record: call get_project_summary and capture output
                var result = dispatcher.CallTool("get_project_summary", new JObject());
                Assert.IsFalse(result.IsError, "get_project_summary should succeed");

                var runner = new RegressionRunner(_db);
                var snapshots = new[] { new ToolCallSnapshot("get_project_summary", "{}", result.Text) };
                var datasetId = runner.Record("Phase 1 Happy Path: project summary", snapshots);

                // Replay: should produce identical output
                var report = runner.Replay(datasetId, dispatcher);
                Assert.AreEqual(1, report.Total);
                Assert.AreEqual(1, report.Passed, $"Regression failed:\n{report.Results[0].Diff}");
                Assert.AreEqual(0, report.Failed);
            }
            finally
            {
                CharonEmitter.Shutdown();
                graphDb.Dispose();
                if (File.Exists(graphDbPath)) File.Delete(graphDbPath);
                if (File.Exists(graphDbPath + "-wal")) File.Delete(graphDbPath + "-wal");
                if (File.Exists(graphDbPath + "-shm")) File.Delete(graphDbPath + "-shm");
            }
        }
    }
}
