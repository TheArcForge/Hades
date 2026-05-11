using NUnit.Framework;
using ArcForge.Hades.Editor.Graph.Scanning;
using System.Linq;

namespace ArcForge.Hades.Editor.Tests.Graph.Scanning
{
    public class PrefabScannerTests
    {
        PrefabScanner _scanner;

        [SetUp]
        public void SetUp()
        {
            _scanner = new PrefabScanner();
        }

        [Test]
        public void SupportedExtensions_IncludesPrefab()
        {
            Assert.Contains(".prefab", _scanner.SupportedExtensions);
        }

        [Test]
        public void Scan_FixturePlayerPrefab_ProducesPrefabNode()
        {
            var path = "Fixtures~/TestProject/Assets/Prefabs/Player.prefab";
            var fullPath = System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", path);
            if (!System.IO.File.Exists(fullPath)) Assert.Ignore("Fixture not available");

            var result = _scanner.Scan(path);

            var prefabNode = result.Nodes.FirstOrDefault(n => n.Type == "Prefab" || n.Type == "PrefabVariant");
            Assert.IsNotNull(prefabNode, "Should produce a Prefab or PrefabVariant node");
        }

        [Test]
        public void Scan_FixturePlayerPrefab_ProducesGameObjects()
        {
            var path = "Fixtures~/TestProject/Assets/Prefabs/Player.prefab";
            var fullPath = System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", path);
            if (!System.IO.File.Exists(fullPath)) Assert.Ignore("Fixture not available");

            var result = _scanner.Scan(path);

            var gameObjects = result.Nodes.Where(n => n.Type == "GameObject").ToList();
            Assert.Greater(gameObjects.Count, 0);
        }

        [Test]
        public void Scan_FixturePlayerPrefab_ProducesComponents()
        {
            var path = "Fixtures~/TestProject/Assets/Prefabs/Player.prefab";
            var fullPath = System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", path);
            if (!System.IO.File.Exists(fullPath)) Assert.Ignore("Fixture not available");

            var result = _scanner.Scan(path);

            var components = result.Nodes.Where(n => n.Type == "Component").ToList();
            Assert.Greater(components.Count, 0);
        }
    }
}
