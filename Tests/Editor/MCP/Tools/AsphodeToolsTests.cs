// Tests/Editor/MCP/Tools/AsphodeToolsTests.cs
using System;
using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Asphodel;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;
using ArcForge.Hades.Editor.MCP.Tools;
using Newtonsoft.Json.Linq;

namespace ArcForge.Hades.Editor.Tests.MCP.Tools
{
    public class AsphodeToolsTests
    {
        string _testDbPath;
        string _testMemDir;
        GraphDatabase _db;
        GraphDatabase _savedInstance;

        [SetUp]
        public void SetUp()
        {
            _savedInstance = GraphDatabase.Instance;
            _testDbPath = Path.Combine(Path.GetTempPath(), $"hades_asph_test_{Guid.NewGuid()}.db");
            _db = new GraphDatabase(_testDbPath);

            _testMemDir = Path.Combine(Path.GetTempPath(), $"hades_asph_mem_{Guid.NewGuid()}");
            var manager = new MemoryManager(_testMemDir);
            manager.EnsureDirectory();
            manager.EnsureDefaults();
            manager.EnsureProposalsDirectory();
            AsphodeTools.SetTestManager(manager);
            AsphodeTools.SetTestValidator(new MemoryValidator(manager, _db));
        }

        [TearDown]
        public void TearDown()
        {
            AsphodeTools.ClearTestOverrides();
            _db?.Dispose();
            GraphDatabase.RestoreInstanceForTests(_savedInstance);
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
            if (File.Exists(_testDbPath + "-wal")) File.Delete(_testDbPath + "-wal");
            if (File.Exists(_testDbPath + "-shm")) File.Delete(_testDbPath + "-shm");
            if (Directory.Exists(_testMemDir)) Directory.Delete(_testMemDir, true);
        }

        [Test]
        public void GetMemorySummary_ReturnsAllFiles()
        {
            var result = AsphodeTools.GetMemorySummary();
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            Assert.IsNotNull(obj["result"]);
            Assert.IsNotNull(obj["result"]["files"]);
            Assert.AreEqual(6, ((JArray)obj["result"]["files"]).Count);
        }

        [Test]
        public void RecallMemory_MatchesContent()
        {
            var result = AsphodeTools.RecallMemory("decisions");
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            Assert.IsNotNull(obj["result"]);
        }

        [Test]
        public void ProposeMemoryUpdate_CreatesProposal()
        {
            var result = AsphodeTools.ProposeMemoryUpdate("patterns", "### New Pattern\n\nObject pooling.", "Observed pattern");
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            Assert.AreEqual("created", obj["result"]["status"].ToString());
        }

        [Test]
        public void ValidateMemory_ReturnsResults()
        {
            var result = AsphodeTools.ValidateMemoryTool("");
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            Assert.IsNotNull(obj["result"]["results"]);
        }

        [Test]
        public void ValidateMemory_SingleFile_ReturnsResult()
        {
            var result = AsphodeTools.ValidateMemoryTool("decisions");
            Assert.IsFalse(result.IsError);

            var obj = JObject.Parse(result.Text);
            Assert.AreEqual("decisions", obj["result"]["filename"].ToString());
        }
    }
}
