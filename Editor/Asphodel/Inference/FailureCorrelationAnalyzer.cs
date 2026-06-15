// Editor/Asphodel/Inference/FailureCorrelationAnalyzer.cs
using System;
using System.Collections.Generic;
using System.Linq;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Asphodel.Inference
{
    public class FailureCorrelationAnalyzer : IPatternAnalyzer
    {
        const float OverRepresentationThreshold = 2.0f; // 2x baseline = significant
        static readonly HashSet<string> IgnoredAttributes = new HashSet<string>
        {
            SpanAttributes.ToolName, SpanAttributes.ToolInput, "error.message", "error.type"
        };

        public string Name => "failure_correlation";

        public bool IsEnabled(InferenceConfig config) => config.FailureCorrelationEnabled;

        public List<InferredPattern> Analyze(
            List<TraceRecord> traces, List<SpanRecord> spans, DateTimeOffset since)
        {
            var sinceMs = since.ToUnixTimeMilliseconds();
            var filtered = traces.Where(t => t.StartTime >= sinceMs).ToList();
            if (filtered.Count == 0) return new List<InferredPattern>();

            var errorTraces = filtered.Where(t => t.Status == SpanStatus.Error).ToList();
            var okTraces = filtered.Where(t => t.Status == SpanStatus.Ok).ToList();
            if (errorTraces.Count < 3 || okTraces.Count == 0) return new List<InferredPattern>();

            var spansByTrace = spans
                .Where(s => s.StartTime >= sinceMs)
                .GroupBy(s => s.TraceId)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Count attribute value occurrences in error vs ok traces
            var errorAttrCounts = CountAttributes(errorTraces, spansByTrace);
            var okAttrCounts = CountAttributes(okTraces, spansByTrace);

            var results = new List<InferredPattern>();

            foreach (var kvp in errorAttrCounts)
            {
                var attrKey = kvp.Key; // "asset_type=prefab_variant"
                int errorCount = kvp.Value;
                float errorRate = (float)errorCount / errorTraces.Count;

                int okCount = okAttrCounts.ContainsKey(attrKey) ? okAttrCounts[attrKey] : 0;
                float baselineRate = okTraces.Count > 0 ? (float)okCount / okTraces.Count : 0f;

                if (baselineRate < 0.01f) baselineRate = 0.01f; // avoid division by near-zero

                float ratio = errorRate / baselineRate;

                if (ratio >= OverRepresentationThreshold && errorCount >= 3)
                {
                    var parts = attrKey.Split(new[] { '=' }, 2);
                    var attrName = parts.Length > 0 ? parts[0] : attrKey;
                    var attrValue = parts.Length > 1 ? parts[1] : "";

                    var description = $"Failures correlate with {attrName}={attrValue} ({ratio:F1}x higher error rate than baseline, {errorCount} of {errorTraces.Count} errors)";
                    var patternKey = attrKey;

                    results.Add(new InferredPattern
                    {
                        Id = InferredPattern.ComputeId(Name, patternKey),
                        AnalyzerName = Name,
                        PatternKey = patternKey,
                        Description = description,
                        TargetFile = "pitfalls",
                        Confidence = Math.Min(1.0f, errorRate),
                        SampleSize = errorTraces.Count,
                        FirstObserved = DateTimeOffset.FromUnixTimeMilliseconds(
                            errorTraces.Min(t => t.StartTime)),
                        LastConfirmed = DateTimeOffset.FromUnixTimeMilliseconds(
                            errorTraces.Max(t => t.StartTime)),
                        PromotionStatus = PromotionStatus.Pending
                    });
                }
            }

            return results;
        }

        Dictionary<string, int> CountAttributes(
            List<TraceRecord> traces,
            Dictionary<string, List<SpanRecord>> spansByTrace)
        {
            var counts = new Dictionary<string, int>();

            foreach (var trace in traces)
            {
                if (!spansByTrace.TryGetValue(trace.TraceId, out var traceSpans)) continue;

                var seenAttrs = new HashSet<string>();
                foreach (var span in traceSpans)
                {
                    foreach (var attr in span.Attributes)
                    {
                        if (IgnoredAttributes.Contains(attr.Key)) continue;
                        var key = $"{attr.Key}={attr.Value}";
                        if (seenAttrs.Add(key))
                        {
                            if (!counts.ContainsKey(key)) counts[key] = 0;
                            counts[key]++;
                        }
                    }
                }
            }

            return counts;
        }
    }
}
