// Tests/Editor/Asphodel/MemoryValidatorTests.cs
using System;
using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Asphodel;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;

namespace ArcForge.Hades.Editor.Tests.Asphodel
{
    public class MemoryValidatorTests
    {
        string _testDbPath;
        string _testMemDir;
        GraphDatabase _db;
        GraphDatabase _savedInstance;
        MemoryManager _memManager;
        MemoryValidator _validator;

        [SetUp]
        public void SetUp()
        {
            _savedInstance = GraphDatabase.Instance;
            _testDbPath = Path.Combine(Path.GetTempPath(), $"hades_val_test_{Guid.NewGuid()}.db");
            _db = new GraphDatabase(_testDbPath);

            _testMemDir = Path.Combine(Path.GetTempPath(), $"hades_mem_val_{Guid.NewGuid()}");
            _memManager = new MemoryManager(_testMemDir);
            _memManager.EnsureDirectory();

            _validator = new MemoryValidator(_memManager, _db);
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
        public void Validate_RulePasses_StatusIsOk()
        {
            _db.InsertNode(new NodeRecord("ScriptableObject") { Name = "DamageChannel", Path = "Assets/SO/DamageChannel.asset" });
            _db.InsertNode(new NodeRecord("ScriptableObject") { Name = "HealthChannel", Path = "Assets/SO/HealthChannel.asset" });
            _db.InsertNode(new NodeRecord("ScriptableObject") { Name = "AudioChannel", Path = "Assets/SO/AudioChannel.asset" });

            var content = "---\nvalidation_status: ok\n---\n# Patterns\n\n### SO Channels\n\n<!-- hades-validation\nquery_type: exists\nquery: search_by_name(%Channel, ScriptableObject)\nmin_count: 3\nfailure_message: Expected at least 3 SO channels.\n-->\n\nWe use SO event channels.\n";
            _memManager.WriteFile("patterns", content);

            var result = _validator.ValidateFile("patterns");

            Assert.AreEqual("ok", result.Status);
            Assert.AreEqual(1, result.RulesChecked);
            Assert.AreEqual(1, result.RulesPassed);
        }

        [Test]
        public void Validate_RuleFails_StatusIsWarning()
        {
            var content = "---\nvalidation_status: ok\n---\n# Patterns\n\n### SO Channels\n\n<!-- hades-validation\nquery_type: exists\nquery: search_by_name(%Channel, ScriptableObject)\nmin_count: 3\nfailure_message: Expected at least 3 SO channels.\n-->\n\nWe use SO event channels.\n";
            _memManager.WriteFile("patterns", content);

            var result = _validator.ValidateFile("patterns");

            Assert.AreEqual("warning", result.Status);
            Assert.AreEqual(1, result.RulesChecked);
            Assert.AreEqual(0, result.RulesPassed);
            Assert.AreEqual(1, result.RulesWarning);
        }

        [Test]
        public void Validate_FailedRule_InsertsWarningComment()
        {
            var content = "---\nvalidation_status: ok\n---\n# Patterns\n\n### SO Channels\n\n<!-- hades-validation\nquery_type: exists\nquery: search_by_name(%Channel, ScriptableObject)\nmin_count: 3\nfailure_message: Expected at least 3 SO channels.\n-->\n\nWe use SO event channels.\n";
            _memManager.WriteFile("patterns", content);

            _validator.ValidateFile("patterns");

            var updated = _memManager.ReadFile("patterns");
            Assert.IsTrue(updated.Body.Contains("HADES VALIDATION WARNING"));
            Assert.IsTrue(updated.Body.Contains("Expected at least 3 SO channels."));
        }

        [Test]
        public void Validate_UpdatesFrontmatterTimestamp()
        {
            var content = "---\nvalidation_status: ok\n---\n# Test\n";
            _memManager.WriteFile("test", content);

            _validator.ValidateFile("test");

            var updated = _memManager.ReadFile("test");
            Assert.IsTrue(updated.Frontmatter.ContainsKey("last_validated_against_graph"));
        }

        [Test]
        public void Validate_NoRules_StatusRemainsOk()
        {
            var content = "---\nvalidation_status: ok\n---\n# No rules here\n";
            _memManager.WriteFile("norules", content);

            var result = _validator.ValidateFile("norules");

            Assert.AreEqual("ok", result.Status);
            Assert.AreEqual(0, result.RulesChecked);
        }

        [Test]
        public void Validate_FindNodesByType_Works()
        {
            _db.InsertNode(new NodeRecord("Scene") { Name = "Main", Path = "Assets/Scenes/Main.unity" });
            _db.InsertNode(new NodeRecord("Scene") { Name = "Menu", Path = "Assets/Scenes/Menu.unity" });

            var content = "---\nvalidation_status: ok\n---\n# Decisions\n\n### Multi-scene\n\n<!-- hades-validation\nquery_type: exists\nquery: find_nodes_by_type(Scene)\nmin_count: 2\nfailure_message: Expected at least 2 scenes.\n-->\n\nWe use multi-scene.\n";
            _memManager.WriteFile("decisions", content);

            var result = _validator.ValidateFile("decisions");
            Assert.AreEqual("ok", result.Status);
        }

        [Test]
        public void Validate_ClearsOldWarningOnPass()
        {
            _db.InsertNode(new NodeRecord("ScriptableObject") { Name = "DamageChannel", Path = "a" });
            _db.InsertNode(new NodeRecord("ScriptableObject") { Name = "HealthChannel", Path = "b" });
            _db.InsertNode(new NodeRecord("ScriptableObject") { Name = "AudioChannel", Path = "c" });

            var content = "---\nvalidation_status: warning\n---\n# Patterns\n\n### SO Channels\n\n<!-- hades-validation\nquery_type: exists\nquery: search_by_name(%Channel, ScriptableObject)\nmin_count: 3\nfailure_message: Expected at least 3.\n-->\n\n<!-- HADES VALIDATION WARNING (2026-05-11):\nExpected at least 3.\nFound 0 matching assets. -->\n\nContent.\n";
            _memManager.WriteFile("patterns", content);

            var result = _validator.ValidateFile("patterns");
            Assert.AreEqual("ok", result.Status);

            var updated = _memManager.ReadFile("patterns");
            Assert.IsFalse(updated.Body.Contains("HADES VALIDATION WARNING"));
        }

        [Test]
        public void Validate_RepeatedFailure_DoesNotDuplicateWarnings()
        {
            // No matching nodes in DB — rule will fail every time
            var content = "---\nvalidation_status: ok\n---\n# Patterns\n\n### SO Channels\n\n<!-- hades-validation\nquery_type: exists\nquery: search_by_name(%Channel, ScriptableObject)\nmin_count: 3\nfailure_message: Expected at least 3 SO channels.\n-->\n\nWe use SO event channels.\n";
            _memManager.WriteFile("patterns", content);

            // Run validation twice
            _validator.ValidateFile("patterns");
            _validator.ValidateFile("patterns");

            // Should have exactly one warning block, not two
            var updated = _memManager.ReadFile("patterns");
            var warningCount = System.Text.RegularExpressions.Regex.Matches(
                updated.Body, "HADES VALIDATION WARNING").Count;
            Assert.AreEqual(1, warningCount, "Expected exactly 1 warning block after 2 validation passes, but found " + warningCount);
        }

        [Test]
        public void ValidateAll_ValidatesAllFiles()
        {
            _memManager.EnsureDefaults();
            var results = _validator.ValidateAll();
            Assert.AreEqual(6, results.Count);
        }
    }
}
