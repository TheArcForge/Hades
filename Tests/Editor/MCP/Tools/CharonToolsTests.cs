// Tests/Editor/MCP/Tools/CharonToolsTests.cs
using System;
using System.IO;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using ArcForge.Hades.Editor.Charon;
using ArcForge.Hades.Editor.MCP;

namespace ArcForge.Hades.Editor.Tests.MCP.Tools
{
    public class CharonToolsTests
    {
        string _testDbPath;
        CharonDatabase _db;
        MCPDispatcher _dispatcher;

        [SetUp]
        public void SetUp()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"charon_tools_test_{Guid.NewGuid()}.db");
            _db = new CharonDatabase(_testDbPath);
            CharonEmitter.Initialize(_db);
            _dispatcher = new MCPDispatcher();
        }

        [TearDown]
        public void TearDown()
        {
            CharonEmitter.Shutdown();
            _db?.Dispose();
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
            if (File.Exists(_testDbPath + "-wal")) File.Delete(_testDbPath + "-wal");
            if (File.Exists(_testDbPath + "-shm")) File.Delete(_testDbPath + "-shm");
        }

        [Test]
        public void CharonStatus_ToolExists()
        {
            var result = _dispatcher.CallTool("hades_charon_status", new JObject());
            Assert.IsFalse(result.IsError);
        }

        [Test]
        public void CharonStatus_ReturnsEnabledState()
        {
            var result = _dispatcher.CallTool("hades_charon_status", new JObject());
            Assert.IsTrue(result.Text.Contains("enabled"));
        }
    }
}
