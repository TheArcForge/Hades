// Tests/Editor/Asphodel/AsphodeIntegrationTests.cs
using System;
using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Asphodel;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;

namespace ArcForge.Hades.Editor.Tests.Asphodel
{
    public class AsphodeIntegrationTests
    {
        string _testDbPath;
        string _testMemDir;
        GraphDatabase _db;
        GraphDatabase _savedInstance;
        MemoryManager _manager;
        MemoryValidator _validator;

        [SetUp]
        public void SetUp()
        {
            _savedInstance = GraphDatabase.Instance;
            _testDbPath = Path.Combine(Path.GetTempPath(), $"hades_int_test_{Guid.NewGuid()}.db");
            _db = new GraphDatabase(_testDbPath);

            _testMemDir = Path.Combine(Path.GetTempPath(), $"hades_int_mem_{Guid.NewGuid()}");
            _manager = new MemoryManager(_testMemDir);
            _manager.EnsureDirectory();
            _manager.EnsureDefaults();
            _manager.EnsureProposalsDirectory();

            _validator = new MemoryValidator(_manager, _db);
        }

        [TearDown]
        public void TearDown()
        {
            _db?.Dispose();
            GraphDatabase.RestoreInstanceForTests(_savedInstance);
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
            if (File.Exists(_testDbPath + "-wal")) File.Delete(_testDbPath + "-wal");
            if (File.Exists(_testDbPath + "-shm")) File.Delete(_testDbPath + "-shm");
            if (Directory.Exists(_testMemDir)) Directory.Delete(_testMemDir, true);
        }

        [Test]
        public void EndToEnd_EditMemory_ValidateRuns_StatusUpdates()
        {
            _db.InsertNode(new NodeRecord("Scene") { Name = "Main", Path = "Assets/Scenes/Main.unity" });

            var content = "---\nvalidation_status: ok\n---\n# Decisions\n\n### Multi-scene\n\n<!-- hades-validation\nquery_type: exists\nquery: find_nodes_by_type(Scene)\nmin_count: 1\nfailure_message: No scenes found.\n-->\n\nWe use scenes.\n";
            _manager.WriteFile("decisions", content);

            var result = _validator.ValidateFile("decisions");
            Assert.AreEqual("ok", result.Status);

            var updated = _manager.ReadFile("decisions");
            Assert.AreEqual("ok", updated.Frontmatter["validation_status"]);
        }

        [Test]
        public void EndToEnd_ValidationDetectsDrift()
        {
            var content = "---\nvalidation_status: ok\n---\n# Patterns\n\n### Object Pooling\n\n<!-- hades-validation\nquery_type: exists\nquery: search_by_name(%Pool%, Script)\nmin_count: 1\nfailure_message: No pooling scripts found.\n-->\n\nWe use object pooling.\n";
            _manager.WriteFile("patterns", content);

            var result = _validator.ValidateFile("patterns");
            Assert.AreEqual("warning", result.Status);

            var updated = _manager.ReadFile("patterns");
            Assert.AreEqual("warning", updated.Frontmatter["validation_status"]);
            Assert.IsTrue(updated.Body.Contains("HADES VALIDATION WARNING"));
        }

        [Test]
        public void EndToEnd_DriftClears_WhenProjectCatchesUp()
        {
            var content = "---\nvalidation_status: ok\n---\n# Patterns\n\n### Pooling\n\n<!-- hades-validation\nquery_type: exists\nquery: search_by_name(%Pool%, ScriptType)\nmin_count: 1\nfailure_message: No pool scripts.\n-->\n\nWe use pooling.\n";
            _manager.WriteFile("patterns", content);

            var result1 = _validator.ValidateFile("patterns");
            Assert.AreEqual("warning", result1.Status);

            _db.InsertNode(new NodeRecord("ScriptType") { Name = "ObjectPool", Path = "Assets/Scripts/ObjectPool.cs" });

            var result2 = _validator.ValidateFile("patterns");
            Assert.AreEqual("ok", result2.Status);

            var updated = _manager.ReadFile("patterns");
            Assert.IsFalse(updated.Body.Contains("HADES VALIDATION WARNING"));
        }

        [Test]
        public void EndToEnd_ProposalWorkflow()
        {
            var proposalId = _manager.CreateProposal("patterns", "### New Pattern\n\nSingleton pattern.", "Observed usage");

            var proposals = _manager.ListProposals();
            Assert.AreEqual(1, proposals.Count);

            var accepted = _manager.AcceptProposal(proposalId);
            Assert.IsTrue(accepted);

            var patterns = _manager.ReadFile("patterns");
            Assert.IsTrue(patterns.Body.Contains("Singleton pattern."));

            proposals = _manager.ListProposals();
            Assert.AreEqual(0, proposals.Count);
        }

        [Test]
        public void EndToEnd_RejectProposal()
        {
            var proposalId = _manager.CreateProposal("patterns", "Bad content", "Bad reason");
            var rejected = _manager.RejectProposal(proposalId);
            Assert.IsTrue(rejected);

            var proposals = _manager.ListProposals();
            Assert.AreEqual(0, proposals.Count);
        }
    }
}
