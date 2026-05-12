// Tests/Editor/Asphodel/MemoryManagerTests.cs
using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Asphodel;

namespace ArcForge.Hades.Editor.Tests.Asphodel
{
    public class MemoryManagerTests
    {
        string _testDir;
        MemoryManager _manager;

        [SetUp]
        public void SetUp()
        {
            _testDir = Path.Combine(Path.GetTempPath(), $"hades_mem_test_{System.Guid.NewGuid()}");
            _manager = new MemoryManager(_testDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }

        [Test]
        public void EnsureDirectory_CreatesMemoryDir()
        {
            _manager.EnsureDirectory();
            Assert.IsTrue(Directory.Exists(_testDir));
        }

        [Test]
        public void EnsureDefaults_CreatesAllTemplateFiles()
        {
            _manager.EnsureDirectory();
            _manager.EnsureDefaults();

            Assert.IsTrue(File.Exists(Path.Combine(_testDir, "decisions.md")));
            Assert.IsTrue(File.Exists(Path.Combine(_testDir, "patterns.md")));
            Assert.IsTrue(File.Exists(Path.Combine(_testDir, "conventions.md")));
            Assert.IsTrue(File.Exists(Path.Combine(_testDir, "pitfalls.md")));
            Assert.IsTrue(File.Exists(Path.Combine(_testDir, "glossary.md")));
            Assert.IsTrue(File.Exists(Path.Combine(_testDir, "intent.md")));
        }

        [Test]
        public void EnsureDefaults_DoesNotOverwriteExistingFiles()
        {
            _manager.EnsureDirectory();
            File.WriteAllText(Path.Combine(_testDir, "decisions.md"), "custom content");
            _manager.EnsureDefaults();

            Assert.AreEqual("custom content", File.ReadAllText(Path.Combine(_testDir, "decisions.md")));
        }

        [Test]
        public void ReadFile_ReturnsMemoryFileWithFrontmatter()
        {
            _manager.EnsureDirectory();
            _manager.EnsureDefaults();

            var file = _manager.ReadFile("decisions");
            Assert.IsNotNull(file);
            Assert.AreEqual("decisions.md", file.Filename);
            Assert.IsTrue(file.Frontmatter.ContainsKey("validation_status"));
        }

        [Test]
        public void ReadFile_NonexistentFile_ReturnsNull()
        {
            _manager.EnsureDirectory();
            var file = _manager.ReadFile("nonexistent");
            Assert.IsNull(file);
        }

        [Test]
        public void WriteFile_AtomicWrite_CreatesFile()
        {
            _manager.EnsureDirectory();
            var content = "---\nvalidation_status: ok\n---\n# Test\n";
            _manager.WriteFile("test", content);

            var path = Path.Combine(_testDir, "test.md");
            Assert.IsTrue(File.Exists(path));
            Assert.AreEqual(content, File.ReadAllText(path));
            Assert.IsFalse(File.Exists(path + ".tmp"));
        }

        [Test]
        public void ListFiles_ReturnsAllMarkdownFiles()
        {
            _manager.EnsureDirectory();
            _manager.EnsureDefaults();

            var files = _manager.ListFiles();
            Assert.AreEqual(6, files.Count);
        }

        [Test]
        public void EnsureProposalsDir_CreatesSubdirectory()
        {
            _manager.EnsureDirectory();
            _manager.EnsureProposalsDirectory();

            Assert.IsTrue(Directory.Exists(Path.Combine(_testDir, "proposals")));
        }

        [Test]
        public void CreateProposal_CreatesFileInProposalsDir()
        {
            _manager.EnsureDirectory();
            _manager.EnsureProposalsDirectory();

            var id = _manager.CreateProposal("patterns", "New pattern content", "Found recurring use of object pooling");

            Assert.IsTrue(File.Exists(Path.Combine(_testDir, "proposals", id + ".md")));
        }

        [Test]
        public void ListProposals_ReturnsProposalFiles()
        {
            _manager.EnsureDirectory();
            _manager.EnsureProposalsDirectory();
            _manager.CreateProposal("patterns", "content", "rationale");

            var proposals = _manager.ListProposals();
            Assert.AreEqual(1, proposals.Count);
        }
    }
}
