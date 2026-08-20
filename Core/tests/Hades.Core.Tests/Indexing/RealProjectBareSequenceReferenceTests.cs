using Hades.Core;
using Hades.Core.Graph;
using Hades.Core.Storage;

namespace Hades.Core.Tests.Indexing;

/// <summary>
/// Ground-truth verification for the bare-sequence-element reference fix (UnityYamlReader.
/// ReadDocument's containerIsSequence branch) against the real Hades-Unity-Client corpus, rather
/// than a synthetic fixture — same directory-exists-guard pattern as Hades.Server.Tests' own
/// RealProject*SmokeTest files: a local sanity check, skipped rather than failing on a machine
/// without this checkout. Uses an isolated <see cref="AppPaths"/> root (the same isolation
/// HADES_HOME gives the real app — see Program.cs), so indexing writes only to a temp directory;
/// Hades-Unity-Client itself is read from disk and never written to.
///
/// Every path/fileId/guid/line number referenced below was read by hand from the real files
/// first (Assets/Demo/Prefabs/Enemy.prefab, Assets/Settings/PC_Renderer.asset, Assets/Settings/
/// PC_RPAsset.asset, Assets/Scenes/SampleScene.unity) — none of it was derived from the fixed
/// reader's own output.
/// </summary>
public class RealProjectBareSequenceReferenceTests : IDisposable
{
    const string RealProject = "/Users/mike/Projects/Hades-Unity-Client";
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    ProjectService NewService() => new(new AppPaths(_appRoot));

    [Fact]
    public void EnemyPrefabsMeshRendererNowReferencesItsMaterial()
    {
        // Assets/Demo/Prefabs/Enemy.prefab:93 — "m_Materials:\n  - {fileID: 2100000, guid:
        // 46ed38f068ed44823b27133f2ce8e23c, type: 2}" under the MeshRenderer at fileId
        // 3158295675534926868. Before this fix: dropped entirely — SequenceStart consumes the
        // pending key, so the bare flow mapping arrives with no key and fell through unread. The
        // bug report's own repro: find_references_to on M_Enemy.mat returned totalReferences: 0.
        if (!Directory.Exists(RealProject)) return;

        var service = NewService();
        var project = service.AdoptAndIndex(RealProject);
        Assert.NotNull(project);

        using var db = GraphDatabase.Open(service.Paths.GraphDb(project!.ProductGuid));

        const long meshRenderer = 3158295675534926868;
        const string materialGuid = "46ed38f068ed44823b27133f2ce8e23c";

        var edges = db.EdgesFrom("Assets/Demo/Prefabs/Enemy.prefab", meshRenderer);
        Assert.Contains(edges, e => e.Kind == "references" && e.ToGuid == materialGuid
            && e.ToFileId == 2100000 && e.PropertyPath == "m_Materials");

        // find_references_to's own query path — the exact tool the bug report measured at
        // totalReferences: 0 against this material.
        var referencingFiles = db.ReferencingFiles(materialGuid, excludePath: null, limit: 100);
        Assert.Contains(referencingFiles, f => f.Path == "Assets/Demo/Prefabs/Enemy.prefab");
        Assert.True(db.CountReferencesTo(materialGuid, excludePath: null) >= 1);
    }

    [Fact]
    public void UrpPipelineAssetNowReferencesItsRendererThroughTheExternalGuidArrayElement()
    {
        // Assets/Settings/PC_RPAsset.asset:20 — "m_RendererDataList:\n  - {fileID: 11400000,
        // guid: f288ae1f4751b564a96ac7587541f7a2, type: 2}", where that guid is PC_Renderer.
        // asset's own .meta guid (Assets/Settings/PC_Renderer.asset.meta). A cross-file "URP
        // renderer list" reference — exactly the class of win this fix targets.
        if (!Directory.Exists(RealProject)) return;

        var service = NewService();
        var project = service.AdoptAndIndex(RealProject);
        using var db = GraphDatabase.Open(service.Paths.GraphDb(project!.ProductGuid));

        var edges = db.EdgesFrom("Assets/Settings/PC_RPAsset.asset", 11400000);
        Assert.Contains(edges, e => e.Kind == "references"
            && e.ToGuid == "f288ae1f4751b564a96ac7587541f7a2"
            && e.PropertyPath == "m_RendererDataList");
    }

    [Fact]
    public void RendererFeatureTextureArrayCapturesAllSevenExternalElements()
    {
        // Assets/Settings/PC_Renderer.asset's nested ScreenSpaceAmbientOcclusion object (fileId
        // 7833122117494664109, lines 61-95): m_BlueNoise256Textures is seven bare flow mappings,
        // each a distinct external texture guid — a multi-element array, not the single-element
        // cases above.
        if (!Directory.Exists(RealProject)) return;

        var service = NewService();
        var project = service.AdoptAndIndex(RealProject);
        using var db = GraphDatabase.Open(service.Paths.GraphDb(project!.ProductGuid));

        var edges = db.EdgesFrom("Assets/Settings/PC_Renderer.asset", 7833122117494664109)
            .Where(e => e.PropertyPath == "m_BlueNoise256Textures")
            .ToList();

        string[] expectedGuids =
        [
            "36f118343fc974119bee3d09e2111500", "4b7b083e6b6734e8bb2838b0b50a0bc8",
            "c06cc21c692f94f5fb5206247191eeee", "cb76dd40fa7654f9587f6a344f125c9a",
            "e32226222ff144b24bf3a5a451de54bc", "3302065f671a8450b82c9ddf07426f3a",
            "56a77a3e8d64f47b6afe9e3c95cb57d5",
        ];

        Assert.Equal(7, edges.Count);
        foreach (var guid in expectedGuids)
            Assert.Contains(edges, e => e.ToGuid == guid);
    }

    [Fact]
    public void LocalGuidLessArrayElementsStayUncaptured()
    {
        // The other half of the design decision: PC_Renderer.asset's OWN m_RendererFeatures
        // (line 37, "- {fileID: 7833122117494664109}", no guid — a same-file sub-object) and
        // SampleScene.unity's SceneRoots.m_Roots (three same-file Transform fileIds, lines
        // 439-441) are both the bare-sequence-element shape this fix now reads, but guid-less —
        // the same class as m_Children, deliberately not turned into edges (see
        // UnityYamlReader.ReadDocument's own comment on this branch). Confirms the fix does not
        // flood the graph with intra-file structural back-references.
        if (!Directory.Exists(RealProject)) return;

        var service = NewService();
        var project = service.AdoptAndIndex(RealProject);
        using var db = GraphDatabase.Open(service.Paths.GraphDb(project!.ProductGuid));

        var rendererEdges = db.EdgesFrom("Assets/Settings/PC_Renderer.asset", 11400000);
        Assert.DoesNotContain(rendererEdges, e => e.PropertyPath == "m_RendererFeatures");

        var sceneRootsEdges = db.EdgesFromPath("Assets/Scenes/SampleScene.unity")
            .Where(e => e.PropertyPath == "m_Roots").ToList();
        Assert.Empty(sceneRootsEdges);
    }

    [Fact]
    public void ReportsProjectScaleForContext()
    {
        // Not a correctness assertion (the four tests above already are) — just prints the
        // corpus's overall scale alongside this fix's own targeted counts, so the report has
        // real numbers rather than guesses. TotalEdges includes every "references"/"instance_of"/
        // "corresponds_to" edge across whatever ResolveScanRoots pulls in for this project
        // (Assets/, Packages/, and any local "file:" package — see ProjectWalker's own doc
        // comment), not just Assets/ — a broader scope than a plain grep over Assets/ alone.
        if (!Directory.Exists(RealProject)) return;

        var service = NewService();
        var project = service.AdoptAndIndex(RealProject);
        using var db = GraphDatabase.Open(service.Paths.GraphDb(project!.ProductGuid));

        Console.WriteLine($"[ground truth] TotalNodes={db.TotalNodes()} TotalEdges={db.TotalEdges()}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_appRoot)) Directory.Delete(_appRoot, recursive: true);
    }
}
