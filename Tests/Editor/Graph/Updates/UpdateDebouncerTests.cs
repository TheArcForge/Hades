using NUnit.Framework;
using ArcForge.Hades.Editor.Graph.Updates;
using System.Collections.Generic;

namespace ArcForge.Hades.Editor.Tests.Graph.Updates
{
    public class UpdateDebouncerTests
    {
        [Test]
        public void Enqueue_AccumulatesGuids()
        {
            var flushed = new List<string[]>();
            var debouncer = new UpdateDebouncer(guids => flushed.Add(guids));

            debouncer.Enqueue(new[] { "guid1", "guid2" });
            debouncer.Enqueue(new[] { "guid3" });

            Assert.AreEqual(0, flushed.Count, "Should not flush immediately");
            Assert.AreEqual(3, debouncer.PendingCount);
        }

        [Test]
        public void ForceFlush_FlushesAll()
        {
            var flushed = new List<string[]>();
            var debouncer = new UpdateDebouncer(guids => flushed.Add(guids));

            debouncer.Enqueue(new[] { "guid1", "guid2" });
            debouncer.ForceFlush();

            Assert.AreEqual(1, flushed.Count);
            Assert.AreEqual(2, flushed[0].Length);
            Assert.AreEqual(0, debouncer.PendingCount);
        }

        [Test]
        public void ForceFlush_Empty_DoesNothing()
        {
            var flushed = new List<string[]>();
            var debouncer = new UpdateDebouncer(guids => flushed.Add(guids));

            debouncer.ForceFlush();

            Assert.AreEqual(0, flushed.Count);
        }

        [Test]
        public void Enqueue_DeduplicatesGuids()
        {
            var flushed = new List<string[]>();
            var debouncer = new UpdateDebouncer(guids => flushed.Add(guids));

            debouncer.Enqueue(new[] { "guid1", "guid2" });
            debouncer.Enqueue(new[] { "guid1", "guid3" });
            debouncer.ForceFlush();

            Assert.AreEqual(1, flushed.Count);
            Assert.AreEqual(3, flushed[0].Length);
        }

        [Test]
        public void BatchCap_SplitsLargeBatches()
        {
            var flushed = new List<string[]>();
            var debouncer = new UpdateDebouncer(guids => flushed.Add(guids), batchCap: 5);

            var guids = new string[12];
            for (int i = 0; i < 12; i++) guids[i] = $"guid{i}";

            debouncer.Enqueue(guids);
            debouncer.ForceFlush();

            Assert.AreEqual(3, flushed.Count);
            Assert.AreEqual(5, flushed[0].Length);
            Assert.AreEqual(5, flushed[1].Length);
            Assert.AreEqual(2, flushed[2].Length);
        }
    }
}
