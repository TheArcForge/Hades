using System.Linq;
using NUnit.Framework;
using ArcForge.Hades.Editor.MCP;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.Tests
{
    public class MCPDispatcherTests
    {
        MCPDispatcher _dispatcher;

        [SetUp]
        public void SetUp()
        {
            _dispatcher = new MCPDispatcher();
        }

        [Test]
        public void HandleRequest_Initialize_ReturnsProtocolVersion()
        {
            var request = @"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize"",""params"":{}}";
            var response = _dispatcher.HandleRequest(request);
            var obj = JObject.Parse(response);

            Assert.AreEqual("2.0", obj["jsonrpc"].ToString());
            Assert.AreEqual(1, obj["id"].Value<int>());
            Assert.AreEqual("2024-11-05", obj["result"]["protocolVersion"].ToString());
            Assert.AreEqual("hades", obj["result"]["serverInfo"]["name"].ToString());
        }

        [Test]
        public void HandleRequest_ToolsList_ReturnsToolArray()
        {
            var request = @"{""jsonrpc"":""2.0"",""id"":2,""method"":""tools/list"",""params"":{}}";
            var response = _dispatcher.HandleRequest(request);
            var obj = JObject.Parse(response);

            Assert.IsNotNull(obj["result"]["tools"]);
            Assert.IsInstanceOf<JArray>(obj["result"]["tools"]);
        }

        [Test]
        public void HandleRequest_InvalidJson_ReturnsParseError()
        {
            var response = _dispatcher.HandleRequest("not json{{{");
            var obj = JObject.Parse(response);

            Assert.AreEqual(-32700, obj["error"]["code"].Value<int>());
        }

        [Test]
        public void HandleRequest_UnknownMethod_ReturnsMethodNotFound()
        {
            var request = @"{""jsonrpc"":""2.0"",""id"":3,""method"":""unknown/method"",""params"":{}}";
            var response = _dispatcher.HandleRequest(request);
            var obj = JObject.Parse(response);

            Assert.AreEqual(-32601, obj["error"]["code"].Value<int>());
        }

        [Test]
        public void HandleRequest_ToolsCall_UnknownTool_ReturnsError()
        {
            var request = @"{""jsonrpc"":""2.0"",""id"":4,""method"":""tools/call"",""params"":{""name"":""nonexistent"",""arguments"":{}}}";
            var response = _dispatcher.HandleRequest(request);
            var obj = JObject.Parse(response);

            Assert.IsTrue(obj["result"]["isError"].Value<bool>());
        }

        [Test]
        public void HandleRequest_Notification_ReturnsNull()
        {
            var request = @"{""jsonrpc"":""2.0"",""method"":""notifications/initialized"",""params"":{}}";
            var response = _dispatcher.HandleRequest(request);

            Assert.IsNull(response);
        }

        [Test]
        public void DiscoverTools_FindsHadesPing()
        {
            var tools = _dispatcher.GetTools();
            Assert.IsTrue(tools.Any(t => t.Name == "hades_ping"),
                "hades_ping tool should be discovered");
        }

        [Test]
        public void HandleRequest_ToolsCall_HadesPing_ReturnsAlive()
        {
            var request = @"{""jsonrpc"":""2.0"",""id"":5,""method"":""tools/call"",""params"":{""name"":""hades_ping"",""arguments"":{}}}";
            var response = _dispatcher.HandleRequest(request);
            var obj = JObject.Parse(response);

            var text = obj["result"]["content"][0]["text"].ToString();
            StringAssert.Contains("Hades is alive", text);
        }
    }
}
