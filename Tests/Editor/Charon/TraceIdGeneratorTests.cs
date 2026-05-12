// Tests/Editor/Charon/TraceIdGeneratorTests.cs
using NUnit.Framework;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Tests.Charon
{
    public class TraceIdGeneratorTests
    {
        [Test]
        public void NewTraceId_Returns32HexChars()
        {
            var id = TraceIdGenerator.NewTraceId();
            Assert.AreEqual(32, id.Length);
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-f]{32}$"));
        }

        [Test]
        public void NewSpanId_Returns16HexChars()
        {
            var id = TraceIdGenerator.NewSpanId();
            Assert.AreEqual(16, id.Length);
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-f]{16}$"));
        }

        [Test]
        public void NewTraceId_IsUnique()
        {
            var ids = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < 1000; i++)
                ids.Add(TraceIdGenerator.NewTraceId());
            Assert.AreEqual(1000, ids.Count);
        }

        [Test]
        public void NewSpanId_IsUnique()
        {
            var ids = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < 1000; i++)
                ids.Add(TraceIdGenerator.NewSpanId());
            Assert.AreEqual(1000, ids.Count);
        }

        [Test]
        public void IsValidTraceId_AcceptsValid()
        {
            var id = TraceIdGenerator.NewTraceId();
            Assert.IsTrue(TraceIdGenerator.IsValidTraceId(id));
        }

        [Test]
        public void IsValidTraceId_RejectsInvalid()
        {
            Assert.IsFalse(TraceIdGenerator.IsValidTraceId(null));
            Assert.IsFalse(TraceIdGenerator.IsValidTraceId(""));
            Assert.IsFalse(TraceIdGenerator.IsValidTraceId("too-short"));
            Assert.IsFalse(TraceIdGenerator.IsValidTraceId("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ"));
        }
    }
}
