// Tests/Editor/Charon/CharonDatabaseTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Tests.Charon
{
    public class CharonDatabaseTests
    {
        string _testDbPath;
        CharonDatabase _db;

        [SetUp]
        public void SetUp()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"charon_test_{Guid.NewGuid()}.db");
            _db = new CharonDatabase(_testDbPath);
        }

        [TearDown]
        public void TearDown()
        {
            _db?.Dispose();
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
            if (File.Exists(_testDbPath + "-wal")) File.Delete(_testDbPath + "-wal");
            if (File.Exists(_testDbPath + "-shm")) File.Delete(_testDbPath + "-shm");
        }

        [Test]
        public void Constructor_CreatesDatabase()
        {
            Assert.IsTrue(File.Exists(_testDbPath));
        }

        [Test]
        public void Schema_TracesTableExists()
        {
            Assert.IsTrue(_db.TableExists("traces"));
        }

        [Test]
        public void Schema_SpansTableExists()
        {
            Assert.IsTrue(_db.TableExists("spans"));
        }

        [Test]
        public void InsertTrace_Succeeds()
        {
            var trace = new TraceRecord
            {
                TraceId = TraceIdGenerator.NewTraceId(),
                RootSpanName = "mcp.tool.hades_ping",
                StartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Status = SpanStatus.Ok,
                SpanCount = 1
            };

            Assert.DoesNotThrow(() => _db.InsertTrace(trace));
        }

        [Test]
        public void InsertSpan_Succeeds()
        {
            var traceId = TraceIdGenerator.NewTraceId();

            _db.InsertTrace(new TraceRecord
            {
                TraceId = traceId,
                RootSpanName = "mcp.tool.test",
                StartTime = 1000,
                Status = SpanStatus.Ok,
                SpanCount = 1
            });

            var span = new SpanRecord
            {
                SpanId = TraceIdGenerator.NewSpanId(),
                TraceId = traceId,
                Name = "mcp.tool.test",
                Kind = SpanKind.Server,
                StartTime = 1000,
                EndTime = 2000,
                Status = SpanStatus.Ok
            };

            Assert.DoesNotThrow(() => _db.InsertSpan(span));
        }

        [Test]
        public void InsertSpans_BatchInsert()
        {
            var traceId = TraceIdGenerator.NewTraceId();

            _db.InsertTrace(new TraceRecord
            {
                TraceId = traceId,
                RootSpanName = "mcp.tool.test",
                StartTime = 1000,
                Status = SpanStatus.Ok,
                SpanCount = 3
            });

            var spans = new List<SpanRecord>();
            for (int i = 0; i < 3; i++)
            {
                spans.Add(new SpanRecord
                {
                    SpanId = TraceIdGenerator.NewSpanId(),
                    TraceId = traceId,
                    Name = $"span_{i}",
                    Kind = SpanKind.Internal,
                    StartTime = 1000 + i * 100,
                    EndTime = 1000 + (i + 1) * 100,
                    Status = SpanStatus.Ok
                });
            }

            Assert.DoesNotThrow(() => _db.InsertSpans(spans));
        }

        [Test]
        public void GetTrace_ReturnsInsertedTrace()
        {
            var traceId = TraceIdGenerator.NewTraceId();
            _db.InsertTrace(new TraceRecord
            {
                TraceId = traceId,
                RootSpanName = "mcp.tool.test",
                StartTime = 1000,
                EndTime = 2000,
                Status = SpanStatus.Ok,
                SpanCount = 1
            });

            var result = _db.GetTrace(traceId);
            Assert.IsNotNull(result);
            Assert.AreEqual("mcp.tool.test", result.RootSpanName);
            Assert.AreEqual(1000, result.StartTime);
            Assert.AreEqual(2000, result.EndTime);
        }

        [Test]
        public void GetTrace_ReturnsNullForMissing()
        {
            var result = _db.GetTrace("nonexistent");
            Assert.IsNull(result);
        }

        [Test]
        public void GetSpansByTraceId_ReturnsSpans()
        {
            var traceId = TraceIdGenerator.NewTraceId();
            _db.InsertTrace(new TraceRecord
            {
                TraceId = traceId,
                RootSpanName = "test",
                StartTime = 1000,
                Status = SpanStatus.Ok,
                SpanCount = 2
            });

            _db.InsertSpan(new SpanRecord
            {
                SpanId = TraceIdGenerator.NewSpanId(),
                TraceId = traceId,
                Name = "root",
                Kind = SpanKind.Server,
                StartTime = 1000,
                EndTime = 2000,
                Status = SpanStatus.Ok
            });
            _db.InsertSpan(new SpanRecord
            {
                SpanId = TraceIdGenerator.NewSpanId(),
                TraceId = traceId,
                Name = "child",
                Kind = SpanKind.Internal,
                StartTime = 1100,
                EndTime = 1500,
                Status = SpanStatus.Ok
            });

            var spans = _db.GetSpansByTraceId(traceId);
            Assert.AreEqual(2, spans.Count);
            Assert.AreEqual("root", spans[0].Name);
            Assert.AreEqual("child", spans[1].Name);
        }

        [Test]
        public void ListTraces_ReturnsInReverseChronologicalOrder()
        {
            for (int i = 0; i < 3; i++)
            {
                _db.InsertTrace(new TraceRecord
                {
                    TraceId = TraceIdGenerator.NewTraceId(),
                    RootSpanName = $"trace_{i}",
                    StartTime = 1000 + i * 1000,
                    Status = SpanStatus.Ok,
                    SpanCount = 1
                });
            }

            var traces = _db.ListTraces(10);
            Assert.AreEqual(3, traces.Count);
            Assert.AreEqual("trace_2", traces[0].RootSpanName);
            Assert.AreEqual("trace_0", traces[2].RootSpanName);
        }

        [Test]
        public void ListTraces_RespectsLimit()
        {
            for (int i = 0; i < 5; i++)
            {
                _db.InsertTrace(new TraceRecord
                {
                    TraceId = TraceIdGenerator.NewTraceId(),
                    RootSpanName = $"trace_{i}",
                    StartTime = 1000 + i * 1000,
                    Status = SpanStatus.Ok,
                    SpanCount = 1
                });
            }

            var traces = _db.ListTraces(2);
            Assert.AreEqual(2, traces.Count);
        }

        [Test]
        public void PruneOlderThan_RemovesOldTraces()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var oldTime = now - (31L * 24 * 60 * 60 * 1000);
            var recentTime = now - (1L * 24 * 60 * 60 * 1000);

            _db.InsertTrace(new TraceRecord
            {
                TraceId = TraceIdGenerator.NewTraceId(),
                RootSpanName = "old_trace",
                StartTime = oldTime,
                Status = SpanStatus.Ok,
                SpanCount = 1
            });
            _db.InsertTrace(new TraceRecord
            {
                TraceId = TraceIdGenerator.NewTraceId(),
                RootSpanName = "recent_trace",
                StartTime = recentTime,
                Status = SpanStatus.Ok,
                SpanCount = 1
            });

            var pruned = _db.PruneOlderThan(30);
            Assert.AreEqual(1, pruned);

            var remaining = _db.ListTraces(10);
            Assert.AreEqual(1, remaining.Count);
            Assert.AreEqual("recent_trace", remaining[0].RootSpanName);
        }

        [Test]
        public void UpdateTraceEnd_UpdatesFields()
        {
            var traceId = TraceIdGenerator.NewTraceId();
            _db.InsertTrace(new TraceRecord
            {
                TraceId = traceId,
                RootSpanName = "test",
                StartTime = 1000,
                Status = SpanStatus.Unset,
                SpanCount = 0
            });

            _db.UpdateTraceEnd(traceId, 2000, SpanStatus.Ok, 3);

            var trace = _db.GetTrace(traceId);
            Assert.AreEqual(2000, trace.EndTime);
            Assert.AreEqual(SpanStatus.Ok, trace.Status);
            Assert.AreEqual(3, trace.SpanCount);
            Assert.AreEqual(1000, trace.TotalDurationMs);
        }
    }
}
