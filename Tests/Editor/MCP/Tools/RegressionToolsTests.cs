// Tests/Editor/MCP/Tools/RegressionToolsTests.cs
using System;
using System.IO;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using ArcForge.Hades.Editor.Charon;
using ArcForge.Hades.Editor.MCP;

namespace ArcForge.Hades.Editor.Tests.MCP.Tools
{
    public class RegressionToolsTests
    {
        string _testDbPath;
        CharonDatabase _db;
        MCPDispatcher _dispatcher;

        [SetUp]
        public void SetUp()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"regression_tools_test_{Guid.NewGuid()}.db");
            _db = new CharonDatabase(_testDbPath);
            CharonEmitter.Initialize(_db);
            _dispatcher = new MCPDispatcher();
        }

        [TearDown]
        public void TearDown()
        {
            CharonEmitter.Shutdown();
            _db?.Dispose();
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
            if (File.Exists(_testDbPath + "-wal")) File.Delete(_testDbPath + "-wal");
            if (File.Exists(_testDbPath + "-shm")) File.Delete(_testDbPath + "-shm");
        }

        [Test]
        public void RegressionRecord_ToolExists()
        {
            var result = _dispatcher.CallTool("hades_regression_record", new JObject
            {
                ["name"] = "test dataset",
                ["tool_calls"] = new JArray
                {
                    new JObject
                    {
                        ["tool_name"] = "hades_ping",
                        ["input"] = "{}",
                        ["expected_output"] = "{\"pong\":true}"
                    }
                }.ToString(Newtonsoft.Json.Formatting.None)
            });
            Assert.IsFalse(result.IsError);
        }

        [Test]
        public void RegressionRecord_ReturnsDatasetId()
        {
            var result = _dispatcher.CallTool("hades_regression_record", new JObject
            {
                ["name"] = "test dataset",
                ["tool_calls"] = new JArray
                {
                    new JObject
                    {
                        ["tool_name"] = "hades_ping",
                        ["input"] = "{}",
                        ["expected_output"] = "{\"pong\":true}"
                    }
                }.ToString(Newtonsoft.Json.Formatting.None)
            });

            var obj = JObject.Parse(result.Text);
            Assert.IsNotNull(obj["result"]["dataset_id"]);
            Assert.IsNotNull(obj["result"]["member_count"]);
        }

        [Test]
        public void RegressionReplay_ToolExists()
        {
            var recordResult = _dispatcher.CallTool("hades_regression_record", new JObject
            {
                ["name"] = "replay test",
                ["tool_calls"] = new JArray
                {
                    new JObject
                    {
                        ["tool_name"] = "hades_ping",
                        ["input"] = "{}",
                        ["expected_output"] = "{\"pong\":true}"
                    }
                }.ToString(Newtonsoft.Json.Formatting.None)
            });

            var datasetId = JObject.Parse(recordResult.Text)["result"]["dataset_id"].ToString();

            var result = _dispatcher.CallTool("hades_regression_replay", new JObject
            {
                ["dataset_id"] = datasetId
            });
            Assert.IsFalse(result.IsError);
        }

        [Test]
        public void RegressionReplay_ReturnsReport()
        {
            var pingResult = _dispatcher.CallTool("hades_ping", new JObject());

            var recordResult = _dispatcher.CallTool("hades_regression_record", new JObject
            {
                ["name"] = "report test",
                ["tool_calls"] = new JArray
                {
                    new JObject
                    {
                        ["tool_name"] = "hades_ping",
                        ["input"] = "{}",
                        ["expected_output"] = pingResult.Text
                    }
                }.ToString(Newtonsoft.Json.Formatting.None)
            });

            var datasetId = JObject.Parse(recordResult.Text)["result"]["dataset_id"].ToString();

            var replayResult = _dispatcher.CallTool("hades_regression_replay", new JObject
            {
                ["dataset_id"] = datasetId
            });

            var obj = JObject.Parse(replayResult.Text);
            Assert.AreEqual(1, (int)obj["result"]["total"]);
            Assert.AreEqual(1, (int)obj["result"]["passed"]);
            Assert.AreEqual(0, (int)obj["result"]["failed"]);
        }
    }
}
