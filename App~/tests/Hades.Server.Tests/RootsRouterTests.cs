using Hades.Core;
using Hades.Core.Storage;
using Hades.Server.Mcp;

namespace Hades.Server.Tests;

public class RootsRouterTests : IDisposable
{
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly List<string> _projectRoots = [];

    string MakeUnityProject(string guid)
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _projectRoots.Add(root);
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(root, "ProjectSettings", "ProjectSettings.asset"),
            $"  productGUID: {guid}\n");
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        return root;
    }

    (ProjectService Service, RootsRouter Router) NewRouter()
    {
        var service = new ProjectService(new AppPaths(_appRoot));
        return (service, new RootsRouter(service));
    }

    [Fact]
    public void ResolvesAnExactProjectRoot()
    {
        var root = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");
        var (service, router) = NewRouter();
        service.AdoptAndIndex(root);

        var result = router.Resolve([root]);

        Assert.True(result.IsResolved);
        Assert.Equal("aaaabbbbccccddddeeeeffff00001111", result.ProductGuid);
    }

    [Fact]
    public void AdoptsAnUnknownButValidUnityProject()
    {
        var root = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");
        var (_, router) = NewRouter();

        var result = router.Resolve([root]);

        Assert.True(result.IsResolved);
        Assert.Equal("aaaabbbbccccddddeeeeffff00001111", result.ProductGuid);
    }

    [Fact]
    public void ResolvesARootInsideTheProject()
    {
        var root = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");
        var (service, router) = NewRouter();
        service.AdoptAndIndex(root);

        var result = router.Resolve([Path.Combine(root, "Assets")]);

        Assert.True(result.IsResolved);
        Assert.Equal("aaaabbbbccccddddeeeeffff00001111", result.ProductGuid);
    }

    [Fact]
    public void FailsWhenNoRootIsAUnityProject()
    {
        var plain = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(plain);
        _projectRoots.Add(plain);
        var (_, router) = NewRouter();

        var result = router.Resolve([plain]);

        Assert.False(result.IsResolved);
        Assert.Contains("No Unity project", result.Error);
    }

    [Fact]
    public void FailsWithNoRootsAtAll()
    {
        Assert.False(NewRouter().Router.Resolve([]).IsResolved);
    }

    [Fact]
    public void DoesNotGuessWhenTwoRootsResolveToDifferentProjects()
    {
        // The v1.2 hub's "if exactly one instance exists, route to it" fallback is deliberately
        // not reproduced: an ambiguous request must name the candidates, not guess.
        var first = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");
        var second = MakeUnityProject("bbbbccccddddeeeeffff000011112222");
        var (service, router) = NewRouter();
        service.AdoptAndIndex(first);
        service.AdoptAndIndex(second);

        var result = router.Resolve([first, second]);

        Assert.False(result.IsResolved);
        Assert.Contains("Ambiguous", result.Error);
        Assert.Contains(Path.GetFileName(first), result.Error);
        Assert.Contains(Path.GetFileName(second), result.Error);
    }

    [Fact]
    public void ResolvesWhenSeveralRootsPointAtTheSameProject()
    {
        var root = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");
        var (service, router) = NewRouter();
        service.AdoptAndIndex(root);

        Assert.True(router.Resolve([root, Path.Combine(root, "Assets")]).IsResolved);
    }

    public void Dispose()
    {
        foreach (var dir in _projectRoots.Append(_appRoot))
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
