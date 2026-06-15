// Tests/Editor/Asphodel/Inference/AcceptanceRateAnalyzerTests.cs
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ArcForge.Hades.Editor.Asphodel.Inference;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Tests.Asphodel.Inference
{
    public class AcceptanceRateAnalyzerTests
    {
        AcceptanceRateAnalyzer _analyzer;

        [SetUp]
        public void SetUp()
        {
            _analyzer = new AcceptanceRateAnalyzer();
        }

        [Test]
        public void Name_IsAcceptanceRate()
        {
            Assert.AreEqual("acceptance_rate", _analyzer.Name);
        }

        [Test]
        public void IsEnabled_RespectsConfig()
        {
            var config = new InferenceConfig { AcceptanceRateEnabled = false };
            Assert.IsFalse(_analyzer.IsEnabled(config));

            config.AcceptanceRateEnabled = true;
            Assert.IsTrue(_analyzer.IsEnabled(config));
        }

        [Test]
        public void Analyze_ProducesPatternFromFixture()
        {
            var (traces, spans) = SyntheticTraceFixtures.AcceptanceRateFixture();
            var since = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

            var results = _analyzer.Analyze(traces, spans, since);

            Assert.IsTrue(results.Count >= 1, "Should detect at least one pattern");
            var objectPoolPattern = results.FirstOrDefault(p =>
                p.Description.Contains("find_prefabs_with_component") &&
                p.Description.Contains("ObjectPool"));
            Assert.IsNotNull(objectPoolPattern, "Should detect ObjectPool pattern");
            Assert.Greater(objectPoolPattern.Confidence, 0.8f);
            Assert.GreaterOrEqual(objectPoolPattern.SampleSize, 10);
            Assert.AreEqual("patterns", objectPoolPattern.TargetFile);
        }

        [Test]
        public void Analyze_EmptyTraces_ReturnsEmpty()
        {
            var (traces, spans) = SyntheticTraceFixtures.EmptyFixture();
            var results = _analyzer.Analyze(traces, spans, DateTimeOffset.UtcNow.AddDays(-90));
            Assert.AreEqual(0, results.Count);
        }

        [Test]
        public void Analyze_RespectsLookbackWindow()
        {
            var (traces, spans) = SyntheticTraceFixtures.AcceptanceRateFixture();
            // Set 'since' to far in the future so all traces are excluded
            var since = DateTimeOffset.UtcNow.AddDays(1);

            var results = _analyzer.Analyze(traces, spans, since);
            Assert.AreEqual(0, results.Count);
        }

        [Test]
        public void Analyze_InsufficientOccurrences_NoPattern()
        {
            // Only 3 traces with same fingerprint — below the 10-occurrence pre-filter
            var traces = new List<TraceRecord>();
            var spans = new List<SpanRecord>();
            var baseTime = new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);

            for (int i = 0; i < 3; i++)
            {
                var traceId = TraceIdGenerator.NewTraceId();
                var spanId = TraceIdGenerator.NewSpanId();
                var t = baseTime.AddMinutes(i * 15);
                traces.Add(new TraceRecord
                {
                    TraceId = traceId,
                    RootSpanName = "mcp.tool.find_orphan_scripts",
                    StartTime = t.ToUnixTimeMilliseconds(),
                    EndTime = t.AddMilliseconds(100).ToUnixTimeMilliseconds(),
                    Status = SpanStatus.Ok,
                    SpanCount = 1
                });
                var span = new SpanRecord
                {
                    SpanId = spanId,
                    TraceId = traceId,
                    Name = "mcp.tool.find_orphan_scripts",
                    Kind = SpanKind.Server,
                    StartTime = t.ToUnixTimeMilliseconds(),
                    EndTime = t.AddMilliseconds(100).ToUnixTimeMilliseconds(),
                    Status = SpanStatus.Ok
                };
                span.Attributes[SpanAttributes.ToolName] = "find_orphan_scripts";
                spans.Add(span);
            }

            var results = _analyzer.Analyze(traces, spans,
                new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
            Assert.AreEqual(0, results.Count);
        }
    }
}
