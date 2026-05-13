// Editor/Asphodel/Inference/TopicClusterAnalyzer.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Asphodel.Inference
{
    public class TopicClusterAnalyzer : IPatternAnalyzer
    {
        const float MinTopicFrequency = 0.15f; // 15% of traces

        static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "get", "find", "search", "query", "project", "scene", "name", "by",
            "mcp", "tool", "hades", "with", "using", "the", "all", "list",
            "set", "to", "from", "for", "of", "in", "on", "at", "is", "are",
            "type", "filter", "pattern", "result", "status", "count", "path",
            "asset", "node", "edge", "graph", "trace", "span"
        };

        public string Name => "topic_cluster";

        public bool IsEnabled(InferenceConfig config) => config.TopicClusterEnabled;

        public List<InferredPattern> Analyze(
            List<TraceRecord> traces, List<SpanRecord> spans, DateTimeOffset since)
        {
            var sinceMs = since.ToUnixTimeMilliseconds();
            var filtered = traces.Where(t => t.StartTime >= sinceMs).ToList();
            if (filtered.Count == 0) return new List<InferredPattern>();

            var spansByTrace = spans
                .Where(s => s.StartTime >= sinceMs)
                .GroupBy(s => s.TraceId)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Extract keywords per trace
            var traceKeywords = new Dictionary<string, HashSet<string>>();
            foreach (var trace in filtered)
            {
                var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Tokenize root span name
                foreach (var token in Tokenize(trace.RootSpanName))
                    if (!StopWords.Contains(token) && token.Length > 2)
                        keywords.Add(token.ToLower());

                // Tokenize span attributes
                if (spansByTrace.TryGetValue(trace.TraceId, out var traceSpans))
                {
                    foreach (var span in traceSpans)
                    {
                        foreach (var attr in span.Attributes)
                        {
                            if (attr.Key == "tool_name") continue;
                            foreach (var token in Tokenize(attr.Value))
                                if (!StopWords.Contains(token) && token.Length > 2)
                                    keywords.Add(token.ToLower());
                        }
                    }
                }

                traceKeywords[trace.TraceId] = keywords;
            }

            // Count keyword frequency across traces
            var keywordCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in traceKeywords)
            {
                foreach (var kw in kvp.Value)
                {
                    if (!keywordCounts.ContainsKey(kw))
                        keywordCounts[kw] = 0;
                    keywordCounts[kw]++;
                }
            }

            int totalTraces = filtered.Count;
            var results = new List<InferredPattern>();

            // Find keywords above frequency threshold
            var significantKeywords = keywordCounts
                .Where(kvp => (float)kvp.Value / totalTraces >= MinTopicFrequency)
                .OrderByDescending(kvp => kvp.Value)
                .ToList();

            // Group co-occurring significant keywords
            var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var primary in significantKeywords)
            {
                if (consumed.Contains(primary.Key)) continue;

                var cluster = new List<string> { primary.Key };
                consumed.Add(primary.Key);

                // Find co-occurring keywords
                foreach (var candidate in significantKeywords)
                {
                    if (consumed.Contains(candidate.Key)) continue;

                    int cooccurrence = traceKeywords.Values
                        .Count(kws => kws.Contains(primary.Key) && kws.Contains(candidate.Key));
                    if ((float)cooccurrence / primary.Value >= 0.5f)
                    {
                        cluster.Add(candidate.Key);
                        consumed.Add(candidate.Key);
                    }
                }

                float frequency = (float)primary.Value / totalTraces;
                var clusterLabel = string.Join(", ", cluster.Take(3));
                var patternKey = string.Join("+", cluster.OrderBy(c => c));

                results.Add(new InferredPattern
                {
                    Id = InferredPattern.ComputeId(Name, patternKey),
                    AnalyzerName = Name,
                    PatternKey = patternKey,
                    Description = $"Frequent topic: {clusterLabel} (appeared in {primary.Value} of {totalTraces} traces)",
                    TargetFile = "intent",
                    Confidence = frequency,
                    SampleSize = primary.Value,
                    FirstObserved = DateTimeOffset.FromUnixTimeMilliseconds(filtered.Min(t => t.StartTime)),
                    LastConfirmed = DateTimeOffset.FromUnixTimeMilliseconds(filtered.Max(t => t.StartTime)),
                    PromotionStatus = PromotionStatus.Pending
                });
            }

            return results;
        }

        static List<string> Tokenize(string input)
        {
            if (string.IsNullOrEmpty(input)) return new List<string>();

            // Split on dots, underscores, hyphens, spaces
            var parts = Regex.Split(input, @"[._\-\s/]+");

            // Further split camelCase
            var tokens = new List<string>();
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                var camelSplit = Regex.Split(part, @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])");
                tokens.AddRange(camelSplit.Where(s => !string.IsNullOrEmpty(s)));
            }

            return tokens;
        }
    }
}
