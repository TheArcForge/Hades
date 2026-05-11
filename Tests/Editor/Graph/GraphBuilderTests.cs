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
    }
}
