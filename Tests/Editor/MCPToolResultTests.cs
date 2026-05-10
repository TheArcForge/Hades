using NUnit.Framework;
using ArcForge.Hades.Editor.MCP;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.Tests
{
    public class MCPToolResultTests
    {
        [Test]
        public void Success_WithString_ReturnsTextDirectly()
        {
            var result = MCPToolResult.Success("hello");
            Assert.IsFalse(result.IsError);
            Assert.AreEqual("hello", result.Text);
        }

        [Test]
        public void Success_WithObject_SerializesAsJson()
        {
            var result = MCPToolResult.Success(new { name = "test", count = 42 });
            Assert.IsFalse(result.IsError);
            var parsed = JObject.Parse(result.Text);
            Assert.AreEqual("test", parsed["name"].ToString());
            Assert.AreEqual(42, parsed["count"].Value<int>());
        }

        [Test]
        public void Error_SetsIsErrorTrue()
        {
            var result = MCPToolResult.Error("something failed");
            Assert.IsTrue(result.IsError);
            Assert.AreEqual("something failed", result.Text);
        }

        [Test]
        public void ToMCPResponse_Success_HasContentArray()
        {
            var result = MCPToolResult.Success("data");
            var response = result.ToMCPResponse();
            Assert.IsNotNull(response["content"]);
            Assert.AreEqual("text", response["content"][0]["type"].ToString());
            Assert.AreEqual("data", response["content"][0]["text"].ToString());
            Assert.IsNull(response["isError"]);
        }

        [Test]
        public void ToMCPResponse_Error_HasIsErrorTrue()
        {
            var result = MCPToolResult.Error("bad");
            var response = result.ToMCPResponse();
            Assert.IsTrue(response["isError"].Value<bool>());
            Assert.AreEqual("bad", response["content"][0]["text"].ToString());
        }

        [Test]
        public void Success_NullContent_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() => MCPToolResult.Success(null));
        }

        [Test]
        public void Error_EmptyMessage_Throws()
        {
            Assert.Throws<System.ArgumentException>(() => MCPToolResult.Error(""));
        }
    }
}
