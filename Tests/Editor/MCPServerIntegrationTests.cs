using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using ArcForge.Hades.Editor.MCP;
using ArcForge.Hades.Editor.Core;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.Tests
{
    public class MCPServerIntegrationTests
    {
        MCPServer _server;

        [SetUp]
        public void SetUp()
        {
            _server = new MCPServer();
        }

        [TearDown]
        public void TearDown()
        {
            _server?.Dispose();
        }

        [Test]
        public void Start_SetsIsRunningTrue()
        {
            _server.Start(new HadesSettings());
            Assert.IsTrue(_server.IsRunning);
        }

        [Test]
        public void Stop_SetsIsRunningFalse()
        {
            _server.Start(new HadesSettings());
            _server.Stop();
            Assert.IsFalse(_server.IsRunning);
        }

        [Test]
        public async Task EndToEnd_Ping_ReturnsHadesIsAlive()
        {
            _server.Start(new HadesSettings());
            await Task.Delay(100);

            using var client = new HttpClient();
            var request = @"{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/call"",""params"":{""name"":""hades_ping"",""arguments"":{}}}";
            var content = new StringContent(request, Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"http://127.0.0.1:{_server.Port}/rpc", content);

            Assert.AreEqual(200, (int)response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            var obj = JObject.Parse(body);
            var text = obj["result"]["content"][0]["text"].ToString();

            StringAssert.Contains("Hades is alive", text);
        }

        [Test]
        public async Task EndToEnd_Initialize_ReturnsServerInfo()
        {
            _server.Start(new HadesSettings());
            await Task.Delay(100);

            using var client = new HttpClient();
            var request = @"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize"",""params"":{}}";
            var content = new StringContent(request, Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"http://127.0.0.1:{_server.Port}/rpc", content);

            var body = await response.Content.ReadAsStringAsync();
            var obj = JObject.Parse(body);

            Assert.AreEqual("hades", obj["result"]["serverInfo"]["name"].ToString());
        }
    }
}
