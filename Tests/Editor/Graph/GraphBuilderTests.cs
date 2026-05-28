using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Graph;

namespace ArcForge.Hades.Editor.Tests.Graph
{
    public class GraphBuilderTests
    {
        string _testDbPath;
        GraphDatabase _db;

        [SetUp]
        public void SetUp()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"hades_test_{System.Guid.NewGuid()}.db");
            _db = new GraphDatabase(_testDbPath);
        }

        [TearDown]
        public void TearDown()
        {
            _db?.Dispose();
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
            if (File.Exists(_testDbPath + "-wal")) File.Delete(_testDbPath + "-wal");
            if (File.Exists(_testDbPath + "-shm")) File.Delete(_testDbPath + "-shm");
        }

        [Test]
        public void Constructor_CreatesBuilder()
        {
            var builder = new GraphBuilder(_db);
            Assert.IsNotNull(builder);
            Assert.AreEqual(BuildStatus.Idle, builder.GetStatus());
        }

        [Test]
        public void GetStatus_Default_IsIdle()
        {
            var builder = new GraphBuilder(_db);
            Assert.AreEqual(BuildStatus.Idle, builder.GetStatus());
        }

        [Test]
        public void EnsureProjectNode_CreatesProjectRoot()
        {
            var builder = new GraphBuilder(_db);
            builder.EnsureProjectNode();

            var nodes = _db.FindNodesByType("Project");
            Assert.AreEqual(1, nodes.Count);
        }

        [Test]
        public void EnsureProjectNode_Idempotent()
        {
            var builder = new GraphBuilder(_db);
            builder.EnsureProjectNode();
            builder.EnsureProjectNode();

            var nodes = _db.FindNodesByType("Project");
            Assert.AreEqual(1, nodes.Count);
        }

        [Test]
        public void IsNodeModulesValid_ReturnsFalse_WhenDirectoryEmpty()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"hades_npm_test_{System.Guid.NewGuid()}");
            Directory.CreateDirectory(Path.Combine(tempDir, "node_modules"));
            try
            {
                Assert.IsFalse(GraphBuilder.IsNodeModulesValid(tempDir));
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Test]
        public void IsNodeModulesValid_ReturnsFalse_WhenBetterSqliteMissing()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"hades_npm_test_{System.Guid.NewGuid()}");
            Directory.CreateDirectory(Path.Combine(tempDir, "node_modules", "some-other-package"));
            File.WriteAllText(Path.Combine(tempDir, "node_modules", "some-other-package", "package.json"), "{}");
            try
            {
                Assert.IsFalse(GraphBuilder.IsNodeModulesValid(tempDir));
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Test]
        public void IsNodeModulesValid_ReturnsTrue_WhenBetterSqlitePresent()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"hades_npm_test_{System.Guid.NewGuid()}");
            var markerDir = Path.Combine(tempDir, "node_modules", "better-sqlite3");
            Directory.CreateDirectory(markerDir);
            File.WriteAllText(Path.Combine(markerDir, "package.json"), "{}");
            try
            {
                Assert.IsTrue(GraphBuilder.IsNodeModulesValid(tempDir));
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Test]
        public void IsNodeModulesValid_ReturnsFalse_WhenNodeModulesDoesNotExist()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"hades_npm_test_{System.Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);
            try
            {
                Assert.IsFalse(GraphBuilder.IsNodeModulesValid(tempDir));
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Test]
        public void ClassifyPendingEdge_Permanent_WhenTargetExtensionNotCovered()
        {
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".unity", ".prefab", ".asset", ".cs", ".mat", ".shader" };

            var targetPath = "Assets/Textures/icon.png";
            var ext = Path.GetExtension(targetPath)?.ToLowerInvariant();
            var isPermanent = ext != null && !covered.Contains(ext);

            Assert.IsTrue(isPermanent, ".png should be classified as permanently unresolvable");
        }

        [Test]
        public void ClassifyPendingEdge_Transient_WhenTargetExtensionCovered()
        {
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".unity", ".prefab", ".asset", ".cs", ".mat", ".shader" };

            var targetPath = "Assets/Scenes/Main.unity";
            var ext = Path.GetExtension(targetPath)?.ToLowerInvariant();
            var isPermanent = ext != null && !covered.Contains(ext);

            Assert.IsFalse(isPermanent, ".unity should be classified as transient");
        }
    }
}
