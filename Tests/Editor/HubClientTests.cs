using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.MCP;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.Tests
{
    public class HubClientTests
    {
        string _testDir;

        [SetUp]
        public void SetUp()
        {
            _testDir = Path.Combine(Path.GetTempPath(),
                "hades_hubclient_test_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }

        [Test]
        public void ReadHubInfo_ReturnsNull_WhenFileDoesNotExist()
        {
            var result = HubClient.ReadHubInfo(Path.Combine(_testDir, "hub.json"));
            Assert.IsNull(result);
        }

        [Test]
        public void ReadHubInfo_ParsesValidFile()
        {
            var filePath = Path.Combine(_testDir, "hub.json");
            File.WriteAllText(filePath, @"{""port"":12345,""pid"":9876,""startedAt"":1700000000}");

            var result = HubClient.ReadHubInfo(filePath);
            Assert.IsNotNull(result);
            Assert.AreEqual(12345, result.Port);
            Assert.AreEqual(9876, result.Pid);
        }

        [Test]
        public void ReadHubInfo_ReturnsNull_OnInvalidJson()
        {
            var filePath = Path.Combine(_testDir, "hub.json");
            File.WriteAllText(filePath, "not json");

            var result = HubClient.ReadHubInfo(filePath);
            Assert.IsNull(result);
        }

        [Test]
        public void WriteBreadcrumb_CreatesFile()
        {
            var pendingDir = Path.Combine(_testDir, "pending");
            HubClient.WriteBreadcrumb(pendingDir, "TestProject", "/path/to/test", 12345, 9876);

            Assert.IsTrue(Directory.Exists(pendingDir));
            var files = Directory.GetFiles(pendingDir, "*.json");
            Assert.AreEqual(1, files.Length);

            var content = JObject.Parse(File.ReadAllText(files[0]));
            Assert.AreEqual("TestProject", content["projectName"].ToString());
            Assert.AreEqual("/path/to/test", content["projectPath"].ToString());
            Assert.AreEqual(12345, content["port"].Value<int>());
        }

        [Test]
        public void DetectHubChange_ReturnsHubInfo_WhenPortChanges()
        {
            HubClient.ResetLastKnownHub();
            HubClient.UpdateLastKnownHub(11111, 9876);

            var filePath = Path.Combine(_testDir, "hub.json");
            File.WriteAllText(filePath, @"{""port"":12345,""pid"":9876}");

            var result = HubClient.DetectHubChange(filePath);
            Assert.IsNotNull(result);
            Assert.AreEqual(12345, result.Port);
        }

        [Test]
        public void DetectHubChange_ReturnsHubInfo_WhenPidChanges()
        {
            HubClient.ResetLastKnownHub();
            HubClient.UpdateLastKnownHub(12345, 9876);

            var filePath = Path.Combine(_testDir, "hub.json");
            File.WriteAllText(filePath, @"{""port"":12345,""pid"":1111}");

            var result = HubClient.DetectHubChange(filePath);
            Assert.IsNotNull(result);
            Assert.AreEqual(1111, result.Pid);
        }

        [Test]
        public void DetectHubChange_ReturnsNull_WhenSame()
        {
            HubClient.ResetLastKnownHub();
            HubClient.UpdateLastKnownHub(12345, 9876);

            var filePath = Path.Combine(_testDir, "hub.json");
            File.WriteAllText(filePath, @"{""port"":12345,""pid"":9876}");

            var result = HubClient.DetectHubChange(filePath);
            Assert.IsNull(result);
        }
    }
}
