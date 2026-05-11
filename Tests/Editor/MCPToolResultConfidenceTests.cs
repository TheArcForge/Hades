using NUnit.Framework;
using ArcForge.Hades.Editor.MCP;
using ArcForge.Hades.Editor.Graph.Models;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.Tests
{
    public class MCPToolResultConfidenceTests
    {
        [Test]
        public void SuccessWithConfidence_IncludesConfidenceInResponse()
        {
            var confidence = ConfidenceBlock.High()
                .WithFactor("graph_freshness", "current");

            var result = MCPToolResult.SuccessWithConfidence(
                new { items = new[] { "a", "b" } },
                confidence);

            Assert.IsFalse(result.IsError);
            var text = result.Text;
            Assert.IsTrue(text.Contains("confidence"));
            Assert.IsTrue(text.Contains("high"));
            Assert.IsTrue(text.Contains("graph_freshness"));
        }

        [Test]
        public void SuccessWithConfidence_ParseableJson()
        {
            var confidence = ConfidenceBlock.Medium("partial")
                .WithFactor("graph_freshness", "rebuilding");

            var result = MCPToolResult.SuccessWithConfidence(
                new { count = 3 },
                confidence);

            var obj = JObject.Parse(result.Text);
            Assert.IsNotNull(obj["result"]);
            Assert.IsNotNull(obj["confidence"]);
            Assert.AreEqual("medium", obj["confidence"]["level"].ToString());
            Assert.AreEqual("partial", obj["confidence"]["result_status"].ToString());
        }
    }
}
