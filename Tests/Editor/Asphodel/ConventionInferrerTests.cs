using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Asphodel;
using ArcForge.Hades.Editor.Asphodel.Conventions;

namespace ArcForge.Hades.Editor.Tests.Asphodel
{
    sealed class FakeDetector : IConventionDetector
    {
        public string Key { get; }
        public ConventionResult Next;
        public FakeDetector(string key) { Key = key; }
        public ConventionResult Detect(GraphDatabase db) => Next ?? ConventionResult.NotFired();
    }

    public class ConventionInferrerTests
    {
        [Test]
        public void Marker_IsStablePerKey()
        {
            Assert.AreEqual("<!-- hades-convention:naming -->", ConventionMarkdown.Marker("naming"));
        }

        [Test]
        public void Tier2_ContainsStatementEvidenceAndMarker()
        {
            var r = new ConventionResult { Fired = true, Statement = "S", Evidence = "E", Confidence = 0.8, TargetFile = "patterns" };
            var md = ConventionMarkdown.Tier2("naming", r);
            StringAssert.Contains("status: inferred", md);
            StringAssert.Contains("S", md);
            StringAssert.Contains("E", md);
            StringAssert.Contains(ConventionMarkdown.Marker("naming"), md);
        }

        [Test]
        public void ProposalBody_ContainsMarker()
        {
            var r = new ConventionResult { Fired = true, Statement = "S", Evidence = "E", Confidence = 0.8, TargetFile = "patterns" };
            StringAssert.Contains(ConventionMarkdown.Marker("naming"), ConventionMarkdown.ProposalBody("naming", r));
        }

        static MemoryManager TempMemory(out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), $"hades_inf_{System.Guid.NewGuid()}");
            var m = new MemoryManager(Path.Combine(dir, ".arcforge", "memory"));
            m.EnsureDirectory();
            return m;
        }

        [Test]
        public void Fired_WritesTier2AndProposalOnce()
        {
            var mem = TempMemory(out var dir);
            var det = new FakeDetector("naming") { Next = new ConventionResult { Fired = true, Statement = "S", Evidence = "E", Confidence = 0.9, TargetFile = "conventions" } };
            var inf = new ConventionInferrer(mem, null, new List<IConventionDetector> { det });

            inf.Run();
            var tier2 = Path.Combine(mem.MemoryDir, "inferred", "convention-naming.md");
            var prop  = Path.Combine(mem.MemoryDir, "proposals", "convention-naming.md");
            Assert.IsTrue(File.Exists(tier2));
            Assert.IsTrue(File.Exists(prop));

            // Second run must NOT create a duplicate proposal (dedup via ledger/stable filename).
            inf.Run();
            Assert.AreEqual(1, Directory.GetFiles(Path.Combine(mem.MemoryDir, "proposals")).Length);
            Directory.Delete(dir, true);
        }

        [Test]
        public void Retracts_Tier2_WhenSignalDisappears()
        {
            var mem = TempMemory(out var dir);
            var det = new FakeDetector("naming") { Next = new ConventionResult { Fired = true, Statement = "S", Evidence = "E", Confidence = 0.9, TargetFile = "conventions" } };
            var inf = new ConventionInferrer(mem, null, new List<IConventionDetector> { det });
            inf.Run();
            Assert.IsTrue(File.Exists(Path.Combine(mem.MemoryDir, "inferred", "convention-naming.md")));

            det.Next = ConventionResult.NotFired();
            inf.Run();
            Assert.IsFalse(File.Exists(Path.Combine(mem.MemoryDir, "inferred", "convention-naming.md")),
                "Tier-2 view retracts when the graph signal is gone");
            Directory.Delete(dir, true);
        }

        [Test]
        public void Dismissed_IsNotReproposed()
        {
            var mem = TempMemory(out var dir);
            var det = new FakeDetector("naming") { Next = new ConventionResult { Fired = true, Statement = "S", Evidence = "E", Confidence = 0.9, TargetFile = "conventions" } };
            var inf = new ConventionInferrer(mem, null, new List<IConventionDetector> { det });
            inf.Run(); // creates proposal

            // Simulate the dashboard REJECT: delete the proposal file, do NOT add to Tier-1.
            File.Delete(Path.Combine(mem.MemoryDir, "proposals", "convention-naming.md"));
            inf.Run(); // should record dismissal, not re-create
            Assert.IsFalse(File.Exists(Path.Combine(mem.MemoryDir, "proposals", "convention-naming.md")),
                "a dismissed convention is not re-proposed");
            Directory.Delete(dir, true);
        }

        [Test]
        public void Promoted_ThenStale_EmitsRemovalProposal()
        {
            var mem = TempMemory(out var dir);
            var det = new FakeDetector("naming") { Next = new ConventionResult { Fired = true, Statement = "S", Evidence = "E", Confidence = 0.9, TargetFile = "conventions" } };
            var inf = new ConventionInferrer(mem, null, new List<IConventionDetector> { det });
            inf.Run();

            // Simulate the dashboard ACCEPT: append the proposal body (with marker) to Tier-1, delete the proposal.
            mem.WriteFile("conventions", ConventionMarkdown.ProposalBody("naming", det.Next));
            File.Delete(Path.Combine(mem.MemoryDir, "proposals", "convention-naming.md"));
            inf.Run(); // records promoted; still firing → no new proposal
            Assert.IsFalse(File.Exists(Path.Combine(mem.MemoryDir, "proposals", "convention-naming.md")));

            // Signal disappears while promoted → stale-removal proposal.
            det.Next = ConventionResult.NotFired();
            inf.Run();
            Assert.IsTrue(File.Exists(Path.Combine(mem.MemoryDir, "proposals", "convention-stale-naming.md")),
                "a promoted convention that stops firing is flagged stale");
            Directory.Delete(dir, true);
        }
    }
}
