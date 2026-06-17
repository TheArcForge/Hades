// Tests/Editor/Charon/SizeEnforcementTests.cs
using System;
using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Tests.Charon
{
    /// <summary>
    /// Phase B (felt-performance): PruneToTraceCap caps the trace table by ROW COUNT with no
    /// synchronous VACUUM — replacing the size-based EnforceSizeLimit that froze editor startup
    /// on a large traces.db.
    /// </summary>
    public class SizeEnforcementTests
    {
        string _testDbPath;
        CharonDatabase _db;

        [SetUp]
        public void SetUp()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"charon_cap_{Guid.NewGuid()}.db");
            _db = new CharonDatabase(_testDbPath);
        }

        [TearDown]
        public void TearDown()
        {
            _db?.Dispose();
            foreach (var e in new[] { "", "-wal", "-shm" })
                if (File.Exists(_testDbPath + e)) File.Delete(_testDbPath + e);
        }

        static TraceRecord Trace(int i) => new TraceRecord
        {
            TraceId = $"t{i:D3}",
            RootSpanName = "x",
            StartTime = i,           // ascending start_time → t000 oldest, t099 newest
            Status = SpanStatus.Ok,
            SpanCount = 1
        };

        [Test]
        public void PruneToTraceCap_KeepsNewestN_DeletesOldest()
        {
            for (int i = 0; i < 100; i++) _db.InsertTrace(Trace(i));

            var deleted = _db.PruneToTraceCap(40);

            Assert.AreEqual(60, deleted, "100 traces capped at 40 → 60 deleted");
            Assert.AreEqual(40, _db.ListTraces(1000).Count, "40 newest traces remain");
            Assert.IsNotNull(_db.GetTrace("t099"), "newest trace kept");
            Assert.IsNull(_db.GetTrace("t000"), "oldest trace deleted");
        }

        [Test]
        public void PruneToTraceCap_UnderCap_DeletesNothing()
        {
            for (int i = 0; i < 10; i++) _db.InsertTrace(Trace(i));

            Assert.AreEqual(0, _db.PruneToTraceCap(40), "fewer rows than the cap → no deletion");
            Assert.AreEqual(10, _db.ListTraces(1000).Count);
        }

        [Test]
        public void PruneToTraceCap_NonPositiveCap_NoOp()
        {
            _db.InsertTrace(Trace(0));
            Assert.AreEqual(0, _db.PruneToTraceCap(0));
            Assert.AreEqual(0, _db.PruneToTraceCap(-5));
            Assert.AreEqual(1, _db.ListTraces(1000).Count);
        }
    }
}
