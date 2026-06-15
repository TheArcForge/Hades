using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.PackageManager;
using ArcForge.Hades.Editor.Graph;
using ArcForge.Hades.Editor.Graph.Models;
using ArcForge.Hades.Editor.Graph.Scanning;

namespace ArcForge.Hades.Editor.Tests.Graph
{
    public class MetaAssetLifecycleTests
    {
        static string ResolveScannerJsPath()
        {
            // Locate the package on disk robustly — works for file:/embedded/registry installs.
            var pkg = PackageInfo.FindForAssembly(typeof(MetaAssetTypes).Assembly);
            if (pkg != null)
            {
                var p = Path.Combine(pkg.resolvedPath, "Scanner~", "src", "meta-scanner.js");
                if (File.Exists(p)) return p;
            }
            // Fallback: walk up from the project root looking for the Scanner~ source.
            var projectRoot = Path.GetDirectoryName(UnityEngine.Application.dataPath);
            var embedded = Path.Combine(projectRoot, "Scanner~", "src", "meta-scanner.js");
            return embedded;
        }

        [Test]
        public void MetaAssetTypes_MatchesJsExtensionMap()
        {
            var jsPath = ResolveScannerJsPath();
            Assert.IsTrue(File.Exists(jsPath), $"meta-scanner.js not found at {jsPath}");

            var js = File.ReadAllText(jsPath);
            // Parse "'.ext': 'Type'" pairs from EXTENSION_TO_TYPE.
            var matches = Regex.Matches(js, @"'(\.[a-zA-Z0-9]+)':\s*'([A-Za-z]+)'");
            Assert.Greater(matches.Count, 0, "no extension pairs parsed from JS");

            foreach (Match m in matches)
            {
                var ext = m.Groups[1].Value;
                var type = m.Groups[2].Value;
                Assert.IsTrue(MetaAssetTypes.TryGetType("x" + ext, out var csType),
                    $"C# map missing extension {ext}");
                Assert.AreEqual(type, csType, $"type mismatch for {ext}");
            }

            Assert.AreEqual(matches.Count, MetaAssetTypes.Entries.Count,
                "C# map and JS map have different entry counts");
        }

        [Test]
        public void MetaNode_WithInboundEdge_NotDestroyedByOwnerDelete_OfOtherAsset()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"hades_meta_{System.Guid.NewGuid()}.db");
            var saved = GraphDatabase.Instance;
            var db = new GraphDatabase(dbPath);
            try
            {
                // texture (meta) + material that references it
                var texId = db.InsertNode(new NodeRecord("Texture", "tex_guid")
                    { Name = "hero", Path = "Assets/hero.png", OwnerGuid = "tex_guid" });
                var matId = db.InsertNode(new NodeRecord("Material", "mat_guid")
                    { Name = "HeroMat", Path = "Assets/HeroMat.mat", OwnerGuid = "mat_guid" });
                db.InsertEdge(matId, texId, "uses_texture");

                // Re-scanning the MATERIAL deletes only the material's owned set.
                db.DeleteNodesByOwnerGuid("mat_guid");

                // The texture node MUST survive (it is owned by tex_guid, not mat_guid).
                Assert.IsNotNull(db.FindNodeById(texId), "texture node was destroyed");
            }
            finally
            {
                db.Dispose();
                GraphDatabase.RestoreInstanceForTests(saved);
                foreach (var ext in new[] { "", "-wal", "-shm" })
                    if (File.Exists(dbPath + ext)) File.Delete(dbPath + ext);
            }
        }
    }
}
