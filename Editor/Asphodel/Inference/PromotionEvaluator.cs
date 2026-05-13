// Editor/Asphodel/Inference/PromotionEvaluator.cs
using System;
using System.Collections.Generic;

namespace ArcForge.Hades.Editor.Asphodel.Inference
{
    public class PromotionEvaluator
    {
        readonly MemoryManager _memManager;
        readonly InferenceConfig _config;

        public PromotionEvaluator(MemoryManager memManager, InferenceConfig config)
        {
            _memManager = memManager;
            _config = config;
        }

        public void Evaluate(List<InferredPattern> patterns)
        {
            foreach (var pattern in patterns)
            {
                switch (pattern.PromotionStatus)
                {
                    case PromotionStatus.Accepted:
                    case PromotionStatus.Dismissed:
                        continue;

                    case PromotionStatus.Proposed:
                        if (pattern.Confidence < _config.PromotionConfidenceThreshold ||
                            pattern.SampleSize < _config.PromotionSampleMinimum)
                        {
                            pattern.PromotionStatus = PromotionStatus.Pending;
                        }
                        break;

                    case PromotionStatus.Deferred:
                        var daysSinceLastConfirmed =
                            (DateTimeOffset.UtcNow - pattern.LastConfirmed).TotalDays;
                        if (daysSinceLastConfirmed >= _config.DeferredCooldownDays &&
                            pattern.Confidence >= _config.PromotionConfidenceThreshold &&
                            pattern.SampleSize >= _config.PromotionSampleMinimum)
                        {
                            pattern.PromotionStatus = PromotionStatus.Proposed;
                            CreatePromotionProposal(pattern);
                        }
                        break;

                    case PromotionStatus.Pending:
                        if (pattern.Confidence >= _config.PromotionConfidenceThreshold &&
                            pattern.SampleSize >= _config.PromotionSampleMinimum)
                        {
                            pattern.PromotionStatus = PromotionStatus.Proposed;
                            CreatePromotionProposal(pattern);
                        }
                        break;
                }
            }
        }

        void CreatePromotionProposal(InferredPattern pattern)
        {
            var rationale = $"Tier 2 inference ({pattern.AnalyzerName}): " +
                $"confidence {pattern.Confidence * 100:F0}%, " +
                $"{pattern.SampleSize} samples, " +
                $"observed {pattern.FirstObserved:yyyy-MM-dd} to {pattern.LastConfirmed:yyyy-MM-dd}";

            _memManager.CreateProposal(pattern.TargetFile, pattern.Description, rationale);
        }
    }
}
