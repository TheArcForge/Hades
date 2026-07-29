using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Core;

namespace ArcForge.Hades.Editor.Tests
{
    public class HadesConfigTests
    {
        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "hades_config_test_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        void WriteConfig(string contents)
            => File.WriteAllText(Path.Combine(_dir, HadesConfig.FileName), contents);

        [Test]
        public void Exists_IsFalse_WhenFileMissing()
        {
            var config = HadesConfig.Load(_dir);
            Assert.IsFalse(config.Exists);
        }

        [Test]
        public void Getters_ReturnFallbacks_WhenFileMissing()
        {
            var config = HadesConfig.Load(_dir);
            Assert.AreEqual("local", config.GetString("hub_scope", "local"));
            Assert.AreEqual(true, config.GetBool("mcp_enabled", true));
            Assert.AreEqual(120, config.GetInt("reload_timeout_seconds", 120));
        }

        [Test]
        public void Getters_ReturnFallbacks_WhenKeyAbsent()
        {
            WriteConfig("mcp_port: 51234\n");
            var config = HadesConfig.Load(_dir);
            Assert.AreEqual("local", config.GetString("hub_scope", "local"));
        }

        [Test]
        public void Getters_ReadStoredValues()
        {
            WriteConfig("hub_scope: global\nmcp_port: 51234\ncharon_enabled: false\n");
            var config = HadesConfig.Load(_dir);
            Assert.AreEqual("global", config.GetString("hub_scope", "local"));
            Assert.AreEqual(51234, config.GetInt("mcp_port", 0));
            Assert.AreEqual(false, config.GetBool("charon_enabled", true));
        }

        [Test]
        public void Getters_ReturnFallbacks_OnUnparseableValues()
        {
            WriteConfig("mcp_port: banana\ncharon_enabled: maybe\n");
            var config = HadesConfig.Load(_dir);
            Assert.AreEqual(7, config.GetInt("mcp_port", 7));
            Assert.AreEqual(true, config.GetBool("charon_enabled", true));
        }

        [Test]
        public void Parse_SkipsBlankAndCommentLines()
        {
            var values = HadesConfig.Parse(new[]
            {
                "# hub_scope: global",
                "",
                "   ",
                "mcp_port: 42"
            });
            Assert.AreEqual(1, values.Count);
            Assert.AreEqual("42", values["mcp_port"]);
        }

        [Test]
        public void Parse_IgnoresLinesWithoutAColon()
        {
            var values = HadesConfig.Parse(new[] { "garbage", "mcp_port: 42" });
            Assert.AreEqual(1, values.Count);
        }

        [Test]
        public void GetBool_IsCaseInsensitive()
        {
            WriteConfig("mcp_enabled: FALSE\n");
            var config = HadesConfig.Load(_dir);
            Assert.AreEqual(false, config.GetBool("mcp_enabled", true));
        }

        [Test]
        public void Save_ThenLoad_RoundTripsAllTypes()
        {
            var config = HadesConfig.Load(_dir);
            config.Set("hub_scope", "global");
            config.Set("mcp_enabled", false);
            config.Set("mcp_port", 51234);
            config.Save();

            var reloaded = HadesConfig.Load(_dir);
            Assert.IsTrue(reloaded.Exists);
            Assert.AreEqual("global", reloaded.GetString("hub_scope", "local"));
            Assert.AreEqual(false, reloaded.GetBool("mcp_enabled", true));
            Assert.AreEqual(51234, reloaded.GetInt("mcp_port", 0));
        }

        [Test]
        public void Save_CreatesTheDirectory_WhenMissing()
        {
            var nested = Path.Combine(_dir, "nested", ".arcforge");
            var config = HadesConfig.Load(nested);
            config.Set("hub_scope", "local");
            config.Save();
            Assert.IsTrue(File.Exists(Path.Combine(nested, HadesConfig.FileName)));
        }

        [Test]
        public void Save_PreservesUnknownKeys()
        {
            WriteConfig("some_future_key: keepme\n");
            var config = HadesConfig.Load(_dir);
            config.Set("mcp_port", 1);
            config.Save();

            var reloaded = HadesConfig.Load(_dir);
            Assert.AreEqual("keepme", reloaded.GetString("some_future_key", ""));
        }
    }
}
