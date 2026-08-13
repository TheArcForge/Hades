using Hades.Core;
using Hades.Core.Storage;

namespace Hades.Core.Tests;

public class ProjectServiceTests : IDisposable
{
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    ProjectService NewService() => new(new AppPaths(_appRoot));

    void MakeUnityProject()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            "  productGUID: aaaabbbbccccddddeeeeffff00001111\n");

        var scripts = Path.Combine(_projectRoot, "Assets", "Scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "PlayerController.cs"),
            "using UnityEngine;\npublic class PlayerController : MonoBehaviour { }");
    }

    [Fact]
    public void AdoptAndIndex_MakesTheProjectQueryable()
    {
        MakeUnityProject();
        var service = NewService();

        var project = service.AdoptAndIndex(_projectRoot);

        Assert.NotNull(project);
        var results = service.Search(project!.ProductGuid, "player");
        Assert.Single(results);
        Assert.Equal("PlayerController", results[0].Name);
    }

    [Fact]
    public void AdoptAndIndex_ReturnsNullForNonUnityDirectory()
    {
        Directory.CreateDirectory(_projectRoot);

        Assert.Null(NewService().AdoptAndIndex(_projectRoot));
    }

    [Fact]
    public void GraphLivesInAppStorage_NotInTheProject()
    {
        MakeUnityProject();

        var project = NewService().AdoptAndIndex(_projectRoot)!;

        Assert.True(File.Exists(new AppPaths(_appRoot).GraphDb(project.ProductGuid)));
        Assert.False(Directory.Exists(Path.Combine(_projectRoot, ".hades")));
        Assert.False(Directory.Exists(Path.Combine(_projectRoot, ".arcforge")));
    }

    [Fact]
    public void Summary_ReportsCountsAndIndexState()
    {
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        var summary = service.Summary(project.ProductGuid);

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.TotalNodes);
        Assert.Equal(1, summary.NodesByKind["Class"]);
        Assert.NotNull(summary.LastIndexedUtc);
        Assert.Contains("UNITY_EDITOR", summary.AppliedDefines);
    }

    [Fact]
    public void Summary_AppliedDefinesReflectsProjectVersionAndScriptingDefineSymbols()
    {
        // Plan 15 Task 3 Step 4: get_project_summary must STATE which symbols were applied, so
        // code guarded by anything else is a stated limitation rather than a silent hole.
        MakeUnityProject();
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectVersion.txt"),
            "m_EditorVersion: 6000.3.2f1\nm_EditorVersionWithRevision: 6000.3.2f1 (a9779f353c9b)\n");
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            "  productGUID: aaaabbbbccccddddeeeeffff00001111\n  scriptingDefineSymbols:\n    Standalone: MY_CUSTOM_DEFINE\n");
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        var summary = service.Summary(project.ProductGuid)!;

        Assert.Contains("UNITY_EDITOR", summary.AppliedDefines);
        Assert.Contains("UNITY_6000_3_OR_NEWER", summary.AppliedDefines);
        Assert.Contains("MY_CUSTOM_DEFINE", summary.AppliedDefines);
    }

    [Fact]
    public void Search_ReturnsEmptyForUnknownProject()
    {
        Assert.Empty(NewService().Search("ffffffffffffffffffffffffffffffff", "anything"));
    }

    // ---------------------------------------------------------------- RecordEditorAttached (F9)
    //
    // project.json kept UnityVersion: null and LastSeen == FirstSeen forever after the initial
    // Adopt, even once a real Editor had attached and reported both - project.json is otherwise
    // only ever written at Adopt time and never revisited. EditorListener.Register calls this once
    // a Hello completes (see that method's own doc comment) - this proves the persistence itself;
    // EditorListenerTests proves the attach-time wiring end to end over a real socket.

    [Fact]
    public void RecordEditorAttached_PersistsTheReportedUnityVersionAndBumpsLastSeen()
    {
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;
        Assert.Null(project.UnityVersion);
        var lastSeenAtAdopt = project.LastSeen;

        Thread.Sleep(20); // guarantee a measurable clock difference for LastSeen

        service.RecordEditorAttached(project.ProductGuid, "6000.3.2f1");

        var updated = service.Get(project.ProductGuid);
        Assert.NotNull(updated);
        Assert.Equal("6000.3.2f1", updated!.UnityVersion);
        Assert.Equal(project.FirstSeen, updated.FirstSeen); // FirstSeen never moves
        Assert.True(updated.LastSeen > lastSeenAtAdopt, "LastSeen was never bumped on attach");
    }

    [Fact]
    public void RecordEditorAttached_BlankUnityVersion_StillBumpsLastSeen_ButNeverClobbersAnAlreadyKnownVersion()
    {
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;
        service.RecordEditorAttached(project.ProductGuid, "6000.3.2f1");
        var afterFirstAttach = service.Get(project.ProductGuid)!.LastSeen;

        Thread.Sleep(20);

        service.RecordEditorAttached(project.ProductGuid, "");

        var updated = service.Get(project.ProductGuid);
        Assert.Equal("6000.3.2f1", updated!.UnityVersion);
        Assert.True(updated.LastSeen > afterFirstAttach, "LastSeen was never bumped on the second attach");
    }

    [Fact]
    public void RecordEditorAttached_UnknownProject_IsANoOpAndNeverThrows()
    {
        var service = NewService();

        var exception = Record.Exception(
            () => service.RecordEditorAttached("ffffffffffffffffffffffffffffffff", "6000.3.2f1"));

        Assert.Null(exception);
    }

    // ---------------------------------------------------------------- ExistsOnDisk (F6-honesty)
    //
    // Backs find_references_to's "exists on disk but is an asset type Hades does not index"
    // branch (HadesTools.FindReferencesTo) — the honest distinction from a genuinely absent path,
    // for an asset the graph never resolved (textures, models, audio, fonts, shaders, animation
    // clips: never indexed as nodes, by design).

    [Fact]
    public void ExistsOnDisk_TrueForAFileOnDisk()
    {
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;
        var texturePath = Path.Combine(_projectRoot, "Assets", "Wood.png");
        File.WriteAllBytes(texturePath, [0]);

        Assert.True(service.ExistsOnDisk(project.ProductGuid, "Assets/Wood.png"));
    }

    [Fact]
    public void ExistsOnDisk_TrueWhenOnlyTheMetaFileExists()
    {
        // The literal case F6-honesty names: "the path (or its .meta) exists on disk" — a folder
        // asset (or an asset whose content was deleted but Unity has not re-imported yet) has a
        // real .meta with no sibling content file for File.Exists to find directly.
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;
        File.WriteAllText(Path.Combine(_projectRoot, "Assets", "Wood.png.meta"),
            "fileFormatVersion: 2\nguid: aabbccddeeff00112233445566778899\n");

        Assert.True(service.ExistsOnDisk(project.ProductGuid, "Assets/Wood.png"));
    }

    [Fact]
    public void ExistsOnDisk_FalseForAPathThatTrulyDoesNotExist()
    {
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        Assert.False(service.ExistsOnDisk(project.ProductGuid, "Assets/DoesNotExist.png"));
    }

    [Fact]
    public void ExistsOnDisk_FalseForAnUnknownProject()
    {
        Assert.False(NewService().ExistsOnDisk("ffffffffffffffffffffffffffffffff", "Assets/Wood.png"));
    }

    [Fact]
    public void ExistsOnDisk_FalseRatherThanThrowingForAPathThatEscapesTheProject()
    {
        MakeUnityProject();
        var service = NewService();
        var project = service.AdoptAndIndex(_projectRoot)!;

        Assert.False(service.ExistsOnDisk(project.ProductGuid, "../../../../etc/passwd"));
    }

    readonly List<string> _adoptedProjectRoots = [];

    [Fact]
    public async Task EnsureIndexed_IsSafeUnderHighConcurrencyAcrossManyProjects()
    {
        // Dictionary<TKey,TValue> (the type _lastIndexed used before this fix) is not safe for
        // concurrent writers, even across different keys — internal bucket/resize state races.
        // Reindex() writes _lastIndexed on every call, and nothing serialises EnsureIndexed for
        // DIFFERENT projects, which is exactly what a server fielding concurrent requests across
        // multiple roots would do. Measured pre-fix: InvalidOperationException and/or silently
        // lost writes under enough concurrent projects and threads.
        var service = NewService();
        const int projectCount = 200;
        var guids = new string[projectCount];

        for (var i = 0; i < projectCount; i++)
        {
            var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            var guid = $"{i:x8}bbbbccccddddeeeeffff0000";
            File.WriteAllText(Path.Combine(root, "ProjectSettings", "ProjectSettings.asset"),
                $"  productGUID: {guid}\n");
            guids[i] = guid;
            _adoptedProjectRoots.Add(root);
            Assert.NotNull(service.Adopt(root));
        }

        await Parallel.ForAsync(0, projectCount * 4, new ParallelOptions { MaxDegreeOfParallelism = 32 },
            (i, _) => { service.EnsureIndexed(guids[i % projectCount]); return ValueTask.CompletedTask; });

        foreach (var guid in guids)
            Assert.NotNull(service.Summary(guid)?.LastIndexedUtc);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _appRoot, _projectRoot }.Concat(_adoptedProjectRoots))
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
