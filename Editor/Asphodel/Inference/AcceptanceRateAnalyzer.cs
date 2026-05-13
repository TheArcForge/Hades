// Editor/Asphodel/Inference/AcceptanceRateAnalyzer.cs
using System;
using System.Collections.Generic;
using System.Linq;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Asphodel.Inference
{
    public class AcceptanceRateAnalyzer : IPatternAnalyzer
    {
        const long SessionGapMs = 30 * 60 * 1000; // 30 minutes
        const int RetryLookahead = 3;
        const int MinOccurrences = 10;

        public string Name => "acceptance_rate";

        public bool IsEnabled(InferenceConfig config) => config.AcceptanceRateEnabled;

        public List<InferredPattern> Analyze(
            List<TraceRecord> traces, List<SpanRecord> spans, DateTimeOffset since)
        {
            var sinceMs = since.ToUnixTimeMilliseconds();
            var filtered = traces.Where(t => t.StartTime >= sinceMs).OrderBy(t => t.StartTime).ToList();
            if (filtered.Count == 0) return new List<InferredPattern>();

            var spansByTrace = spans
                .Where(s => s.StartTime >= sinceMs)
                .GroupBy(s => s.TraceId)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Build fingerprints: tool_name + sorted primary attribute keys
            var fingerprints = new Dictionary<string, List<int>>(); // fingerprint → trace indices
            for (int i = 0; i < filtered.Count; i++)
            {
                var trace = filtered[i];
                if (!spansByTrace.TryGetValue(trace.TraceId, out var traceSpans)) continue;
                foreach (var span in traceSpans)
                {
                    if (!span.Attributes.ContainsKey("tool_name")) continue;
                    var fp = BuildFingerprint(span);
                    if (!fingerprints.ContainsKey(fp))
                        fingerprints[fp] = new List<int>();
                    fingerprints[fp].Add(i);
                }
            }

            var results = new List<InferredPattern>();

            foreach (var kvp in fingerprints)
            {
                if (kvp.Value.Count < MinOccurrences) continue;

                int accepted = 0;
                int total = kvp.Value.Count;

                foreach (var idx in kvp.Value)
                {
                    if (IsAccepted(filtered, spansByTrace, idx, kvp.Key))
                        accepted++;
                }

                float confidence = (float)accepted / total;
                if (confidence < 0.5f) continue;

                var sampleSpan = spansByTrace[filtered[kvp.Value[0]].TraceId][0];
                var toolName = sampleSpan.Attributes.ContainsKey("tool_name")
                    ? sampleSpan.Attributes["tool_name"] : "unknown";
                var primaryAttr = sampleSpan.Attributes
                    .Where(a => a.Key != "tool_name")
                    .Select(a => $"{a.Key}={a.Value}")
                    .FirstOrDefault() ?? "";

                var description = string.IsNullOrEmpty(primaryAttr)
                    ? $"Recurring use of {toolName}"
                    : $"Recurring use of {toolName} with {primaryAttr}";

                var patternKey = kvp.Key;
                results.Add(new InferredPattern
                {
                    Id = InferredPattern.ComputeId(Name, patternKey),
                    AnalyzerName = Name,
                    PatternKey = patternKey,
                    Description = description,
                    TargetFile = "patterns",
                    Confidence = confidence,
                    SampleSize = total,
                    FirstObserved = DateTimeOffset.FromUnixTimeMilliseconds(
                        filtered[kvp.Value[0]].StartTime),
                    LastConfirmed = DateTimeOffset.FromUnixTimeMilliseconds(
                        filtered[kvp.Value[kvp.Value.Count - 1]].StartTime),
                    PromotionStatus = PromotionStatus.Pending
                });
            }

            return results;
        }

        string BuildFingerprint(SpanRecord span)
        {
            var toolName = span.Attributes.ContainsKey("tool_name")
                ? span.Attributes["tool_name"] : span.Name;
            var attrKeys = span.Attributes.Keys
                .Where(k => k != "tool_name")
                .OrderBy(k => k);
            return toolName + ":" + string.Join(",", attrKeys);
        }

        bool IsAccepted(List<TraceRecord> sortedTraces,
            Dictionary<string, List<SpanRecord>> spansByTrace,
            int traceIndex, string fingerprint)
        {
            var currentTrace = sortedTraces[traceIndex];

            // Check next RetryLookahead traces within the same session
            for (int offset = 1; offset <= RetryLookahead && traceIndex + offset < sortedTraces.Count; offset++)
            {
                var nextTrace = sortedTraces[traceIndex + offset];

                // Session boundary check
                if (nextTrace.StartTime - currentTrace.StartTime > SessionGapMs)
                    break;

                // Rebuild signal
                if (nextTrace.RootSpanName.Contains("rebuild_graph"))
                    return false;

                // Retry signal: same tool, different primary params
                if (!spansByTrace.TryGetValue(nextTrace.TraceId, out var nextSpans)) continue;
                foreach (var nextSpan in nextSpans)
                {
                    if (!nextSpan.Attributes.ContainsKey("tool_name")) continue;
                    var nextFp = BuildFingerprint(nextSpan);
                    if (nextFp == fingerprint)
                    {
                        // Same fingerprint keys but check if values differ
                        if (!spansByTrace.TryGetValue(currentTrace.TraceId, out var curSpans)) continue;
                        var curSpan = curSpans.FirstOrDefault(s => s.Attributes.ContainsKey("tool_name"));
                        if (curSpan != null && HaveDifferentValues(curSpan, nextSpan))
                            return false;
                    }
                }
            }

            return true;
        }

        bool HaveDifferentValues(SpanRecord a, SpanRecord b)
        {
            foreach (var key in a.Attributes.Keys.Where(k => k != "tool_name"))
            {
                if (b.Attributes.ContainsKey(key) && a.Attributes[key] != b.Attributes[key])
                    return true;
            }
            return false;
        }
    }
}
