using NUnit.Framework;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Tests.Asphodel.Inference
{
    public class SpanAttributesContractTests
    {
        // The emitter (MCPDispatcher.CallToolWithTracing) writes these literal keys into
        // traces.db; external consumers (the Charon dashboard) read them too. The inference
        // analyzers and synthetic fixtures now reference the same constants. This test pins
        // the literal values so a rename can't silently re-break the loop (#8) or the dashboard.
        [Test]
        public void ToolAttributeKeys_MatchEmittedLiterals()
        {
            Assert.AreEqual("tool.name", SpanAttributes.ToolName);
            Assert.AreEqual("tool.input", SpanAttributes.ToolInput);
        }
    }
}
