using NUnit.Framework;
using ArcForge.Hades.Editor.Graph.Scanning;
using System.Linq;

namespace ArcForge.Hades.Editor.Tests.Graph.Scanning
{
    public class MaterialShaderScannerTests
    {
        [Test]
        public void MaterialScanner_SupportedExtensions_IncludesMat()
        {
            var scanner = new MaterialScanner();
            Assert.Contains(".mat", scanner.SupportedExtensions);
        }

        [Test]
        public void ShaderScanner_SupportedExtensions_IncludesShader()
        {
            var scanner = new ShaderScanner();
            Assert.Contains(".shader", scanner.SupportedExtensions);
            Assert.Contains(".shadergraph", scanner.SupportedExtensions);
        }

        [Test]
        public void MaterialScanner_Scan_ProducesMaterialNode()
        {
            var guids = UnityEditor.AssetDatabase.FindAssets("t:Material", new[] { "Fixtures~/TestProject/Assets" });
            if (guids.Length == 0) Assert.Ignore("No material fixtures available");

            var scanner = new MaterialScanner();
            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
            var result = scanner.Scan(path);

            Assert.IsTrue(result.Nodes.Any(n => n.Type == "Material"));
        }
    }
}
