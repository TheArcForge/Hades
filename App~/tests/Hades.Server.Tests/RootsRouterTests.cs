using System.Reflection;
using Hades.Core;
using Hades.Core.Projects;
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

    /// <summary>
    /// Test-only realpath oracle - invokes the actual (internal) <see cref="ProjectStore.Canonicalize"/>
    /// via reflection rather than re-implementing it, so this helper can never drift from what
    /// RootsRouter's own Canonicalize (a thin delegate to it) actually does. Needed by any fixture
    /// that builds a symlink's own TARGET from <see cref="Path.GetTempPath"/>: that path itself sits
    /// under a symlinked ancestor on macOS (<c>/var</c> -&gt; <c>/private/var</c>), and
    /// <see cref="ProjectStore"/>'s single-pass, component-by-component resolution does not re-walk
    /// a just-substituted symlink target's own ancestors - so a symlink created with a non-canonical
    /// target only gets PARTIALLY resolved. Pre-resolving with this helper before a symlink's target
    /// is ever written keeps every fixture's target already-canonical, side-stepping that requirement
    /// entirely - same technique ProjectStoreTests's own RealPath helper and the fixture it feeds
    /// (Adopt_ThroughAnIntermediateSymlinkedDirectory_ResolvesTheFullChain_SoBothSpellingsConvergeOnOneRow)
    /// already use for the identical reason.
    /// </summary>
    static string RealPath(string path)
    {
        var method = typeof(ProjectStore).GetMethod("Canonicalize", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ProjectStore.Canonicalize not found — has it been renamed?");

        return (string)method.Invoke(null, [path])!;
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
    public async Task ResolveAsync_SkipsAKnownProjectWithACorruptedStoredPath_RatherThanThrowing()
    {
        // The bug this guards: MatchAgainstKnownProjects used to canonicalize each KNOWN
        // project's own stored Path with no guard, unlike the reported root's own Canonicalize
        // call a few lines above it in ResolveAsync (the try/catch around `canonical =
        // Canonicalize(raw)`). A corrupted project.json - Path == "" here - makes
        // Path.GetFullPath throw ArgumentException, which used to propagate straight out of
        // ResolveAsync instead of just meaning "this project does not match". Written directly
        // via a second ProjectStore on the SAME AppPaths (never through Adopt, which always
        // canonicalizes and could never itself produce this) to simulate exactly that corruption.
        var (_, router) = NewRouter();
        new ProjectStore(new AppPaths(_appRoot)).Save(new UnityProject
        {
            ProductGuid = "aaaabbbbccccddddeeeeffff00009999",
            Path = "",
            Name = "Corrupt",
        });

        var plain = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(plain);
        _projectRoots.Add(plain);

        var result = await router.ResolveAsync(new FakeRootsProvider(plain));

        Assert.False(result.IsResolved);
        Assert.Contains("No Unity project", result.Error);
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
    public async Task ResolveAsync_MatchesAKnownProjectReportedThroughAnIntermediateSymlinkedAncestor()
    {
        // The bug this guards: RootsRouter's own Canonicalize used to resolve only the LEAF
        // component's own symlink, so a reported root sitting under a symlinked ANCESTOR - macOS's
        // own /tmp -> /private/tmp is exactly this shape, since /tmp is never the leaf of a project
        // path like /tmp/MyProj - canonicalized to a spelling that never matched the known
        // project's own fully-resolved stored Path (ProjectStore.Adopt already resolves the FULL
        // chain). The already-known project then looked unmatched and got silently re-adopted and
        // re-announced as brand new on every call - unlike
        // ResolveAsync_CanonicalizesASymlinkedRootBeforeMatching above, where the symlink IS the
        // reported root's own leaf and leaf-only resolution already handled it. Built from scratch
        // (scratch/link -> scratch/real) rather than relying on any particular OS's own ambient
        // symlinks - the same shape ProjectStoreTests's own
        // Adopt_ThroughAnIntermediateSymlinkedDirectory_ResolvesTheFullChain_SoBothSpellingsConvergeOnOneRow
        // test uses for the storage side of this identical invariant - including pre-resolving
        // "scratch" via RealPath before it ever becomes a symlink's own TARGET text: resolution is
        // single-pass, so a target written with a not-yet-canonical ancestor of its own (e.g. this
        // process's own GetTempPath(), under macOS's /var -> /private/var) would only be partially
        // resolved when the "link" component substitutes it in - a fixture-construction pitfall,
        // not the production bug this test exists to pin.
        var scratch = RealPath(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        var real = Path.Combine(scratch, "real");
        var link = Path.Combine(scratch, "link");
        var projectViaReal = Path.Combine(real, "Proj");
        var projectViaLink = Path.Combine(link, "Proj");

        Directory.CreateDirectory(Path.Combine(projectViaReal, "ProjectSettings"));
        File.WriteAllText(Path.Combine(projectViaReal, "ProjectSettings", "ProjectSettings.asset"),
            "  productGUID: aaaabbbbccccddddeeeeffff00003333\n");
        Directory.CreateDirectory(Path.Combine(projectViaReal, "Assets"));
        Directory.CreateSymbolicLink(link, real);

        try
        {
            var (service, router) = NewRouter();
            service.AdoptAndIndex(projectViaReal); // Known via the REAL spelling.

            // Reported root arrives through the INTERMEDIATE symlink, never the project's own leaf.
            var result = await router.ResolveAsync(new FakeRootsProvider(projectViaLink));

            Assert.True(result.IsResolved);
            Assert.Equal("aaaabbbbccccddddeeeeffff00003333", result.ProductGuid);
            Assert.Null(result.Announcement); // Already known - must not re-announce as new.
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_PicksTheDeepestKnownProjectWhenOneIsNestedInsideAnother()
    {
        // Deliberately named so ProjectStore.All's alphabetical-by-Name ordering (what
        // KnownProjects() returns) visits the OUTER project BEFORE the inner one - proving this
        // test fails for the right reason if MatchAgainstKnownProjects still returned the FIRST
        // equals-or-contains match instead of the DEEPEST one: "AAA_Outer..." sorts before
        // "ZZZ_Inner" regardless of either one's own random suffix.
        var outer = Path.Combine(Path.GetTempPath(), "AAA_Outer_" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(outer, "ProjectSettings"));
        File.WriteAllText(Path.Combine(outer, "ProjectSettings", "ProjectSettings.asset"),
            "  productGUID: aaaabbbbccccddddeeeeffff00001111\n");
        Directory.CreateDirectory(Path.Combine(outer, "Assets"));
        _projectRoots.Add(outer);

        // A second, fully independent Unity project nested INSIDE the outer one's own tree - e.g.
        // a stray copy, an embedded example project, or a submodule.
        var inner = Path.Combine(outer, "Nested", "ZZZ_Inner");
        Directory.CreateDirectory(Path.Combine(inner, "ProjectSettings"));
        File.WriteAllText(Path.Combine(inner, "ProjectSettings", "ProjectSettings.asset"),
            "  productGUID: bbbbccccddddeeeeffff000011112222\n");
        Directory.CreateDirectory(Path.Combine(inner, "Assets"));

        var (service, router) = NewRouter();
        service.AdoptAndIndex(outer);
        service.AdoptAndIndex(inner);

        // A root inside the INNER project must resolve to the inner project specifically - it is
        // also, technically, inside the outer one, but the outer is the wrong, less-specific
        // answer for a root that sits inside the deeper, more specific project.
        var result = await router.ResolveAsync(new FakeRootsProvider(Path.Combine(inner, "Assets")));

        Assert.True(result.IsResolved);
        Assert.Equal("bbbbccccddddeeeeffff000011112222", result.ProductGuid);
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
