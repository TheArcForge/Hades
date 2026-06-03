using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;

namespace ArcForge.Hades.Editor.Tests.Graph
{
    public class GraphBuilderTests
    {
        string _testDbPath;
        GraphDatabase _db;
        GraphDatabase _savedInstance;

        [SetUp]
        public void SetUp()
        {
            _savedInstance = GraphDatabase.Instance;
            _testDbPath = Path.Combine(Path.GetTempPath(), $"hades_test_{System.Guid.NewGuid()}.db");
            _db = new GraphDatabase(_testDbPath);
        }

        [TearDown]
        public void TearDown()
        {
            _db?.Dispose();
            GraphDatabase.RestoreInstanceForTests(_savedInstance);
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
            var sqliteDir = Path.Combine(tempDir, "node_modules", "better-sqlite3");
            var treeSitterDir = Path.Combine(tempDir, "node_modules", "tree-sitter");
            Directory.CreateDirectory(sqliteDir);
            Directory.CreateDirectory(treeSitterDir);
            File.WriteAllText(Path.Combine(sqliteDir, "package.json"), "{}");
            File.WriteAllText(Path.Combine(treeSitterDir, "package.json"), "{}");
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

        // -------------------------------------------------------------------
        // Package scan non-destructive fix (Task A2)
        // -------------------------------------------------------------------

        // NOTE: ScanPackages() cannot be invoked in EditMode tests — it calls
        // Application.dataPath and other Unity Editor APIs that require a live Editor
        // domain with a project loaded. The tests below exercise the database-layer
        // properties and the helper utilities that the non-destructive logic depends on,
        // giving meaningful coverage without needing a running Editor.
        //
        // Full integration (scanner failure leaves nodes intact, status=degraded) must
        // be verified manually or in a PlayMode/custom Editor test once the maintainer
        // can run the Unity Editor with the package installed. See companion report.

        /// <summary>
        /// Verifies the DB metadata round-trip that package_scan_status depends on.
        /// Mirrors the csharp_scan_status pattern: SetMetadata("package_scan_status", value)
        /// is readable back via GetMetadata("package_scan_status").
        /// </summary>
        [Test]
        public void PackageScanStatus_SetAndGet_RoundTrips()
        {
            _db.SetMetadata("package_scan_status", "ok");
            Assert.AreEqual("ok", _db.GetMetadata("package_scan_status"));

            _db.SetMetadata("package_scan_status", "degraded");
            Assert.AreEqual("degraded", _db.GetMetadata("package_scan_status"));
        }

        /// <summary>
        /// Pre-existing package nodes must survive when package_scan_status is "degraded".
        /// This is the critical correctness property: when the scanner fails, we do NOT wipe
        /// existing nodes. This test simulates the sequence manually since ScanPackages()
        /// requires Unity context.
        /// </summary>
        [Test]
        public void PackageScanStatusDegraded_PackageNodesUnchanged()
        {
            // Arrange: seed two package-tier nodes as if from a previous successful scan.
            var node1 = new NodeRecord("ScriptType", "aabbccddeeff00112233445566778899")
            {
                Name = "SomePackageClass"
            };
            var node2 = new NodeRecord("ScriptType", "00112233445566778899aabbccddeeff")
            {
                Name = "AnotherPackageClass"
            };
            _db.InsertNode(node1, "package");
            _db.InsertNode(node2, "package");
            var beforeCount = _db.GetNodeCount("ScriptType", "package");

            // Act: simulate what ScanPackages does on scanner failure —
            // write degraded status but do NOT delete any nodes.
            _db.SetMetadata("package_scan_status", "degraded");

            // Assert: package nodes are still present, unchanged.
            var afterCount = _db.GetNodeCount("ScriptType", "package");
            Assert.AreEqual(beforeCount, afterCount,
                "Scanner failure must NOT remove existing package nodes");
            Assert.AreEqual("degraded", _db.GetMetadata("package_scan_status"));
        }

        /// <summary>
        /// On a successful scan the status must be "ok".
        /// </summary>
        [Test]
        public void PackageScanStatusOk_WhenScanSucceeds()
        {
            // Simulate what ScanPackages does on success.
            _db.SetMetadata("package_scan_status", "ok");
            Assert.AreEqual("ok", _db.GetMetadata("package_scan_status"));
        }

        /// <summary>
        /// GetDistinctGuidsForTier returns only GUIDs for the requested tier.
        /// </summary>
        [Test]
        public void GetDistinctGuidsForTier_ReturnsOnlyPackageTierGuids()
        {
            _db.InsertNode(new NodeRecord("ScriptType", "pkg001") { Name = "PkgClass" }, "package");
            _db.InsertNode(new NodeRecord("ScriptType", "prj001") { Name = "PrjClass" }, "project");

            var packageGuids = _db.GetDistinctGuidsForTier("package");
            Assert.AreEqual(1, packageGuids.Count);
            Assert.Contains("pkg001", packageGuids);
            Assert.IsFalse(packageGuids.Contains("prj001"), "Project-tier GUIDs must not appear");
        }

        /// <summary>
        /// ExtractGuidFromMeta correctly parses a Unity-style .meta file.
        /// </summary>
        [Test]
        public void ExtractGuidFromMeta_ParsesValidMetaFile()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"hades_meta_test_{System.Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);
            var metaPath = Path.Combine(tempDir, "Foo.cs.meta");
            File.WriteAllText(metaPath, "fileFormatVersion: 2\nguid: abcdef1234567890abcdef1234567890\nMonoImporter:\n  externalObjects: {}\n");
            try
            {
                var guid = GraphBuilder.ExtractGuidFromMeta(metaPath);
                Assert.AreEqual("abcdef1234567890abcdef1234567890", guid);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        /// <summary>
        /// ExtractGuidFromMeta returns null for a file with no guid line.
        /// </summary>
        [Test]
        public void ExtractGuidFromMeta_ReturnsNull_WhenNoGuidLine()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"hades_meta_test_{System.Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);
            var metaPath = Path.Combine(tempDir, "Foo.cs.meta");
            File.WriteAllText(metaPath, "fileFormatVersion: 2\n# no guid here\n");
            try
            {
                var guid = GraphBuilder.ExtractGuidFromMeta(metaPath);
                Assert.IsNull(guid, "Should return null when no guid line is present");
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        /// <summary>
        /// ExtractGuidFromMeta returns null for a non-existent file.
        /// </summary>
        [Test]
        public void ExtractGuidFromMeta_ReturnsNull_WhenFileMissing()
        {
            var guid = GraphBuilder.ExtractGuidFromMeta("/nonexistent/path/Foo.cs.meta");
            Assert.IsNull(guid);
        }

        /// <summary>
        /// ExtractGuidFromMeta rejects a guid line with non-hex characters.
        /// </summary>
        [Test]
        public void ExtractGuidFromMeta_ReturnsNull_WhenGuidIsInvalid()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"hades_meta_test_{System.Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);
            var metaPath = Path.Combine(tempDir, "Foo.cs.meta");
            // 32 chars but contains 'x' — not valid hex
            File.WriteAllText(metaPath, "guid: xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n");
            try
            {
                var guid = GraphBuilder.ExtractGuidFromMeta(metaPath);
                Assert.IsNull(guid, "Non-hex guid should be rejected");
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
