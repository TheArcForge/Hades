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
    }

    [Fact]
    public void Search_ReturnsEmptyForUnknownProject()
    {
        Assert.Empty(NewService().Search("ffffffffffffffffffffffffffffffff", "anything"));
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
