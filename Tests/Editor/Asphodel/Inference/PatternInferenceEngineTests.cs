// Tests/Editor/Asphodel/Inference/PatternInferenceEngineTests.cs
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ArcForge.Hades.Editor.Asphodel;
using ArcForge.Hades.Editor.Asphodel.Inference;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Tests.Asphodel.Inference
{
    public class PatternInferenceEngineTests
    {
        string _testMemDir;
        string _testCharonDbPath;
        MemoryManager _memManager;
        CharonDatabase _charonDb;
        PatternInferenceEngine _engine;
        InferenceConfig _config;

        [SetUp]
        public void SetUp()
        {
            _testMemDir = Path.Combine(Path.GetTempPath(), $"hades_engine_test_{Guid.NewGuid()}");
            _testCharonDbPath = Path.Combine(Path.GetTempPath(), $"charon_engine_test_{Guid.NewGuid()}.db");
            _memManager = new MemoryManager(_testMemDir);
            _memManager.EnsureDirectory();
            _memManager.EnsureDefaults();
            _charonDb = new CharonDatabase(_testCharonDbPath);
            _config = new InferenceConfig();
            _engine = new PatternInferenceEngine(_memManager, _charonDb, _config);
        }

        [TearDown]
        public void TearDown()
        {
            _charonDb?.Dispose();
            if (File.Exists(_testCharonDbPath)) File.Delete(_testCharonDbPath);
            if (File.Exists(_testCharonDbPath + "-wal")) File.Delete(_testCharonDbPath + "-wal");
            if (File.Exists(_testCharonDbPath + "-shm")) File.Delete(_testCharonDbPath + "-shm");
            if (Directory.Exists(_testMemDir)) Directory.Delete(_testMemDir, true);
        }

        [Test]
        public void RunInference_WithFixtureData_ProducesInferredFiles()
        {
            SeedDatabase(SyntheticTraceFixtures.AcceptanceRateFixture());

            _engine.RunInference();

            var inferredDir = Path.Combine(_testMemDir, "inferred");
            Assert.IsTrue(Directory.Exists(inferredDir), "Inferred directory should exist");
            var files = Directory.GetFiles(inferredDir, "*.md");
            Assert.IsTrue(files.Length > 0, "Should produce at least one inferred file");
        }

        [Test]
        public void RunInference_InferredFileHasCorrectFrontmatter()
        {
            SeedDatabase(SyntheticTraceFixtures.AcceptanceRateFixture());

            _engine.RunInference();

            var inferredDir = Path.Combine(_testMemDir, "inferred");
            var files = Directory.GetFiles(inferredDir, "*.md");
            Assert.IsTrue(files.Length > 0);

            var content = File.ReadAllText(files[0]);
            Assert.IsTrue(content.Contains("status: inferred"));
            Assert.IsTrue(content.Contains("analyzer:"));
            Assert.IsTrue(content.Contains("confidence:"));
            Assert.IsTrue(content.Contains("sample_size:"));
            Assert.IsTrue(content.Contains("promotion_status:"));
            Assert.IsTrue(content.Contains("INFERRED PATTERN"));
        }

        [Test]
        public void RunInference_MergesExistingPattern()
        {
            SeedDatabase(SyntheticTraceFixtures.AcceptanceRateFixture());

            _engine.RunInference();

            var inferredDir = Path.Combine(_testMemDir, "inferred");
            var filesBefore = Directory.GetFiles(inferredDir, "*.md");
            int countBefore = filesBefore.Length;

            // Run again — should update, not duplicate
            _engine.RunInference();

            var filesAfter = Directory.GetFiles(inferredDir, "*.md");
            Assert.AreEqual(countBefore, filesAfter.Length, "Should not duplicate inferred files on re-run");
        }

        [Test]
        public void RunInference_DisabledAnalyzer_SkipsIt()
        {
            _config.AcceptanceRateEnabled = false;
            _config.TopicClusterEnabled = false;
            _config.TimeOfDayEnabled = false;
            _config.FailureCorrelationEnabled = false;

            SeedDatabase(SyntheticTraceFixtures.AcceptanceRateFixture());

            _engine.RunInference();

            var inferredDir = Path.Combine(_testMemDir, "inferred");
            if (Directory.Exists(inferredDir))
            {
                var files = Directory.GetFiles(inferredDir, "*.md");
                Assert.AreEqual(0, files.Length, "No patterns should be produced with all analyzers disabled");
            }
        }

        [Test]
        public void RunInference_EmptyDatabase_ProducesNoFiles()
        {
            _engine.RunInference();

            var inferredDir = Path.Combine(_testMemDir, "inferred");
            if (Directory.Exists(inferredDir))
            {
                var files = Directory.GetFiles(inferredDir, "*.md");
                Assert.AreEqual(0, files.Length);
            }
        }

        // Regression: second RunInference call used to throw NullReferenceException inside
        // DetectConflicts because InferredPattern.FromMemoryFile never restores TargetFile
        // (it is not persisted to the inferred markdown), leaving it null for patterns loaded
        // from disk via LoadExistingPatterns. The fix guards TargetFile before calling .Replace.
        [Test]
        public void RunInference_SecondRun_DoesNotThrowWhenExistingPatternsHaveNullTargetFile()
        {
            SeedDatabase(SyntheticTraceFixtures.AcceptanceRateFixture());

            // First run produces inferred files (TargetFile is set in-memory)
            _engine.RunInference();

            var inferredDir = Path.Combine(_testMemDir, "inferred");
            Assert.IsTrue(Directory.Exists(inferredDir), "Inferred dir must exist after first run");
            Assert.IsTrue(Directory.GetFiles(inferredDir, "*.md").Length > 0,
                "At least one inferred file must be written after first run");

            // Second run: LoadExistingPatterns reads those files back; FromMemoryFile does not
            // restore TargetFile (field is absent from the markdown), so TargetFile == null.
            // DetectConflicts must not dereference it.
            Assert.DoesNotThrow(() => _engine.RunInference(),
                "RunInference must not throw on second call when existing inferred files have null TargetFile");
        }

        void SeedDatabase((List<TraceRecord> traces, List<SpanRecord> spans) fixture)
        {
            foreach (var trace in fixture.traces)
                _charonDb.InsertTrace(trace);
            _charonDb.InsertSpans(fixture.spans);
        }
    }
}
