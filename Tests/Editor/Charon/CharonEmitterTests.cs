// Tests/Editor/Charon/CharonEmitterTests.cs
using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Tests.Charon
{
    public class CharonEmitterTests
    {
        string _testDbPath;
        CharonDatabase _db;

        [SetUp]
        public void SetUp()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"charon_emitter_test_{Guid.NewGuid()}.db");
            _db = new CharonDatabase(_testDbPath);
            CharonEmitter.Initialize(_db);
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
        public void StartSpan_ReturnsSpan()
        {
            using (var span = CharonEmitter.StartSpan("test.op", SpanKind.Server))
            {
                Assert.IsNotNull(span);
                Assert.AreEqual("test.op", span.Name);
                Assert.AreEqual(SpanKind.Server, span.Kind);
            }
        }

        [Test]
        public void StartSpan_GeneratesTraceId()
        {
            using (var span = CharonEmitter.StartSpan("test.op", SpanKind.Server))
            {
                Assert.IsTrue(TraceIdGenerator.IsValidTraceId(span.TraceId));
            }
        }

        [Test]
        public void StartSpan_ChildInheritsTraceId()
        {
            using (var parent = CharonEmitter.StartSpan("parent", SpanKind.Server))
            {
                using (var child = CharonEmitter.StartSpan("child", SpanKind.Internal))
                {
                    Assert.AreEqual(parent.TraceId, child.TraceId);
                    Assert.AreEqual(parent.SpanId, child.ParentSpanId);
                }
            }
        }

        [Test]
        public void StartSpan_NestedChildrenChainCorrectly()
        {
            using (var root = CharonEmitter.StartSpan("root", SpanKind.Server))
            {
                using (var mid = CharonEmitter.StartSpan("mid", SpanKind.Internal))
                {
                    using (var leaf = CharonEmitter.StartSpan("leaf", SpanKind.Internal))
                    {
                        Assert.AreEqual(root.TraceId, leaf.TraceId);
                        Assert.AreEqual(mid.SpanId, leaf.ParentSpanId);
                    }
                }
            }
        }

        [Test]
        public void StartSpan_WithExplicitTraceId_UsesIt()
        {
            var explicitId = "aaaabbbbccccddddaaaabbbbccccdddd";
            using (var span = CharonEmitter.StartSpan("test.op", SpanKind.Server, explicitId))
            {
                Assert.AreEqual(explicitId, span.TraceId);
            }
        }

        [Test]
        public void Flush_WritesBufferedSpansToDatabase()
        {
            using (var span = CharonEmitter.StartSpan("test.flush", SpanKind.Server))
            {
                span.SetAttribute("tool.name", "test");
            }

            CharonEmitter.Flush();

            var traces = _db.ListTraces(10);
            Assert.AreEqual(1, traces.Count);
            Assert.AreEqual("test.flush", traces[0].RootSpanName);

            var spans = _db.GetSpansByTraceId(traces[0].TraceId);
            Assert.AreEqual(1, spans.Count);
        }

        [Test]
        public void Flush_WritesNestedSpans()
        {
            using (var root = CharonEmitter.StartSpan("root", SpanKind.Server))
            {
                using (var child = CharonEmitter.StartSpan("child", SpanKind.Internal))
                {
                    child.SetAttribute("query.kind", "test");
                }
            }

            CharonEmitter.Flush();

            var traces = _db.ListTraces(10);
            Assert.AreEqual(1, traces.Count);
            Assert.AreEqual(2, traces[0].SpanCount);

            var spans = _db.GetSpansByTraceId(traces[0].TraceId);
            Assert.AreEqual(2, spans.Count);
        }

        [Test]
        public void Flush_UpdatesTraceEndTime()
        {
            using (var span = CharonEmitter.StartSpan("test", SpanKind.Server))
            {
                Thread.Sleep(10);
            }

            CharonEmitter.Flush();

            var traces = _db.ListTraces(10);
            Assert.IsTrue(traces[0].EndTime.HasValue);
            Assert.IsTrue(traces[0].TotalDurationMs > 0);
        }

        [Test]
        public void Dispose_RestoresParentContext()
        {
            using (var parent = CharonEmitter.StartSpan("parent", SpanKind.Server))
            {
                using (var child = CharonEmitter.StartSpan("child", SpanKind.Internal))
                {
                }
                using (var sibling = CharonEmitter.StartSpan("sibling", SpanKind.Internal))
                {
                    Assert.AreEqual(parent.SpanId, sibling.ParentSpanId);
                }
            }
        }

        [Test]
        public void BufferCount_TracksUnflushed()
        {
            Assert.AreEqual(0, CharonEmitter.BufferCount);

            using (var span = CharonEmitter.StartSpan("test", SpanKind.Server))
            {
            }

            Assert.IsTrue(CharonEmitter.BufferCount > 0);

            CharonEmitter.Flush();
            Assert.AreEqual(0, CharonEmitter.BufferCount);
        }

        [Test]
        public void IsEnabled_FalseBeforeInitialize()
        {
            CharonEmitter.Shutdown();
            Assert.IsFalse(CharonEmitter.IsEnabled);
        }

        [Test]
        public void StartSpan_WhenDisabled_ReturnsNoOpSpan()
        {
            CharonEmitter.Shutdown();

            using (var span = CharonEmitter.StartSpan("test", SpanKind.Server))
            {
                Assert.IsNotNull(span);
                span.SetAttribute("key", "value");
            }
        }
    }
}
