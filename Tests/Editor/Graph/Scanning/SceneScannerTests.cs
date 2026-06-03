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

        // -----------------------------------------------------------------------
        // Task C4: scene→prefab 'instantiates' edges
        // -----------------------------------------------------------------------

        [Test]
        public void Scan_FixtureScene_WithPrefabInstance_ProducesInstantiatesEdge()
        {
            // When a scene contains at least one prefab instance, SceneScanner must emit
            // at least one 'instantiates' edge from the scene GUID to the source prefab GUID.
            // The fixture scene must have at least one prefab instance for this to pass.
            var scenePath = "Fixtures~/TestProject/Assets/Scenes/Gameplay.unity";
            var fullPath = System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", scenePath);
            if (!System.IO.File.Exists(fullPath)) Assert.Ignore("Fixture not available");

            var result = _scanner.Scan(scenePath);

            var instantiatesEdges = result.Edges.Where(e => e.Type == "instantiates").ToList();
            Assert.Greater(instantiatesEdges.Count, 0,
                "Scene with prefab instances must produce at least one 'instantiates' edge");
        }

        [Test]
        public void Scan_FixtureScene_InstantiatesEdges_SourceIsSceneGuid()
        {
            // Every 'instantiates' edge must originate from the scene's own GUID (the scene
            // asset node), not from a child GameObject GUID.
            var scenePath = "Fixtures~/TestProject/Assets/Scenes/Gameplay.unity";
            var fullPath = System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", scenePath);
            if (!System.IO.File.Exists(fullPath)) Assert.Ignore("Fixture not available");

            var result = _scanner.Scan(scenePath);

            var sceneNode = result.Nodes.FirstOrDefault(n => n.Type == "Scene");
            Assert.IsNotNull(sceneNode, "Expected a Scene node");

            var instantiatesEdges = result.Edges.Where(e => e.Type == "instantiates").ToList();
            if (instantiatesEdges.Count == 0) Assert.Ignore("No prefab instances in fixture scene");

            foreach (var edge in instantiatesEdges)
            {
                Assert.AreEqual(sceneNode.Guid, edge.SourceGuid,
                    $"'instantiates' edge source must be the scene GUID, got '{edge.SourceGuid}'");
            }
        }

        [Test]
        public void Scan_FixtureScene_InstantiatesEdges_DeduplicatedPerPrefab()
        {
            // If the same prefab is instantiated more than once in a scene, SceneScanner
            // must emit exactly ONE 'instantiates' edge to that prefab (not one per instance).
            var scenePath = "Fixtures~/TestProject/Assets/Scenes/Gameplay.unity";
            var fullPath = System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", scenePath);
            if (!System.IO.File.Exists(fullPath)) Assert.Ignore("Fixture not available");

            var result = _scanner.Scan(scenePath);

            var instantiatesEdges = result.Edges.Where(e => e.Type == "instantiates").ToList();
            if (instantiatesEdges.Count == 0) Assert.Ignore("No prefab instances in fixture scene");

            // Group by (source, target) — there must be no duplicates
            var duplicates = instantiatesEdges
                .GroupBy(e => $"{e.SourceGuid}:{e.TargetGuid}")
                .Where(g => g.Count() > 1)
                .ToList();

            Assert.AreEqual(0, duplicates.Count,
                $"Found {duplicates.Count} duplicate 'instantiates' edge(s) — each prefab must appear at most once per scene");
        }
    }
}
