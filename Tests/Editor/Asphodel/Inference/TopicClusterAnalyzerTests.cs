// Tests/Editor/Asphodel/Inference/TopicClusterAnalyzerTests.cs
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ArcForge.Hades.Editor.Asphodel.Inference;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Tests.Asphodel.Inference
{
    public class TopicClusterAnalyzerTests
    {
        TopicClusterAnalyzer _analyzer;

        [SetUp]
        public void SetUp()
        {
            _analyzer = new TopicClusterAnalyzer();
        }

        [Test]
        public void Name_IsTopicCluster()
        {
            Assert.AreEqual("topic_cluster", _analyzer.Name);
        }

        [Test]
        public void IsEnabled_RespectsConfig()
        {
            var config = new InferenceConfig { TopicClusterEnabled = false };
            Assert.IsFalse(_analyzer.IsEnabled(config));
        }

        [Test]
        public void Analyze_IdentifiesDominantTopics()
        {
            var (traces, spans) = SyntheticTraceFixtures.TopicClusterFixture();
            var since = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

            var results = _analyzer.Analyze(traces, spans, since);

            Assert.IsTrue(results.Count >= 2, $"Should detect at least 2 topic clusters, got {results.Count}");
            var audioPattern = results.FirstOrDefault(p =>
                p.Description.ToLower().Contains("audio"));
            Assert.IsNotNull(audioPattern, "Should detect audio topic cluster");
            Assert.AreEqual("intent", audioPattern.TargetFile);
        }

        [Test]
        public void Analyze_FiltersStopWords()
        {
            var (traces, spans) = SyntheticTraceFixtures.TopicClusterFixture();
            var since = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

            var results = _analyzer.Analyze(traces, spans, since);

            // Generic terms like "search", "find", "name" should not appear as topics
            foreach (var r in results)
            {
                Assert.IsFalse(r.Description.ToLower().Contains("frequent topic: search"),
                    "Stop words should be filtered");
                Assert.IsFalse(r.Description.ToLower().Contains("frequent topic: find"),
                    "Stop words should be filtered");
            }
        }

        [Test]
        public void Analyze_EmptyTraces_ReturnsEmpty()
        {
            var (traces, spans) = SyntheticTraceFixtures.EmptyFixture();
            var results = _analyzer.Analyze(traces, spans, DateTimeOffset.UtcNow.AddDays(-90));
            Assert.AreEqual(0, results.Count);
        }

        [Test]
        public void Analyze_UniformDistribution_NoCluster()
        {
            // All unique terms — no cluster should form above 15% threshold
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
                    RootSpanName = $"mcp.tool.search_by_name",
                    StartTime = t.ToUnixTimeMilliseconds(),
                    EndTime = t.AddMilliseconds(100).ToUnixTimeMilliseconds(),
                    Status = SpanStatus.Ok,
                    SpanCount = 1
                });
                var span = new SpanRecord
                {
                    SpanId = spanId,
                    TraceId = traceId,
                    Name = $"mcp.tool.search_by_name",
                    Kind = SpanKind.Server,
                    StartTime = t.ToUnixTimeMilliseconds(),
                    EndTime = t.AddMilliseconds(100).ToUnixTimeMilliseconds(),
                    Status = SpanStatus.Ok
                };
                span.Attributes["tool_name"] = "search_by_name";
                var uniqueTerms = new[] { "Xylophone", "Quasar", "Fibonacci", "Parallax", "Zeppelin",
                    "Origami", "Kaleidoscope", "Nebula", "Labyrinth", "Chrysalis",
                    "Vortex", "Enigma", "Pendulum", "Silhouette", "Talisman",
                    "Albatross", "Mirage", "Zenith", "Catalyst", "Rhapsody" };
                span.Attributes["query"] = uniqueTerms[i];
                spans.Add(span);
            }

            var results = _analyzer.Analyze(traces, spans,
                new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
            Assert.AreEqual(0, results.Count);
        }
    }
}
