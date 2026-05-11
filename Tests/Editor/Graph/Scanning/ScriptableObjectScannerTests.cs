using NUnit.Framework;
using ArcForge.Hades.Editor.Graph.Scanning;
using System.Linq;

namespace ArcForge.Hades.Editor.Tests.Graph.Scanning
{
    public class ScriptableObjectScannerTests
    {
        ScriptableObjectScanner _scanner;

        [SetUp]
        public void SetUp()
        {
            _scanner = new ScriptableObjectScanner();
        }

        [Test]
        public void SupportedExtensions_IncludesAsset()
        {
            Assert.Contains(".asset", _scanner.SupportedExtensions);
        }

        [Test]
        public void Scan_SOInstance_ProducesSONode()
        {
            var guids = UnityEditor.AssetDatabase.FindAssets("t:ScriptableObject", new[] { "Fixtures~/TestProject/Assets" });
            if (guids.Length == 0) Assert.Ignore("No ScriptableObject fixtures available");

            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
            var result = _scanner.Scan(path);

            var soNode = result.Nodes.FirstOrDefault(n => n.Type == "ScriptableObject");
            Assert.IsNotNull(soNode, "Should produce a ScriptableObject node");
        }

        [Test]
        public void Scan_SOInstance_ProducesInstanceOfEdge()
        {
            var guids = UnityEditor.AssetDatabase.FindAssets("t:ScriptableObject", new[] { "Fixtures~/TestProject/Assets" });
            if (guids.Length == 0) Assert.Ignore("No ScriptableObject fixtures available");

            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
            var result = _scanner.Scan(path);

            var instanceOfEdge = result.Edges.FirstOrDefault(e => e.Type == "instance_of");
            Assert.IsNotNull(instanceOfEdge, "Should produce an instance_of edge");
        }
    }
}
