// Editor/Charon/RegressionRunner.cs
using System;
using System.Collections.Generic;
using ArcForge.Hades.Editor.MCP;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.Charon
{
    public class ToolCallSnapshot
    {
        public string ToolName { get; }
        public string InputJson { get; }
        public string ExpectedOutputJson { get; }

        public ToolCallSnapshot(string toolName, string inputJson, string expectedOutputJson)
        {
            ToolName = toolName;
            InputJson = inputJson;
            ExpectedOutputJson = expectedOutputJson;
        }
    }

    public class ReplayResult
    {
        public string ToolName { get; set; }
        public string InputJson { get; set; }
        public string ExpectedOutputJson { get; set; }
        public string ActualOutputJson { get; set; }
        public bool Passed { get; set; }
        public string Diff { get; set; }
    }

    public class ReplayReport
    {
        public string DatasetId { get; set; }
        public string DatasetName { get; set; }
        public int Total { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public List<ReplayResult> Results { get; set; } = new List<ReplayResult>();
    }

    public class RegressionRunner
    {
        readonly CharonDatabase _db;

        public RegressionRunner(CharonDatabase db)
        {
            _db = db;
        }

        public string Record(string name, IEnumerable<ToolCallSnapshot> snapshots)
        {
            var datasetId = "ds_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _db.InsertEvalDataset(new EvalDataset
            {
                DatasetId = datasetId,
                Name = name,
                CreatedAt = now
            });

            foreach (var snapshot in snapshots)
            {
                _db.InsertEvalDatasetMember(new EvalDatasetMember
                {
                    DatasetId = datasetId,
                    ToolName = snapshot.ToolName,
                    InputJson = snapshot.InputJson,
                    ExpectedOutputJson = snapshot.ExpectedOutputJson
                });
            }

            return datasetId;
        }

        public ReplayReport Replay(string datasetId, MCPDispatcher dispatcher)
        {
            var dataset = _db.GetEvalDataset(datasetId);
            if (dataset == null) return null;

            var members = _db.GetEvalDatasetMembers(datasetId);
            var report = new ReplayReport
            {
                DatasetId = datasetId,
                DatasetName = dataset.Name,
                Total = members.Count
            };

            foreach (var member in members)
            {
                var args = string.IsNullOrEmpty(member.InputJson) || member.InputJson == "{}"
                    ? new JObject()
                    : JObject.Parse(member.InputJson);

                var result = dispatcher.CallTool(member.ToolName, args);

                var replayResult = new ReplayResult
                {
                    ToolName = member.ToolName,
                    InputJson = member.InputJson,
                    ExpectedOutputJson = member.ExpectedOutputJson,
                    ActualOutputJson = result.Text
                };

                if (result.IsError)
                {
                    replayResult.Passed = false;
                    replayResult.Diff = $"Tool returned error: {result.Text}";
                }
                else
                {
                    replayResult.Passed = NormalizedEquals(member.ExpectedOutputJson, result.Text);
                    if (!replayResult.Passed)
                        replayResult.Diff = BuildDiff(member.ExpectedOutputJson, result.Text);
                }

                if (replayResult.Passed)
                    report.Passed++;
                else
                    report.Failed++;

                report.Results.Add(replayResult);
            }

            return report;
        }

        public string RecordFromTrace(string traceId, string name, MCPDispatcher dispatcher)
        {
            var trace = _db.GetTrace(traceId);
            if (trace == null) return null;

            var spans = _db.GetSpansByTraceId(traceId);
            var snapshots = new List<ToolCallSnapshot>();

            foreach (var span in spans)
            {
                if (span.Kind != SpanKind.Server) continue;
                if (!span.Name.StartsWith("mcp.tool.")) continue;

                var toolName = span.Attributes.ContainsKey("tool.name")
                    ? span.Attributes["tool.name"]
                    : span.Name.Substring("mcp.tool.".Length);

                var inputJson = span.Attributes.ContainsKey("tool.input")
                    ? span.Attributes["tool.input"]
                    : "{}";

                var args = inputJson == "{}" ? new JObject() : JObject.Parse(inputJson);
                var result = dispatcher.CallTool(toolName, args);

                if (!result.IsError)
                    snapshots.Add(new ToolCallSnapshot(toolName, inputJson, result.Text));
            }

            if (snapshots.Count == 0) return null;

            var datasetId = Record(name, snapshots);

            var members = _db.GetEvalDatasetMembers(datasetId);
            foreach (var member in members)
            {
                member.TraceId = traceId;
                _db.InsertEvalDatasetMember(member);
            }

            return datasetId;
        }

        static bool NormalizedEquals(string expectedJson, string actualJson)
        {
            try
            {
                var expected = JToken.Parse(expectedJson);
                var actual = JToken.Parse(actualJson);
                return JToken.DeepEquals(expected, actual);
            }
            catch
            {
                return expectedJson == actualJson;
            }
        }

        static string BuildDiff(string expectedJson, string actualJson)
        {
            try
            {
                var expected = JToken.Parse(expectedJson).ToString(Formatting.Indented);
                var actual = JToken.Parse(actualJson).ToString(Formatting.Indented);
                return $"Expected:\n{expected}\n\nActual:\n{actual}";
            }
            catch
            {
                return $"Expected:\n{expectedJson}\n\nActual:\n{actualJson}";
            }
        }
    }
}
