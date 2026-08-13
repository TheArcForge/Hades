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

    // -----------------------------------------------------------------------------------------
    // ResolveAsync: live-client roots, via an injected IRootsProvider. This is the seam that
    // feeds ToolSupport.ResolveProjectAsync (see ToolSupportTests for the explicit-handle and
    // single-known-project fallback that sit in front of this, and for the "today's explicit
    // error" normalization once this fails). Resolve(IReadOnlyList<string>) above is untouched -
    // it keeps its own documented job (a path already in hand, walked up via FindProjectRoot -
    // startup seeding, a future control-API "add this folder") and its own tests, unchanged.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_RoutesToAUniqueKnownProjectMatch()
    {
        var root = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");
        var (service, router) = NewRouter();
        service.AdoptAndIndex(root);

        var result = await router.ResolveAsync(new FakeRootsProvider(root));

        Assert.True(result.IsResolved);
        Assert.Equal("aaaabbbbccccddddeeeeffff00001111", result.ProductGuid);
        Assert.Null(result.Announcement);
    }

    [Fact]
    public async Task ResolveAsync_RoutesToAKnownProjectWhenRootIsInside()
    {
        var root = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");
        var (service, router) = NewRouter();
        service.AdoptAndIndex(root);

        var result = await router.ResolveAsync(new FakeRootsProvider(Path.Combine(root, "Assets")));

        Assert.True(result.IsResolved);
        Assert.Equal("aaaabbbbccccddddeeeeffff00001111", result.ProductGuid);
    }

    [Fact]
    public async Task ResolveAsync_AutoAdoptsAnUnregisteredUnityRootAndAnnouncesIt()
    {
        var root = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");
        var (service, router) = NewRouter();
        // Deliberately never adopted beforehand - the "brand-new project" case.

        var result = await router.ResolveAsync(new FakeRootsProvider(root));

        Assert.True(result.IsResolved);
        Assert.Equal("aaaabbbbccccddddeeeeffff00001111", result.ProductGuid);
        Assert.Contains(service.KnownProjects(), p => p.ProductGuid == "aaaabbbbccccddddeeeeffff00001111");

        Assert.NotNull(result.Announcement);
        Assert.Contains(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), result.Announcement);
        Assert.Contains("first index", result.Announcement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_DoesNotAnnounceWhenTheRootMatchesAnAlreadyKnownProject()
    {
        var root = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");
        var (service, router) = NewRouter();
        service.AdoptAndIndex(root);

        var result = await router.ResolveAsync(new FakeRootsProvider(root));

        Assert.True(result.IsResolved);
        Assert.Null(result.Announcement);
    }

    [Fact]
    public async Task ResolveAsync_FailsWhenTheClientReportsNoRoots()
    {
        var (_, router) = NewRouter();

        var result = await router.ResolveAsync(new FakeRootsProvider());

        Assert.False(result.IsResolved);
        Assert.Contains("No workspace roots", result.Error);
    }

    [Fact]
    public async Task ResolveAsync_FailsWhenNoReportedRootIsAUnityProject()
    {
        var plain = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(plain);
        _projectRoots.Add(plain);
        var (_, router) = NewRouter();

        var result = await router.ResolveAsync(new FakeRootsProvider(plain));

        Assert.False(result.IsResolved);
        Assert.Contains("No Unity project", result.Error);
    }

    [Fact]
    public async Task ResolveAsync_FailsOnAmbiguousRoots()
    {
        var first = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");
        var second = MakeUnityProject("bbbbccccddddeeeeffff000011112222");
        var (service, router) = NewRouter();
        service.AdoptAndIndex(first);
        service.AdoptAndIndex(second);

        var result = await router.ResolveAsync(new FakeRootsProvider(first, second));

        Assert.False(result.IsResolved);
        Assert.Contains("Ambiguous", result.Error);
    }

    [Fact]
    public async Task ResolveAsync_CanonicalizesATrailingSlashBeforeMatching()
    {
        var root = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");
        var (service, router) = NewRouter();
        service.AdoptAndIndex(root);

        var withTrailingSlash = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var result = await router.ResolveAsync(new FakeRootsProvider(withTrailingSlash));

        Assert.True(result.IsResolved);
        Assert.Equal("aaaabbbbccccddddeeeeffff00001111", result.ProductGuid);
    }

    [Fact]
    public async Task ResolveAsync_CanonicalizesASymlinkedRootBeforeMatching()
    {
        var root = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");
        var (service, router) = NewRouter();
        service.AdoptAndIndex(root);

        var link = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateSymbolicLink(link, root);
        try
        {
            var result = await router.ResolveAsync(new FakeRootsProvider(link));

            Assert.True(result.IsResolved);
            Assert.Equal("aaaabbbbccccddddeeeeffff00001111", result.ProductGuid);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task ResolveAsync_CachesRootsSoARepeatCallDoesNotRoundTripAgain()
    {
        var root = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111");
        var (service, router) = NewRouter();
        service.AdoptAndIndex(root);
        var provider = new FakeRootsProvider(root);

        await router.ResolveAsync(provider);
        await router.ResolveAsync(provider);

        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task ResolveAsync_TimesOutRatherThanHangingWhenTheClientNeverResponds()
    {
        var (service, _) = NewRouter();
        var router = new RootsRouter(service) { RootsRequestTimeout = TimeSpan.FromMilliseconds(50) };

        var result = await router.ResolveAsync(FakeRootsProvider.NeverCompletes());

        Assert.False(result.IsResolved);
    }

    public void Dispose()
    {
        foreach (var dir in _projectRoots.Append(_appRoot))
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}

/// <summary>Stands in for a real MCP client's roots/list response - the fake half of the
/// <see cref="IRootsProvider"/> seam <see cref="RootsRouter.ResolveAsync"/> is tested against, so
/// none of these tests need a live MCP session. Shared by RootsRouterTests and
/// ToolSupportTests (same assembly, same namespace).</summary>
sealed class FakeRootsProvider(params string[] roots) : IRootsProvider
{
    public int CallCount { get; private set; }

    bool _neverCompletes;

    public static FakeRootsProvider NeverCompletes()
    {
        var provider = new FakeRootsProvider();
        provider._neverCompletes = true;
        return provider;
    }

    public async Task<IReadOnlyList<string>> GetRootsAsync(CancellationToken cancellationToken)
    {
        CallCount++;

        if (_neverCompletes)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        return roots;
    }
}
