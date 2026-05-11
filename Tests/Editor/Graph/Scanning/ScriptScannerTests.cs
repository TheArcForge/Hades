// Tests/Editor/Graph/Scanning/ScriptScannerTests.cs
using NUnit.Framework;
using ArcForge.Hades.Editor.Graph.Scanning;
using System.IO;
using System.Linq;

namespace ArcForge.Hades.Editor.Tests.Graph.Scanning
{
    public class ScriptScannerTests
    {
        ScriptScanner _scanner;
        string _fixtureDir;

        [SetUp]
        public void SetUp()
        {
            _scanner = new ScriptScanner();
            _fixtureDir = Path.GetFullPath(
                Path.Combine(UnityEngine.Application.dataPath, "..", "Fixtures~", "TestProject", "Assets", "Scripts"));
        }

        [Test]
        public void SupportedExtensions_IncludesCs()
        {
            Assert.Contains(".cs", _scanner.SupportedExtensions);
        }

        [Test]
        public void Version_IsPositive()
        {
            Assert.Greater(_scanner.Version, 0);
        }

        [Test]
        public void Scan_PlayerController_ProducesScriptNode()
        {
            var path = Path.Combine(_fixtureDir, "Player", "PlayerController.cs");
            if (!File.Exists(path)) Assert.Ignore("Fixture not available");

            var result = _scanner.Scan(path);

            var scriptNode = result.Nodes.FirstOrDefault(n => n.Type == "Script");
            Assert.IsNotNull(scriptNode, "Should produce a Script node");
            Assert.AreEqual("PlayerController.cs", scriptNode.Name);
        }

        [Test]
        public void Scan_PlayerController_ProducesScriptTypeNode()
        {
            var path = Path.Combine(_fixtureDir, "Player", "PlayerController.cs");
            if (!File.Exists(path)) Assert.Ignore("Fixture not available");

            var result = _scanner.Scan(path);

            var typeNode = result.Nodes.FirstOrDefault(n => n.Type == "ScriptType" && n.Name == "PlayerController");
            Assert.IsNotNull(typeNode, "Should produce a ScriptType node for PlayerController");
        }

        [Test]
        public void Scan_PlayerController_DetectsMonoBehaviourBase()
        {
            var path = Path.Combine(_fixtureDir, "Player", "PlayerController.cs");
            if (!File.Exists(path)) Assert.Ignore("Fixture not available");

            var result = _scanner.Scan(path);

            var typeNode = result.Nodes.First(n => n.Type == "ScriptType" && n.Name == "PlayerController");
            Assert.IsTrue(typeNode.Properties.ContainsKey("base_type"));
            Assert.AreEqual("MonoBehaviour", typeNode.Properties["base_type"]);
        }

        [Test]
        public void Scan_PlayerController_ExtractsMethods()
        {
            var path = Path.Combine(_fixtureDir, "Player", "PlayerController.cs");
            if (!File.Exists(path)) Assert.Ignore("Fixture not available");

            var result = _scanner.Scan(path);

            var methods = result.Nodes.Where(n => n.Type == "ScriptMethod").ToList();
            Assert.GreaterOrEqual(methods.Count, 2);
            Assert.IsTrue(methods.Any(m => m.Name == "Move"), "Should find Move method");
        }

        [Test]
        public void Scan_PlayerController_ProducesDefinesEdges()
        {
            var path = Path.Combine(_fixtureDir, "Player", "PlayerController.cs");
            if (!File.Exists(path)) Assert.Ignore("Fixture not available");

            var result = _scanner.Scan(path);

            var definesEdges = result.Edges.Where(e => e.Type == "defines").ToList();
            Assert.GreaterOrEqual(definesEdges.Count, 1, "Script should define at least one type");
        }

        [Test]
        public void Scan_EventChannel_DetectsScriptableObjectBase()
        {
            var path = Path.Combine(_fixtureDir, "Systems", "EventChannel.cs");
            if (!File.Exists(path)) Assert.Ignore("Fixture not available");

            var result = _scanner.Scan(path);

            var typeNode = result.Nodes.First(n => n.Type == "ScriptType" && n.Name == "EventChannel");
            Assert.AreEqual("ScriptableObject", typeNode.Properties["base_type"]);
        }

        [Test]
        public void Scan_PlayerController_DetectsNamespace()
        {
            var path = Path.Combine(_fixtureDir, "Player", "PlayerController.cs");
            if (!File.Exists(path)) Assert.Ignore("Fixture not available");

            var result = _scanner.Scan(path);

            var typeNode = result.Nodes.First(n => n.Type == "ScriptType" && n.Name == "PlayerController");
            Assert.AreEqual("TestProject.Player", typeNode.Properties["namespace"]);
        }

        [Test]
        public void Scan_Singleton_DetectsGenericBase()
        {
            var path = Path.Combine(_fixtureDir, "Utilities", "Singleton.cs");
            if (!File.Exists(path)) Assert.Ignore("Fixture not available");

            var result = _scanner.Scan(path);

            var typeNode = result.Nodes.FirstOrDefault(n => n.Type == "ScriptType");
            Assert.IsNotNull(typeNode);
        }

        [Test]
        public void Scan_EmptyOrInvalidFile_ReturnsEmptyResult()
        {
            var tempFile = Path.GetTempFileName();
            File.WriteAllText(tempFile, "// just a comment, no types");
            try
            {
                var result = _scanner.Scan(tempFile);
                Assert.IsNotNull(result);
                Assert.AreEqual(0, result.Nodes.Count(n => n.Type == "ScriptType"));
            }
            finally
            {
                File.Delete(tempFile);
            }
        }
    }
}
