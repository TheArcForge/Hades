// Tests/Editor/Asphodel/Inference/PromotionEvaluatorTests.cs
using System;
using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using ArcForge.Hades.Editor.Asphodel;
using ArcForge.Hades.Editor.Asphodel.Inference;

namespace ArcForge.Hades.Editor.Tests.Asphodel.Inference
{
    public class PromotionEvaluatorTests
    {
        string _testMemDir;
        MemoryManager _memManager;
        PromotionEvaluator _evaluator;
        InferenceConfig _config;

        [SetUp]
        public void SetUp()
        {
            _testMemDir = Path.Combine(Path.GetTempPath(), $"hades_promo_test_{Guid.NewGuid()}");
            _memManager = new MemoryManager(_testMemDir);
            _memManager.EnsureDirectory();
            _memManager.EnsureDefaults();
            _config = new InferenceConfig();
            _evaluator = new PromotionEvaluator(_memManager, _config);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testMemDir)) Directory.Delete(_testMemDir, true);
        }

        [Test]
        public void Evaluate_AboveThreshold_CreatesProposal()
        {
            var patterns = new List<InferredPattern>
            {
                new InferredPattern
                {
                    Id = "abc12345",
                    AnalyzerName = "acceptance_rate",
                    PatternKey = "test_pattern",
                    Description = "Team uses object pooling",
                    TargetFile = "patterns",
                    Confidence = 0.95f,
                    SampleSize = 60,
                    FirstObserved = DateTimeOffset.UtcNow.AddDays(-30),
                    LastConfirmed = DateTimeOffset.UtcNow,
                    PromotionStatus = PromotionStatus.Pending
                }
            };

            _evaluator.Evaluate(patterns);

            Assert.AreEqual(PromotionStatus.Proposed, patterns[0].PromotionStatus);
            var proposals = _memManager.ListProposals();
            Assert.AreEqual(1, proposals.Count);
            Assert.IsTrue(proposals[0].Body.Contains("object pooling"));
        }

        [Test]
        public void Evaluate_BelowConfidence_NoProposal()
        {
            var patterns = new List<InferredPattern>
            {
                new InferredPattern
                {
                    Id = "abc12345",
                    AnalyzerName = "acceptance_rate",
                    PatternKey = "test_pattern",
                    Description = "Weak pattern",
                    TargetFile = "patterns",
                    Confidence = 0.80f,
                    SampleSize = 60,
                    FirstObserved = DateTimeOffset.UtcNow.AddDays(-30),
                    LastConfirmed = DateTimeOffset.UtcNow,
                    PromotionStatus = PromotionStatus.Pending
                }
            };

            _evaluator.Evaluate(patterns);

            Assert.AreEqual(PromotionStatus.Pending, patterns[0].PromotionStatus);
            Assert.AreEqual(0, _memManager.ListProposals().Count);
        }

        [Test]
        public void Evaluate_BelowSampleSize_NoProposal()
        {
            var patterns = new List<InferredPattern>
            {
                new InferredPattern
                {
                    Id = "abc12345",
                    AnalyzerName = "acceptance_rate",
                    PatternKey = "test_pattern",
                    Description = "Not enough samples",
                    TargetFile = "patterns",
                    Confidence = 0.95f,
                    SampleSize = 30,
                    FirstObserved = DateTimeOffset.UtcNow.AddDays(-30),
                    LastConfirmed = DateTimeOffset.UtcNow,
                    PromotionStatus = PromotionStatus.Pending
                }
            };

            _evaluator.Evaluate(patterns);

            Assert.AreEqual(PromotionStatus.Pending, patterns[0].PromotionStatus);
        }

        [Test]
        public void Evaluate_DismissedPattern_SkipsProposal()
        {
            var patterns = new List<InferredPattern>
            {
                new InferredPattern
                {
                    Id = "abc12345",
                    AnalyzerName = "acceptance_rate",
                    PatternKey = "test_pattern",
                    Description = "Dismissed pattern",
                    TargetFile = "patterns",
                    Confidence = 0.99f,
                    SampleSize = 100,
                    FirstObserved = DateTimeOffset.UtcNow.AddDays(-30),
                    LastConfirmed = DateTimeOffset.UtcNow,
                    PromotionStatus = PromotionStatus.Dismissed
                }
            };

            _evaluator.Evaluate(patterns);

            Assert.AreEqual(PromotionStatus.Dismissed, patterns[0].PromotionStatus);
            Assert.AreEqual(0, _memManager.ListProposals().Count);
        }

        [Test]
        public void Evaluate_DeferredPastCooldown_ResetsToProposed()
        {
            var patterns = new List<InferredPattern>
            {
                new InferredPattern
                {
                    Id = "abc12345",
                    AnalyzerName = "acceptance_rate",
                    PatternKey = "test_pattern",
                    Description = "Deferred pattern past cooldown",
                    TargetFile = "patterns",
                    Confidence = 0.95f,
                    SampleSize = 60,
                    FirstObserved = DateTimeOffset.UtcNow.AddDays(-60),
                    LastConfirmed = DateTimeOffset.UtcNow.AddDays(-15), // past 14-day cooldown
                    PromotionStatus = PromotionStatus.Deferred
                }
            };

            _evaluator.Evaluate(patterns);

            Assert.AreEqual(PromotionStatus.Proposed, patterns[0].PromotionStatus);
        }

        [Test]
        public void Evaluate_DeferredWithinCooldown_StaysDeferred()
        {
            var patterns = new List<InferredPattern>
            {
                new InferredPattern
                {
                    Id = "abc12345",
                    AnalyzerName = "acceptance_rate",
                    PatternKey = "test_pattern",
                    Description = "Deferred pattern within cooldown",
                    TargetFile = "patterns",
                    Confidence = 0.95f,
                    SampleSize = 60,
                    FirstObserved = DateTimeOffset.UtcNow.AddDays(-10),
                    LastConfirmed = DateTimeOffset.UtcNow.AddDays(-5), // within 14-day cooldown
                    PromotionStatus = PromotionStatus.Deferred
                }
            };

            _evaluator.Evaluate(patterns);

            Assert.AreEqual(PromotionStatus.Deferred, patterns[0].PromotionStatus);
        }

        [Test]
        public void Evaluate_ProposedConfidenceDropped_RetractedToPending()
        {
            var patterns = new List<InferredPattern>
            {
                new InferredPattern
                {
                    Id = "abc12345",
                    AnalyzerName = "acceptance_rate",
                    PatternKey = "test_pattern",
                    Description = "Was proposed but confidence dropped",
                    TargetFile = "patterns",
                    Confidence = 0.70f, // dropped below 0.9
                    SampleSize = 60,
                    FirstObserved = DateTimeOffset.UtcNow.AddDays(-30),
                    LastConfirmed = DateTimeOffset.UtcNow,
                    PromotionStatus = PromotionStatus.Proposed
                }
            };

            _evaluator.Evaluate(patterns);

            Assert.AreEqual(PromotionStatus.Pending, patterns[0].PromotionStatus);
        }
    }
}
