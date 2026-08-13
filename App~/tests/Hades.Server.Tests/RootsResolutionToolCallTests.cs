using System.Text.Json;
using Hades.Core;
using Hades.Core.Storage;
using Hades.Server.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

/// <summary>
/// End-to-end proof, through the real MCP HTTP path (same <see cref="WebApplicationFactory{TEntryPoint}"/>
/// + <see cref="McpTestClient"/> harness as <c>ToolCallTests</c>/<c>TransportConformanceTests</c>),
/// that the seamless-resolution wiring actually reaches a live tool call - not just that the
/// algorithm itself is correct. <c>RootsRouterTests</c> and <c>ToolSupportTests</c> already pin the
/// algorithm (explicit handle / sole-known-project / roots / auto-adopt / the standard error)
/// against a bare <see cref="RootsRouter"/>/<see cref="ProjectService"/>, constructing an
/// <see cref="IRootsProvider"/> by hand - neither ever calls an actual <c>[McpServerTool]</c> method,
/// so neither can prove the SDK really binds a live session into that new
/// <c>RequestContext&lt;CallToolRequestParams&gt;</c> parameter, or that
/// <see cref="ToolSupport.AppendAnnouncement"/>'s <c>context.Items</c> hand-off from inside a tool
/// method to Program.cs's own CallToolFilters chain actually crosses that boundary on a real
/// request. <c>ProjectHandleTests</c> already covers the two-known-projects/no-fake-roots case
/// (roots genuinely unavailable over this harness's plain HTTP client, so the standard "needs a
/// project argument" error still fires, byte-identical - unchanged by this wiring, proven there);
/// this fixture is the roots-DO-decide-it counterpart.
///
/// <see cref="IRootsProvider"/> is substituted via DI - the exact seam
/// <see cref="ToolSupport.ResolveProjectAsync(ProjectService,string?,ModelContextProtocol.Server.RequestContext{ModelContextProtocol.Protocol.CallToolRequestParams})"/>
/// itself probes first (<c>services.GetService&lt;IRootsProvider&gt;()</c>), before ever
/// constructing a real <see cref="McpRootsProvider"/> over the live <c>McpServer</c>. Production
/// wiring never registers one, so this substitution is test-only - the same idea as every other
/// <see cref="WebApplicationFactory{TEntryPoint}"/>-based fixture in this project swapping
/// <see cref="AppPaths"/> for an isolated one. <c>RootsRouterTests</c>' own
/// <c>FakeRootsProvider</c> is reused unchanged (internal, same assembly).
/// </summary>
public class RootsResolutionToolCallTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    readonly WebApplicationFactory<Program> _baseFactory;
    readonly List<string> _tempDirs = [];

    public RootsResolutionToolCallTests(WebApplicationFactory<Program> factory) => _baseFactory = factory;

    static JsonElement Structured(JsonElement envelope) =>
        envelope.GetProperty("result").GetProperty("structuredContent");

    static JsonElement ContentBlocks(JsonElement envelope) =>
        envelope.GetProperty("result").GetProperty("content");

    string MakeUnityProject(string guid, string typeName)
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _tempDirs.Add(root);
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(root, "ProjectSettings", "ProjectSettings.asset"),
            $"  productGUID: {guid}\n");
        var scripts = Path.Combine(root, "Assets", "Scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, $"{typeName}.cs"), $"public class {typeName} {{ }}");
        return root;
    }

    /// <summary>A fresh <see cref="AppPaths"/>-isolated derivative of the shared base factory, with
    /// <paramref name="fakeRoots"/> registered ahead of whatever a real McpServer session would
    /// otherwise be probed for - built per-test (unlike every other fixture's one-per-class
    /// <c>_factory</c>) because each test here needs a DIFFERENT fake roots answer.</summary>
    WebApplicationFactory<Program> FactoryWithFakeRoots(IRootsProvider fakeRoots)
    {
        var appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _tempDirs.Add(appRoot);

        return _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppPaths>();
                services.AddSingleton(new AppPaths(appRoot));
                services.AddSingleton(fakeRoots);
            }));
    }

    [Fact]
    public async Task NoProjectArgument_WithTwoKnownProjects_ConsultsTheRootsProviderAndRoutesToTheMatch()
    {
        var alphaRoot = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111", "AlphaOnlyType");
        var betaRoot = MakeUnityProject("bbbbccccddddeeeeffff000011112222", "BetaOnlyType");

        using var factory = FactoryWithFakeRoots(new FakeRootsProvider(betaRoot));
        var projects = factory.Services.GetRequiredService<ProjectService>();
        projects.AdoptAndIndex(alphaRoot);
        projects.AdoptAndIndex(betaRoot);

        // No 'project' argument, and two are known - exactly the shape ProjectHandleTests.
        // OmittingTheHandleFailsWithTheCandidateList proves fails (today, unchanged) when roots
        // are not available. Here they ARE (FakeRootsProvider names beta's own root), so this must
        // succeed instead, scoped to beta specifically - not alpha, and not a "which one?" error.
        // Succeeding at all, AND landing on the right one, is only possible if this call actually
        // asked FakeRootsProvider for roots and matched them - the assertion below is the proof.
        var envelope = await McpTestClient.CallTool(factory, "search_by_name", new { namePattern = "OnlyType" });
        var structured = Structured(envelope);

        Assert.Equal(1, structured.GetProperty("totalReturned").GetInt32());
        Assert.Equal("BetaOnlyType", structured.GetProperty("results")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task NoProjectArgument_WithTwoKnownProjects_RoutesToWhicheverRootsNameEvenTheOtherOne()
    {
        // Same setup as above, roots pointed at the OTHER project instead - rules out "always picks
        // beta" (e.g. last-adopted, or alphabetical) as a false positive for the test above.
        var alphaRoot = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111", "AlphaOnlyType");
        var betaRoot = MakeUnityProject("bbbbccccddddeeeeffff000011112222", "BetaOnlyType");

        using var factory = FactoryWithFakeRoots(new FakeRootsProvider(alphaRoot));
        var projects = factory.Services.GetRequiredService<ProjectService>();
        projects.AdoptAndIndex(alphaRoot);
        projects.AdoptAndIndex(betaRoot);

        var structured = Structured(
            await McpTestClient.CallTool(factory, "search_by_name", new { namePattern = "OnlyType" }));

        Assert.Equal("AlphaOnlyType", structured.GetProperty("results")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task AutoAdoptedProject_AnnouncementAppearsInTheVisibleResult()
    {
        var freshRoot = MakeUnityProject("ccccddddeeeeffff0000111122223333", "FreshType");
        // Deliberately never adopted beforehand - zero known projects, so an explicit handle and
        // the sole-known-project fallback both come up empty and this can only succeed by
        // auto-adopting through FakeRootsProvider.

        using var factory = FactoryWithFakeRoots(new FakeRootsProvider(freshRoot));

        var envelope = await McpTestClient.CallTool(factory, "get_project_summary");

        // The call itself succeeded, scoped to the freshly auto-adopted (and synchronously
        // indexed - ProjectService.EnsureIndexed blocks, no polling needed here) project.
        Assert.True(Structured(envelope).GetProperty("totalNodes").GetInt32() > 0, envelope.GetRawText());

        var blocks = ContentBlocks(envelope);
        var announcementText = Enumerable.Range(0, blocks.GetArrayLength())
            .Select(i => blocks[i])
            .Where(b => b.GetProperty("type").GetString() == "text")
            .Select(b => b.GetProperty("text").GetString() ?? "")
            .FirstOrDefault(text => text.Contains("Registered", StringComparison.Ordinal));

        Assert.True(announcementText is not null,
            $"No announcement content block found in the visible result. Full content: {blocks.GetRawText()}");
        Assert.Contains(Path.GetFileName(freshRoot.TrimEnd(Path.DirectorySeparatorChar)), announcementText);
        Assert.Contains("first index", announcementText, StringComparison.OrdinalIgnoreCase);

        // Purely additive: the structured-content mirror this codebase always puts at content[0]
        // (see ToolCallTests.SearchByName_AlsoMirrorsJsonIntoATextBlock) is still there, unmoved -
        // the announcement is an EXTRA block, never a replacement.
        Assert.Contains("totalNodes", blocks[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ProjectAlreadyKnown_RootsMatchProducesNoAnnouncement()
    {
        // The mirror image of the auto-adopt test above: a root that matches an ALREADY-known
        // project routes silently (RootsRouter.ResolveAsync's own doc comment: "routes there
        // silently") - no new project was just registered, so there is nothing to announce. A
        // SECOND known project is adopted too (unused by the call itself, whose 'namePattern'-free
        // get_project_summary needs no disambiguation content) purely so KnownProjects().Count is 2
        // - with only one known project, ResolveProjectAsync's own sole-known-project fast path
        // would short-circuit before ever asking FakeRootsProvider anything, and this test would
        // pass even if roots-matching-a-known-project were broken. Two known projects forces this
        // call through RootsRouter.ResolveAsync for real.
        var root = MakeUnityProject("ddddeeeeffff00001111222233334444", "KnownType");
        var otherRoot = MakeUnityProject("eeeeffff000011112222333344445555", "OtherKnownType");

        using var factory = FactoryWithFakeRoots(new FakeRootsProvider(root));
        var projects = factory.Services.GetRequiredService<ProjectService>();
        projects.AdoptAndIndex(root);
        projects.AdoptAndIndex(otherRoot);

        var envelope = await McpTestClient.CallTool(factory, "get_project_summary");

        // Asserted BEFORE the announcement check, and deliberately not just "no announcement": with
        // two known projects and no explicit handle, a call that failed to resolve at all (e.g. the
        // roots seam itself broken) has no structuredContent either - Structured() throws exactly
        // as it does for the three tests above whose whole point IS that failure. This keeps
        // "produces no announcement" a genuine positive claim about a SUCCESSFUL, roots-routed
        // call, not something equally true of an outright failure.
        Assert.Equal(1, Structured(envelope).GetProperty("totalNodes").GetInt32());

        var blocks = ContentBlocks(envelope);
        var hasAnnouncement = Enumerable.Range(0, blocks.GetArrayLength())
            .Select(i => blocks[i])
            .Any(b => b.GetProperty("type").GetString() == "text"
                && (b.GetProperty("text").GetString() ?? "").Contains("Registered", StringComparison.Ordinal));

        Assert.False(hasAnnouncement, blocks.GetRawText());
    }

    public void Dispose()
    {
        // See EditorToolTestBase.Dispose's own comment: each derived factory's own background
        // services can still be touching these directories until that host itself is disposed -
        // which happens per-test (`using var factory = ...` above), before this runs. The shared
        // _baseFactory is owned by xUnit's IClassFixture, never disposed here.
        TeardownDiagnostics.Delete([.. _tempDirs]);
    }
}
