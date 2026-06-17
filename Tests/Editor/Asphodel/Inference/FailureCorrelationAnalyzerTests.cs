// Tests/Editor/Asphodel/Inference/FailureCorrelationAnalyzerTests.cs
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ArcForge.Hades.Editor.Asphodel.Inference;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Tests.Asphodel.Inference
{
    public class FailureCorrelationAnalyzerTests
    {
        FailureCorrelationAnalyzer _analyzer;

        [SetUp]
        public void SetUp()
        {
            _analyzer = new FailureCorrelationAnalyzer();
        }

        [Test]
        public void Name_IsFailureCorrelation()
        {
            Assert.AreEqual("failure_correlation", _analyzer.Name);
        }

        [Test]
        public void IsEnabled_RespectsConfig()
        {
            var config = new InferenceConfig { FailureCorrelationEnabled = false };
            Assert.IsFalse(_analyzer.IsEnabled(config));
        }

        [Test]
        public void Analyze_IdentifiesOverRepresentedAttribute()
        {
            var (traces, spans) = SyntheticTraceFixtures.FailureCorrelationFixture();
            var since = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

            var results = _analyzer.Analyze(traces, spans, since);

            Assert.IsTrue(results.Count >= 1, "Should detect at least one correlation");
            var prefabPattern = results.FirstOrDefault(p =>
                p.Description.ToLower().Contains("prefab_variant"));
            Assert.IsNotNull(prefabPattern, "Should detect prefab_variant correlation");
            Assert.AreEqual("pitfalls", prefabPattern.TargetFile);
            Assert.Greater(prefabPattern.Confidence, 0.5f);
        }

        [Test]
        public void Analyze_EmptyTraces_ReturnsEmpty()
        {
            var (traces, spans) = SyntheticTraceFixtures.EmptyFixture();
            var results = _analyzer.Analyze(traces, spans, DateTimeOffset.UtcNow.AddDays(-90));
            Assert.AreEqual(0, results.Count);
        }

        [Test]
        public void Analyze_NoFailures_ReturnsEmpty()
        {
            var traces = new List<TraceRecord>();
            var spans = new List<SpanRecord>();
            var baseTime = new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);

            for (int i = 0; i < 20; i++)
            {
                var traceId = TraceIdGenerator.NewTraceId();
                var spanId = TraceIdGenerator.NewSpanId();
                var t = baseTime.AddMinutes(i * 10);
                traces.Add(new TraceRecord
                {
                    TraceId = traceId,
                    RootSpanName = "mcp.tool.test",
                    StartTime = t.ToUnixTimeMilliseconds(),
                    EndTime = t.AddMilliseconds(100).ToUnixTimeMilliseconds(),
                    Status = SpanStatus.Ok,
                    SpanCount = 1
                });
                var span = new SpanRecord
                {
                    SpanId = spanId,
                    TraceId = traceId,
                    Name = "mcp.tool.test",
                    Kind = SpanKind.Server,
                    StartTime = t.ToUnixTimeMilliseconds(),
                    EndTime = t.AddMilliseconds(100).ToUnixTimeMilliseconds(),
                    Status = SpanStatus.Ok
                };
                span.Attributes[SpanAttributes.ToolName] = "test";
                span.Attributes["asset_type"] = "script";
                spans.Add(span);
            }

            var results = _analyzer.Analyze(traces, spans,
                new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
            Assert.AreEqual(0, results.Count);
        }

        [Test]
        public void Analyze_EvenlyDistributedErrors_NoCorrelation()
        {
            var traces = new List<TraceRecord>();
            var spans = new List<SpanRecord>();
            var baseTime = new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);
            var types = new[] { "script", "prefab", "scene", "material" };

            // 40 ok, 40 error — evenly distributed across types
            for (int i = 0; i < 80; i++)
            {
                var traceId = TraceIdGenerator.NewTraceId();
                var spanId = TraceIdGenerator.NewSpanId();
                var t = baseTime.AddMinutes(i * 10);
                var isError = i >= 40;
                traces.Add(new TraceRecord
                {
                    TraceId = traceId,
                    RootSpanName = "mcp.tool.test",
                    StartTime = t.ToUnixTimeMilliseconds(),
                    EndTime = t.AddMilliseconds(100).ToUnixTimeMilliseconds(),
                    Status = isError ? SpanStatus.Error : SpanStatus.Ok,
                    SpanCount = 1
                });
                var span = new SpanRecord
                {
                    SpanId = spanId,
                    TraceId = traceId,
                    Name = "mcp.tool.test",
                    Kind = SpanKind.Server,
                    StartTime = t.ToUnixTimeMilliseconds(),
                    EndTime = t.AddMilliseconds(100).ToUnixTimeMilliseconds(),
                    Status = isError ? SpanStatus.Error : SpanStatus.Ok
                };
                span.Attributes[SpanAttributes.ToolName] = "test";
                span.Attributes["asset_type"] = types[i % 4]; // evenly distributed
                spans.Add(span);
            }

            var results = _analyzer.Analyze(traces, spans,
                new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
            Assert.AreEqual(0, results.Count, "Evenly distributed errors should not produce correlations");
        }
    }
}
