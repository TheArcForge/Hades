using Hades.Core.Graph;
using Hades.Core.Storage;

namespace Hades.Core.Tests.Graph;

/// <summary>
/// Plan 16 Task 1: <see cref="MutationImpact.Analyze"/> must delegate to
/// <see cref="ProjectService.FindReferencesTo"/> — the exact call the <c>find_references_to</c> MCP
/// tool itself makes — rather than re-deriving references a second time. These tests prove that by
/// building a real indexed project (a script referenced by a prefab, a scene nothing references) and
/// comparing <see cref="MutationImpact.Analyze"/> against a direct <c>FindReferencesTo</c> call, then
/// confirm the string-lookup blind spot is always present as data, on both the referenced and clean
/// paths.
/// </summary>
public class MutationImpactTests : IDisposable
{
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";
    const string ScriptGuid = "aaaa1111aaaa1111aaaa1111aaaa1111";
    const string PrefabGuid = "bbbb2222bbbb2222bbbb2222bbbb2222";

    ProjectService NewService() => new(new AppPaths(_appRoot));

    void Write(string relative, string body, string? guid = null)
    {
        var full = Path.Combine(_projectRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
        if (guid is not null) File.WriteAllText(full + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
    }

    /// <summary>A script referenced by one prefab, which one scene instantiates - the smallest
    /// fixture giving both a genuinely-referenced asset (the script) and a genuinely-unreferenced
    /// one (the scene, which nothing points at) to exercise both branches of Analyze. Same shape as
    /// Hades.Server.Tests' FindReferencesToTests fixture, so "matches find_references_to" is
    /// verified on the identical data shape the tool's own tests use.</summary>
    void MakeProjectWithAReferencedScript()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            "  productGUID: 11112222333344445555666677778888\n");

        Write("Assets/PlayerController.cs",
            "using UnityEngine;\npublic class PlayerController : MonoBehaviour { }", ScriptGuid);
        Write("Assets/Player.prefab",
            Header + "--- !u!1 &1\nGameObject:\n  m_Name: Player\n"
            + $"--- !u!114 &2\nMonoBehaviour:\n  m_Script: {{fileID: 11500000, guid: {ScriptGuid}, type: 3}}\n",
            PrefabGuid);
        Write("Assets/Main.unity",
            Header + "--- !u!1001 &100\nPrefabInstance:\n  m_Modification:\n"
            + "    m_TransformParent: {fileID: 200}\n    m_Modifications: []\n    m_RemovedComponents: []\n"
            + $"  m_SourcePrefab: {{fileID: 100100000, guid: {PrefabGuid}, type: 3}}\n",
            "cccc3333cccc3333cccc3333cccc3333");
    }

    [Fact]
    public void Analyze_ForAReferencedScript_MatchesFindReferencesTo()
    {
        MakeProjectWithAReferencedScript();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        var direct = service.FindReferencesTo(project.ProductGuid, "Assets/PlayerController.cs")!;
        var impact = MutationImpact.Analyze(service, project.ProductGuid, "Assets/PlayerController.cs")!;

        Assert.Equal(1, direct.TotalReferences); // sanity: the fixture really is referenced
        Assert.Equal(direct.AssetPath, impact.AssetPath);
        Assert.Equal(direct.Guid, impact.Guid);
        Assert.Equal(direct.TotalReferences, impact.TotalReferences);
        Assert.Equal(direct.ReferencingFileCount, impact.ReferencingFileCount);
        Assert.Equal(direct.Truncated, impact.Truncated);
        Assert.Equal(direct.Files.Count, impact.Files.Count);
        Assert.Equal(direct.Files[0].Path, impact.Files[0].Path);
        Assert.Equal(direct.Files[0].References, impact.Files[0].References);
        Assert.Equal(direct.Files[0].SampleVia, impact.Files[0].SampleVia);
        Assert.Equal(direct.Files[0].Relationships, impact.Files[0].Relationships);
    }

    [Fact]
    public void Analyze_ForAnUnreferencedAsset_ReturnsACleanEmptyResult()
    {
        MakeProjectWithAReferencedScript();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        // Nothing in the fixture references the scene itself.
        var impact = MutationImpact.Analyze(service, project.ProductGuid, "Assets/Main.unity")!;

        Assert.NotNull(impact);
        Assert.Equal(0, impact.TotalReferences);
        Assert.Equal(0, impact.ReferencingFileCount);
        Assert.Empty(impact.Files);
        Assert.False(impact.Truncated);
    }

    [Fact]
    public void Analyze_StatesTheStringLookupBlindSpot_RegardlessOfWhetherReferencesWereFound()
    {
        // The dangerous failure mode: a caller sees TotalReferences > 0, concludes the graph
        // caught everything relevant, and never learns it did not check string-based lookups. The
        // caveat must therefore appear on BOTH the referenced and the clean result, not just one.
        MakeProjectWithAReferencedScript();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        var referenced = MutationImpact.Analyze(service, project.ProductGuid, "Assets/PlayerController.cs")!;
        var unreferenced = MutationImpact.Analyze(service, project.ProductGuid, "Assets/Main.unity")!;

        foreach (var result in new[] { referenced, unreferenced })
        {
            Assert.False(string.IsNullOrWhiteSpace(result.BlindSpot));
            Assert.Contains("GameObject.Find", result.BlindSpot);
            Assert.Contains("CompareTag", result.BlindSpot);
            Assert.Contains("SetTrigger", result.BlindSpot);
            Assert.Contains("Resources.Load", result.BlindSpot);
        }
    }

    [Fact]
    public void Analyze_ReturnsNullForAnAssetPathUnknownToTheGraph()
    {
        // Same null-means-unknown convention as FindReferencesTo, which this delegates to -
        // "unknown" and "known, zero references" must stay distinguishable through this wrapper too.
        MakeProjectWithAReferencedScript();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        Assert.Null(MutationImpact.Analyze(service, project.ProductGuid, "Assets/DoesNotExist.prefab"));
    }

    [Fact]
    public void Analyze_ReturnsNullForAnUnknownProject()
    {
        Assert.Null(MutationImpact.Analyze(
            NewService(), "ffffffffffffffffffffffffffffffff", "Assets/PlayerController.cs"));
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
