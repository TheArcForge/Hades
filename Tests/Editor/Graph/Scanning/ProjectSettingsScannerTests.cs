using NUnit.Framework;
using ArcForge.Hades.Editor.Graph.Scanning;
using System.Linq;

namespace ArcForge.Hades.Editor.Tests.Graph.Scanning
{
    public class ProjectSettingsScannerTests
    {
        ProjectSettingsScanner _scanner;

        [SetUp]
        public void SetUp()
        {
            _scanner = new ProjectSettingsScanner();
        }

        [Test]
        public void ScannerName_IsCorrect()
        {
            Assert.AreEqual("ProjectSettingsScanner", _scanner.ScannerName);
        }

        [Test]
        public void Scan_ProducesBuildSettingsNode()
        {
            var result = _scanner.Scan("ProjectSettings/EditorBuildSettings.asset");
            var node = result.Nodes.FirstOrDefault(n => n.Type == "BuildSettings");
            Assert.IsNotNull(node, "Should produce a BuildSettings node");
        }

        [Test]
        public void Scan_BuildSettings_HasSceneList()
        {
            var result = _scanner.Scan("ProjectSettings/EditorBuildSettings.asset");
            var node = result.Nodes.First(n => n.Type == "BuildSettings");
            Assert.IsTrue(node.Properties.ContainsKey("scene_count"));
        }

        [Test]
        public void Scan_ProducesRenderPipelineNode_IfConfigured()
        {
            var result = _scanner.Scan("ProjectSettings/GraphicsSettings.asset");
            Assert.IsNotNull(result);
        }
    }
}
