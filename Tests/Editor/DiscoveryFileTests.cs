using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Core;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.Tests
{
    public class DiscoveryFileTests
    {
        string _testDir;

        [SetUp]
        public void SetUp()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "hades_test_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }

        [Test]
        public void Write_CreatesValidJson()
        {
            var filePath = Path.Combine(_testDir, "server.json");
            DiscoveryFile.Write(filePath, 7780, System.Diagnostics.Process.GetCurrentProcess().Id);

            Assert.IsTrue(File.Exists(filePath));
            var content = File.ReadAllText(filePath);
            var obj = JObject.Parse(content);

            Assert.AreEqual(7780, obj["port"].Value<int>());
            Assert.AreEqual("http://127.0.0.1:7780/rpc", obj["endpoint"].ToString());
            Assert.IsNotNull(obj["pid"]);
        }

        [Test]
        public void Read_ValidFile_ReturnsData()
        {
            var filePath = Path.Combine(_testDir, "server.json");
            DiscoveryFile.Write(filePath, 7785, 12345);

            var data = DiscoveryFile.Read(filePath);

            Assert.AreEqual(7785, data.Port);
            Assert.AreEqual("http://127.0.0.1:7785/rpc", data.Endpoint);
            Assert.AreEqual(12345, data.Pid);
        }

        [Test]
        public void Read_MissingFile_ReturnsNull()
        {
            var data = DiscoveryFile.Read(Path.Combine(_testDir, "nonexistent.json"));
            Assert.IsNull(data);
        }

        [Test]
        public void Delete_RemovesFile()
        {
            var filePath = Path.Combine(_testDir, "server.json");
            File.WriteAllText(filePath, "{}");

            DiscoveryFile.Delete(filePath);

            Assert.IsFalse(File.Exists(filePath));
        }
    }
}
