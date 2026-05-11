// Tests/Editor/Graph/GraphDatabaseSchemaTests.cs
using System.IO;
using NUnit.Framework;
using ArcForge.Hades.Editor.Graph;

namespace ArcForge.Hades.Editor.Tests.Graph
{
    public class GraphDatabaseSchemaTests
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
        public void Constructor_CreatesDatabase()
        {
            Assert.IsTrue(File.Exists(_testDbPath));
        }

        [Test]
        public void Schema_NodesTableExists()
        {
            var exists = _db.TableExists("nodes");
            Assert.IsTrue(exists);
        }

        [Test]
        public void Schema_EdgesTableExists()
        {
            var exists = _db.TableExists("edges");
            Assert.IsTrue(exists);
        }

        [Test]
        public void Schema_ScannedAssetsTableExists()
        {
            var exists = _db.TableExists("scanned_assets");
            Assert.IsTrue(exists);
        }

        [Test]
        public void Schema_GraphMetadataTableExists()
        {
            var exists = _db.TableExists("graph_metadata");
            Assert.IsTrue(exists);
        }

        [Test]
        public void Schema_SchemaVersionTableExists()
        {
            var exists = _db.TableExists("schema_version");
            Assert.IsTrue(exists);
        }

        [Test]
        public void Pragmas_WalModeEnabled()
        {
            var journalMode = _db.ExecuteScalar<string>("PRAGMA journal_mode;");
            Assert.AreEqual("wal", journalMode.ToLower());
        }

        [Test]
        public void Pragmas_ForeignKeysEnabled()
        {
            var fk = _db.ExecuteScalar<long>("PRAGMA foreign_keys;");
            Assert.AreEqual(1L, fk);
        }

        [Test]
        public void SchemaVersion_IsRecorded()
        {
            var version = _db.ExecuteScalar<long>("SELECT MAX(version) FROM schema_version;");
            Assert.AreEqual(1L, version);
        }

        [Test]
        public void SetMetadata_AndGetMetadata()
        {
            _db.SetMetadata("test_key", "test_value");
            var result = _db.GetMetadata("test_key");
            Assert.AreEqual("test_value", result);
        }

        [Test]
        public void GetMetadata_Missing_ReturnsNull()
        {
            var result = _db.GetMetadata("nonexistent");
            Assert.IsNull(result);
        }

        [Test]
        public void IsRebuildInProgress_Default_ReturnsFalse()
        {
            Assert.IsFalse(_db.IsRebuildInProgress());
        }

        [Test]
        public void SetCurrentOperation_ThenIsRebuildInProgress_ReturnsTrue()
        {
            _db.SetCurrentOperation("rebuild", new string[] { "guid1" });
            Assert.IsTrue(_db.IsRebuildInProgress());
        }

        [Test]
        public void ClearCurrentOperation_ThenIsRebuildInProgress_ReturnsFalse()
        {
            _db.SetCurrentOperation("rebuild", new string[] { "guid1" });
            _db.ClearCurrentOperation();
            Assert.IsFalse(_db.IsRebuildInProgress());
        }
    }
}
