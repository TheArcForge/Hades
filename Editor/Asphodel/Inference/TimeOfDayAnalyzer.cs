// Editor/Asphodel/Inference/TimeOfDayAnalyzer.cs
using System;
using System.Collections.Generic;
using System.Linq;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Asphodel.Inference
{
    public class TimeOfDayAnalyzer : IPatternAnalyzer
    {
        const float PeakThresholdMultiplier = 1.5f; // hour must be 1.5x above mean to be "peak"

        public string Name => "time_of_day";

        public bool IsEnabled(InferenceConfig config) => config.TimeOfDayEnabled;

        public List<InferredPattern> Analyze(
            List<TraceRecord> traces, List<SpanRecord> spans, DateTimeOffset since)
        {
            var sinceMs = since.ToUnixTimeMilliseconds();
            var filtered = traces.Where(t => t.StartTime >= sinceMs).ToList();
            if (filtered.Count < 20) return new List<InferredPattern>();

            // Bucket by hour-of-day
            var hourCounts = new int[24];
            foreach (var trace in filtered)
            {
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(trace.StartTime);
                hourCounts[dt.Hour]++;
            }

            float mean = (float)filtered.Count / 24;
            if (mean < 1) return new List<InferredPattern>();

            // Find contiguous peak windows above threshold
            var peakHours = new List<int>();
            for (int h = 0; h < 24; h++)
            {
                if (hourCounts[h] >= mean * PeakThresholdMultiplier)
                    peakHours.Add(h);
            }

            if (peakHours.Count == 0) return new List<InferredPattern>();

            // Find contiguous ranges
            var ranges = new List<(int start, int end)>();
            int rangeStart = peakHours[0];
            int prev = peakHours[0];
            for (int i = 1; i < peakHours.Count; i++)
            {
                if (peakHours[i] == prev + 1)
                {
                    prev = peakHours[i];
                }
                else
                {
                    ranges.Add((rangeStart, prev));
                    rangeStart = peakHours[i];
                    prev = peakHours[i];
                }
            }
            ranges.Add((rangeStart, prev));

            // Take the largest range
            var bestRange = ranges.OrderByDescending(r => r.end - r.start).First();
            int peakTraces = 0;
            for (int h = bestRange.start; h <= bestRange.end; h++)
                peakTraces += hourCounts[h];
            float peakPct = (float)peakTraces / filtered.Count;

            var description = $"Primary work window: {bestRange.start:D2}:00-{bestRange.end + 1:D2}:00 ({peakPct * 100:F0}% of traces)";
            var patternKey = $"peak_{bestRange.start}_{bestRange.end}";

            return new List<InferredPattern>
            {
                new InferredPattern
                {
                    Id = InferredPattern.ComputeId(Name, patternKey),
                    AnalyzerName = Name,
                    PatternKey = patternKey,
                    Description = description,
                    TargetFile = "intent",
                    Confidence = peakPct,
                    SampleSize = filtered.Count,
                    FirstObserved = DateTimeOffset.FromUnixTimeMilliseconds(filtered.Min(t => t.StartTime)),
                    LastConfirmed = DateTimeOffset.FromUnixTimeMilliseconds(filtered.Max(t => t.StartTime)),
                    PromotionStatus = PromotionStatus.Pending
                }
            };
        }
    }
}
