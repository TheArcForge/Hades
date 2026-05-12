// Tests/Editor/Charon/CharonInstrumentationTests.cs
using System;
using System.IO;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using ArcForge.Hades.Editor.Charon;
using ArcForge.Hades.Editor.MCP;

namespace ArcForge.Hades.Editor.Tests.Charon
{
    public class CharonInstrumentationTests
    {
        string _testDbPath;
        CharonDatabase _db;
        MCPDispatcher _dispatcher;

        [SetUp]
        public void SetUp()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"charon_instr_test_{Guid.NewGuid()}.db");
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
        public void CallTool_EmitsRootSpan()
        {
            _dispatcher.CallToolWithTracing("hades_ping", new JObject());
            CharonEmitter.Flush();

            var traces = _db.ListTraces(10);
            Assert.AreEqual(1, traces.Count);
            Assert.AreEqual("mcp.tool.hades_ping", traces[0].RootSpanName);

            var spans = _db.GetSpansByTraceId(traces[0].TraceId);
            Assert.IsTrue(spans.Count >= 1);
            Assert.AreEqual("mcp.tool.hades_ping", spans[0].Name);
        }

        [Test]
        public void CallTool_SpanHasToolNameAttribute()
        {
            _dispatcher.CallToolWithTracing("hades_ping", new JObject());
            CharonEmitter.Flush();

            var traces = _db.ListTraces(10);
            var spans = _db.GetSpansByTraceId(traces[0].TraceId);
            Assert.IsTrue(spans[0].Attributes.ContainsKey("tool.name"));
            Assert.AreEqual("hades_ping", spans[0].Attributes["tool.name"]);
        }

        [Test]
        public void CallTool_SpanRecordsStatus()
        {
            _dispatcher.CallToolWithTracing("hades_ping", new JObject());
            CharonEmitter.Flush();

            var traces = _db.ListTraces(10);
            Assert.AreEqual(SpanStatus.Ok, traces[0].Status);
        }

        [Test]
        public void CallTool_ErrorSetsErrorStatus()
        {
            _dispatcher.CallToolWithTracing("nonexistent_tool", new JObject());
            CharonEmitter.Flush();

            var traces = _db.ListTraces(10);
            var spans = _db.GetSpansByTraceId(traces[0].TraceId);

            Assert.AreEqual(SpanStatus.Error, spans[0].Status);
        }

        [Test]
        public void CallTool_WithExplicitTraceId_UsesIt()
        {
            var explicitTraceId = "aaaabbbbccccddddeeeeffffaaaabbbb";
            _dispatcher.CallToolWithTracing("hades_ping", new JObject(), explicitTraceId);
            CharonEmitter.Flush();

            var trace = _db.GetTrace(explicitTraceId);
            Assert.IsNotNull(trace);
        }
    }
}
