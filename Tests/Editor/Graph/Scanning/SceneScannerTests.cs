using NUnit.Framework;
using ArcForge.Hades.Editor.Graph.Scanning;
using System.Linq;

namespace ArcForge.Hades.Editor.Tests.Graph.Scanning
{
    public class SceneScannerTests
    {
        SceneScanner _scanner;

        [SetUp]
        public void SetUp()
        {
            _scanner = new SceneScanner();
        }

        [Test]
        public void SupportedExtensions_IncludesUnity()
        {
            Assert.Contains(".unity", _scanner.SupportedExtensions);
        }

        [Test]
        public void Scan_FixtureScene_ProducesSceneNode()
        {
            var scenePath = "Fixtures~/TestProject/Assets/Scenes/MainMenu.unity";
            var fullPath = System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", scenePath);
            if (!System.IO.File.Exists(fullPath)) Assert.Ignore("Fixture not available");

            var result = _scanner.Scan(scenePath);

            var sceneNode = result.Nodes.FirstOrDefault(n => n.Type == "Scene");
            Assert.IsNotNull(sceneNode, "Should produce a Scene node");
        }

        [Test]
        public void Scan_FixtureScene_ProducesGameObjectNodes()
        {
            var scenePath = "Fixtures~/TestProject/Assets/Scenes/MainMenu.unity";
            var fullPath = System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", scenePath);
            if (!System.IO.File.Exists(fullPath)) Assert.Ignore("Fixture not available");

            var result = _scanner.Scan(scenePath);

            var gameObjects = result.Nodes.Where(n => n.Type == "GameObject").ToList();
            Assert.Greater(gameObjects.Count, 0, "Should produce GameObject nodes");
        }

        [Test]
        public void Scan_FixtureScene_ProducesContainsEdges()
        {
            var scenePath = "Fixtures~/TestProject/Assets/Scenes/MainMenu.unity";
            var fullPath = System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", scenePath);
            if (!System.IO.File.Exists(fullPath)) Assert.Ignore("Fixture not available");

            var result = _scanner.Scan(scenePath);

            var containsEdges = result.Edges.Where(e => e.Type == "contains").ToList();
            Assert.Greater(containsEdges.Count, 0, "Should produce contains edges");
        }

        [Test]
        public void Scan_FixtureScene_ProducesComponentNodes()
        {
            var scenePath = "Fixtures~/TestProject/Assets/Scenes/Gameplay.unity";
            var fullPath = System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", scenePath);
            if (!System.IO.File.Exists(fullPath)) Assert.Ignore("Fixture not available");

            var result = _scanner.Scan(scenePath);

            var components = result.Nodes.Where(n => n.Type == "Component").ToList();
            Assert.Greater(components.Count, 0, "Should produce Component nodes");
        }
    }
}
