// Editor/MCP/Tools/RegressionTools.cs
using System.Collections.Generic;
using ArcForge.Hades.Editor.Charon;
using ArcForge.Hades.Editor.Graph.Models;
using ArcForge.Hades.Editor.MCP;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.MCP.Tools
{
    public static class RegressionTools
    {
        [MCPTool("hades_regression_record",
            "Record a set of tool call snapshots as a regression dataset. Each snapshot captures a tool name, input, and expected output.")]
        public static MCPToolResult RegressionRecord(
            [MCPToolParam("Name for this regression dataset", required: true)] string name,
            [MCPToolParam("JSON array of {tool_name, input, expected_output} objects", required: true)] string tool_calls)
        {
            var db = CharonEmitter.Database;
            if (db == null)
                return MCPToolResult.Error("Charon is not initialized");

            JArray calls;
            try
            {
                calls = JArray.Parse(tool_calls);
            }
            catch
            {
                return MCPToolResult.Error("tool_calls must be a valid JSON array");
            }

            var snapshots = new List<ToolCallSnapshot>();
            foreach (var call in calls)
            {
                var toolName = call["tool_name"]?.ToString();
                var input = call["input"]?.ToString() ?? "{}";
                var expectedOutput = call["expected_output"]?.ToString();

                if (string.IsNullOrEmpty(toolName) || string.IsNullOrEmpty(expectedOutput))
                    return MCPToolResult.Error("Each tool_call must have tool_name and expected_output");

                snapshots.Add(new ToolCallSnapshot(toolName, input, expectedOutput));
            }

            var runner = new RegressionRunner(db);
            var datasetId = runner.Record(name, snapshots);

            return MCPToolResult.SuccessWithConfidence(
                new { dataset_id = datasetId, member_count = snapshots.Count },
                ConfidenceBlock.High());
        }

        [MCPTool("hades_regression_replay",
            "Replay a regression dataset: re-run each recorded tool call and compare output to the snapshot. Returns pass/fail report.")]
        public static MCPToolResult RegressionReplay(
            [MCPToolParam("ID of the dataset to replay", required: true)] string dataset_id)
        {
            var db = CharonEmitter.Database;
            if (db == null)
                return MCPToolResult.Error("Charon is not initialized");

            var dispatcher = new MCPDispatcher();
            var runner = new RegressionRunner(db);
            var report = runner.Replay(dataset_id, dispatcher);

            if (report == null)
                return MCPToolResult.Error($"Dataset not found: {dataset_id}");

            var resultArray = new JArray();
            foreach (var r in report.Results)
            {
                var item = new JObject
                {
                    ["tool_name"] = r.ToolName,
                    ["passed"] = r.Passed
                };
                if (!r.Passed && r.Diff != null)
                    item["diff"] = r.Diff;
                resultArray.Add(item);
            }

            var result = new JObject
            {
                ["dataset_id"] = report.DatasetId,
                ["dataset_name"] = report.DatasetName,
                ["total"] = report.Total,
                ["passed"] = report.Passed,
                ["failed"] = report.Failed,
                ["results"] = resultArray
            };

            var confidence = report.Failed == 0 ? ConfidenceBlock.High() : ConfidenceBlock.Medium();
            return MCPToolResult.SuccessWithConfidence(result, confidence);
        }
    }
}
