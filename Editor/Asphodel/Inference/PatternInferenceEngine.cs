// Editor/Asphodel/Inference/PatternInferenceEngine.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Asphodel.Inference
{
    public class PatternInferenceEngine
    {
        readonly MemoryManager _memManager;
        readonly CharonDatabase _charonDb;
        readonly InferenceConfig _config;
        readonly List<IPatternAnalyzer> _analyzers;
        readonly PromotionEvaluator _promotionEvaluator;

        DateTimeOffset _lastRunTime = DateTimeOffset.MinValue;

        public PatternInferenceEngine(
            MemoryManager memManager,
            CharonDatabase charonDb,
            InferenceConfig config)
        {
            _memManager = memManager;
            _charonDb = charonDb;
            _config = config;

            _analyzers = new List<IPatternAnalyzer>
            {
                new AcceptanceRateAnalyzer(),
                new TopicClusterAnalyzer(),
                new TimeOfDayAnalyzer(),
                new FailureCorrelationAnalyzer()
            };

            _promotionEvaluator = new PromotionEvaluator(memManager, config);
        }

        public string InferredDir => Path.Combine(_memManager.MemoryDir, "inferred");

        public void RunInference()
        {
            if (!_config.Enabled) return;

            using (var span = CharonEmitter.IsEnabled
                ? CharonEmitter.StartSpan("asphodel.inference.run", SpanKind.Internal)
                : null)
            {
                try
                {
                    var since = DateTimeOffset.UtcNow.AddDays(-_config.MaxTraceLookbackDays);
                    if (_lastRunTime > since) since = _lastRunTime;

                    var sinceMs = since.ToUnixTimeMilliseconds();

                    // Load traces and spans from Charon
                    var traces = _charonDb.ListTraces(10000);
                    traces = traces.Where(t => t.StartTime >= sinceMs).ToList();

                    if (traces.Count == 0)
                    {
                        _lastRunTime = DateTimeOffset.UtcNow;
                        return;
                    }

                    var allSpans = new List<SpanRecord>();
                    foreach (var trace in traces)
                    {
                        var traceSpans = _charonDb.GetSpansByTraceId(trace.TraceId);
                        allSpans.AddRange(traceSpans);
                    }

                    // Run each enabled analyzer
                    var newPatterns = new List<InferredPattern>();
                    foreach (var analyzer in _analyzers)
                    {
                        if (!analyzer.IsEnabled(_config)) continue;

                        var results = analyzer.Analyze(traces, allSpans, since);
                        newPatterns.AddRange(results);

                        span?.SetAttribute($"analyzer.{analyzer.Name}.count", results.Count.ToString());
                    }

                    // Load existing inferred patterns
                    var existing = LoadExistingPatterns();

                    // Merge
                    var merged = MergePatterns(existing, newPatterns);

                    // Detect conflicts with Tier 1
                    DetectConflicts(merged);

                    // Write updated inferred files
                    WriteInferredFiles(merged);

                    // Evaluate promotions
                    _promotionEvaluator.Evaluate(merged);

                    // Re-write after promotion status changes
                    WriteInferredFiles(merged);

                    _lastRunTime = DateTimeOffset.UtcNow;

                    span?.SetAttribute("patterns.total", merged.Count.ToString());
                    span?.SetAttribute("patterns.new",
                        (merged.Count - existing.Count).ToString());
                }
                catch (Exception ex)
                {
                    span?.SetStatus(SpanStatus.Error);
                    span?.SetAttribute("error.message", ex.Message);
                    UnityEngine.Debug.LogWarning($"[Hades] Inference run failed: {ex}");
                }
            }
        }

        List<InferredPattern> LoadExistingPatterns()
        {
            var patterns = new List<InferredPattern>();
            if (!Directory.Exists(InferredDir)) return patterns;

            foreach (var filePath in Directory.GetFiles(InferredDir, "*.md"))
            {
                var content = File.ReadAllText(filePath);
                var memFile = FrontmatterParser.Parse(content);
                memFile.Filename = Path.GetFileName(filePath);
                memFile.FilePath = filePath;
                var pattern = InferredPattern.FromMemoryFile(memFile);
                if (pattern != null) patterns.Add(pattern);
            }

            return patterns;
        }

        List<InferredPattern> MergePatterns(
            List<InferredPattern> existing, List<InferredPattern> incoming)
        {
            var merged = new Dictionary<string, InferredPattern>();

            // Start with existing
            foreach (var p in existing)
                merged[p.Id] = p;

            // Merge incoming
            foreach (var p in incoming)
            {
                if (merged.TryGetValue(p.Id, out var existingPattern))
                {
                    // Update mutable fields, preserve stable ones
                    existingPattern.Confidence = p.Confidence;
                    existingPattern.SampleSize = p.SampleSize;
                    existingPattern.LastConfirmed = p.LastConfirmed;
                    existingPattern.Description = p.Description;
                    // Preserve: FirstObserved, PromotionStatus, ConflictsWith
                }
                else
                {
                    merged[p.Id] = p;
                }
            }

            return merged.Values.ToList();
        }

        void DetectConflicts(List<InferredPattern> patterns)
        {
            var tier1Files = _memManager.ListFiles();

            foreach (var pattern in patterns)
            {
                if (pattern.PromotionStatus == PromotionStatus.Accepted) continue;
                if (string.IsNullOrEmpty(pattern.Description)) continue;

                var keywords = pattern.Description.ToLower().Split(
                    new[] { ' ', ',', '.', ':', ';', '(', ')', '-', '_' },
                    StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 3)
                    .Take(5)
                    .ToList();

                foreach (var file in tier1Files)
                {
                    if (string.IsNullOrEmpty(pattern.TargetFile)) continue;
                    if (file.Filename.Replace(".md", "") != pattern.TargetFile.Replace(".md", ""))
                        continue;

                    var body = file.Body.ToLower();
                    int matchCount = keywords.Count(kw => body.Contains(kw));
                    if (matchCount >= 2)
                    {
                        pattern.ConflictsWith = file.Filename;
                        pattern.ConflictDetail =
                            $"Potential overlap with existing Tier 1 entry in {file.Filename}";
                    }
                }
            }
        }

        void WriteInferredFiles(List<InferredPattern> patterns)
        {
            if (!Directory.Exists(InferredDir))
                Directory.CreateDirectory(InferredDir);

            foreach (var pattern in patterns)
            {
                var filename = $"{pattern.AnalyzerName}-{pattern.Id}.md";
                var filePath = Path.Combine(InferredDir, filename);
                var content = pattern.ToMarkdown();

                var tmpPath = filePath + ".tmp";
                File.WriteAllText(tmpPath, content);
                if (File.Exists(filePath)) File.Delete(filePath);
                File.Move(tmpPath, filePath);
            }
        }
    }
}
