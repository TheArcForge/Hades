// Tests/Editor/Asphodel/Inference/TimeOfDayAnalyzerTests.cs
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ArcForge.Hades.Editor.Asphodel.Inference;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Tests.Asphodel.Inference
{
    public class TimeOfDayAnalyzerTests
    {
        TimeOfDayAnalyzer _analyzer;

        [SetUp]
        public void SetUp()
        {
            _analyzer = new TimeOfDayAnalyzer();
        }

        [Test]
        public void Name_IsTimeOfDay()
        {
            Assert.AreEqual("time_of_day", _analyzer.Name);
        }

        [Test]
        public void IsEnabled_RespectsConfig()
        {
            var config = new InferenceConfig { TimeOfDayEnabled = false };
            Assert.IsFalse(_analyzer.IsEnabled(config));
        }

        [Test]
        public void Analyze_DetectsWeekdayPeak()
        {
            var (traces, spans) = SyntheticTraceFixtures.TimeOfDayFixture();
            var since = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

            var results = _analyzer.Analyze(traces, spans, since);

            Assert.IsTrue(results.Count >= 1, "Should detect at least one time pattern");
            var pattern = results[0];
            Assert.IsTrue(pattern.Description.Contains("09") || pattern.Description.Contains("9"),
                $"Should detect 09:xx as part of work window, got: {pattern.Description}");
            // Should not promote to Tier 1 — metadata only
            Assert.AreEqual("intent", pattern.TargetFile);
        }

        [Test]
        public void Analyze_EmptyTraces_ReturnsEmpty()
        {
            var (traces, spans) = SyntheticTraceFixtures.EmptyFixture();
            var results = _analyzer.Analyze(traces, spans, DateTimeOffset.UtcNow.AddDays(-90));
            Assert.AreEqual(0, results.Count);
        }

        [Test]
        public void Analyze_UniformDistribution_NoPeak()
        {
            // Traces evenly spread across all 24 hours
            var traces = new List<TraceRecord>();
            var spans = new List<SpanRecord>();
            var baseDate = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

            for (int day = 0; day < 10; day++)
            {
                for (int hour = 0; hour < 24; hour++)
                {
                    var t = baseDate.AddDays(day).AddHours(hour);
                    var traceId = TraceIdGenerator.NewTraceId();
                    traces.Add(new TraceRecord
                    {
                        TraceId = traceId,
                        RootSpanName = "mcp.tool.test",
                        StartTime = t.ToUnixTimeMilliseconds(),
                        EndTime = t.AddMilliseconds(100).ToUnixTimeMilliseconds(),
                        Status = SpanStatus.Ok,
                        SpanCount = 1
                    });
                }
            }

            var results = _analyzer.Analyze(traces, spans,
                new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
            Assert.AreEqual(0, results.Count, "Uniform distribution should produce no peak");
        }
    }
}
