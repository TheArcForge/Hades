// Tests/Editor/Asphodel/Inference/InferenceIntegrationTests.cs
using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ArcForge.Hades.Editor.Asphodel;
using ArcForge.Hades.Editor.Asphodel.Inference;
using ArcForge.Hades.Editor.Charon;
using ArcForge.Hades.Editor.MCP.Tools;

namespace ArcForge.Hades.Editor.Tests.Asphodel.Inference
{
    public class InferenceIntegrationTests
    {
        string _testMemDir;
        MemoryManager _memManager;

        [SetUp]
        public void SetUp()
        {
            _testMemDir = Path.Combine(Path.GetTempPath(), $"hades_integ_test_{Guid.NewGuid()}");
            _memManager = new MemoryManager(_testMemDir);
            _memManager.EnsureDirectory();
            _memManager.EnsureDefaults();
            AsphodeTools.SetTestManager(_memManager);
        }

        [TearDown]
        public void TearDown()
        {
            AsphodeTools.ClearTestOverrides();
            if (Directory.Exists(_testMemDir)) Directory.Delete(_testMemDir, true);
        }

        [Test]
        public void GetMemorySummary_IncludesInferredPatterns()
        {
            // Seed an inferred file
            var inferredDir = Path.Combine(_testMemDir, "inferred");
            Directory.CreateDirectory(inferredDir);
            var inferredContent = "---\nstatus: inferred\nanalyzer: acceptance_rate\nconfidence: 0.92\nsample_size: 55\nfirst_observed: 2026-04-01\nlast_confirmed: 2026-05-13\npromotion_status: pending\n---\n\nINFERRED PATTERN (not confirmed by team)\n\nTeam uses object pooling for spawned entities.\n";
            File.WriteAllText(Path.Combine(inferredDir, "acceptance_rate-abc12345.md"), inferredContent);

            var result = AsphodeTools.GetMemorySummary();
            Assert.IsFalse(result.IsError);
            Assert.IsTrue(result.Text.Contains("INFERRED") || result.Text.Contains("inferred"),
                $"Summary should include inferred patterns, got: {result.Text}");
        }

        [Test]
        public void RecallMemory_SearchesInferredFiles()
        {
            // Seed an inferred file about object pooling
            var inferredDir = Path.Combine(_testMemDir, "inferred");
            Directory.CreateDirectory(inferredDir);
            var inferredContent = "---\nstatus: inferred\nanalyzer: acceptance_rate\nconfidence: 0.92\nsample_size: 55\nfirst_observed: 2026-04-01\nlast_confirmed: 2026-05-13\npromotion_status: pending\n---\n\nINFERRED PATTERN (not confirmed by team)\n\nTeam uses object pooling for spawned entities.\n";
            File.WriteAllText(Path.Combine(inferredDir, "acceptance_rate-abc12345.md"), inferredContent);

            var result = AsphodeTools.RecallMemory("object pooling");
            Assert.IsFalse(result.IsError);
            Assert.IsTrue(result.Text.Contains("object pooling"),
                $"Recall should find inferred pattern, got: {result.Text}");
        }

        [Test]
        public void EndToEnd_PromotionFlow()
        {
            // Set up Charon DB with enough data to trigger promotion
            var charonDbPath = Path.Combine(Path.GetTempPath(), $"charon_e2e_{Guid.NewGuid()}.db");
            var charonDb = new CharonDatabase(charonDbPath);

            try
            {
                var config = new InferenceConfig
                {
                    PromotionConfidenceThreshold = 0.8f, // Lower for test
                    PromotionSampleMinimum = 10
                };
                var engine = new PatternInferenceEngine(_memManager, charonDb, config);

                // Seed 20 traces with high acceptance pattern. Anchor to "now" (not a fixed
                // calendar date) so the traces always fall inside the engine's MaxTraceLookbackDays
                // (90d) window regardless of when the suite runs — a fixed 2026-04-01 date aged out
                // of the window on 2026-06-30 and silently failed inference from then on.
                var baseTime = DateTimeOffset.UtcNow.AddDays(-1);
                for (int i = 0; i < 20; i++)
                {
                    var t = baseTime.AddMinutes(i * 15);
                    var traceId = TraceIdGenerator.NewTraceId();
                    charonDb.InsertTrace(new TraceRecord
                    {
                        TraceId = traceId,
                        RootSpanName = "mcp.tool.find_prefabs_with_component",
                        StartTime = t.ToUnixTimeMilliseconds(),
                        EndTime = t.AddMilliseconds(200).ToUnixTimeMilliseconds(),
                        Status = SpanStatus.Ok,
                        SpanCount = 1
                    });
                    var span = new SpanRecord
                    {
                        SpanId = TraceIdGenerator.NewSpanId(),
                        TraceId = traceId,
                        Name = "mcp.tool.find_prefabs_with_component",
                        Kind = SpanKind.Server,
                        StartTime = t.ToUnixTimeMilliseconds(),
                        EndTime = t.AddMilliseconds(200).ToUnixTimeMilliseconds(),
                        Status = SpanStatus.Ok
                    };
                    span.Attributes[SpanAttributes.ToolName] = "find_prefabs_with_component";
                    span.Attributes["component_type"] = "ObjectPool";
                    charonDb.InsertSpan(span);
                }

                // Run inference
                engine.RunInference();

                // Verify inferred file created
                var inferredDir = Path.Combine(_testMemDir, "inferred");
                Assert.IsTrue(Directory.Exists(inferredDir));
                var files = Directory.GetFiles(inferredDir, "*.md");
                Assert.IsTrue(files.Length > 0, "Should have inferred files");

                // Verify proposal created (threshold met: >80% confidence, >=10 samples)
                var proposals = _memManager.ListProposals();
                Assert.IsTrue(proposals.Count > 0,
                    "Should have created a promotion proposal");
                Assert.IsTrue(proposals.Any(p =>
                    p.Body.Contains("find_prefabs_with_component") ||
                    p.Body.Contains("ObjectPool")),
                    "At least one proposal should reference the detected pattern");

                // Accept the matching proposal
                var matchingProposal = proposals.First(p =>
                    p.Body.Contains("find_prefabs_with_component") ||
                    p.Body.Contains("ObjectPool"));
                var accepted = _memManager.AcceptProposal(
                    System.IO.Path.GetFileNameWithoutExtension(matchingProposal.FilePath));
                Assert.IsTrue(accepted);

                // Verify Tier 1 file updated
                var patternsFile = _memManager.ReadFile("patterns");
                Assert.IsNotNull(patternsFile);
                Assert.IsTrue(patternsFile.Body.Contains("find_prefabs_with_component") ||
                    patternsFile.Body.Contains("ObjectPool"),
                    "Patterns file should contain the promoted pattern");
            }
            finally
            {
                charonDb?.Dispose();
                if (File.Exists(charonDbPath)) File.Delete(charonDbPath);
                if (File.Exists(charonDbPath + "-wal")) File.Delete(charonDbPath + "-wal");
                if (File.Exists(charonDbPath + "-shm")) File.Delete(charonDbPath + "-shm");
            }
        }
    }
}
