using System.Text.Json;
using Hades.Core;
using Hades.Core.Storage;
using Hades.Core.Tracing;
using Hades.Server.Mcp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
    /// <c>_factory</c>) because each test here needs a DIFFERENT fake roots answer.
    /// <paramref name="logging"/>, when given, is added as an extra logging provider (T1's own
    /// "unroutable case logs a structured drop line" tests below need to observe that line, the
    /// same DI-substitution style as everything else in this fixture).</summary>
    WebApplicationFactory<Program> FactoryWithFakeRoots(IRootsProvider fakeRoots, ILoggerProvider? logging = null)
    {
        var appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _tempDirs.Add(appRoot);

        return _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppPaths>();
                services.AddSingleton(new AppPaths(appRoot));
                services.AddSingleton(fakeRoots);
            });

            if (logging is not null) builder.ConfigureLogging(lb => lb.AddProvider(logging));
        });
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

    [Fact]
    public async Task RootsResolvedCall_IsTracedIntoTheResolvedProjectsOwnTracesDb()
    {
        // Two known projects and no 'project' argument - the shape only roots can resolve.
        // Program.cs's RecordTrace must file this call under the project the call ACTUALLY ran
        // against, which the synchronous ToolSupport.ResolveProject alone can never determine
        // here (two candidates, no handle - it throws, and tracing's own catch used to swallow
        // that into "no trace anywhere"). The Traces surface being blind to exactly the calls
        // the seamless path serves is the F15 blind-spot class all over again, hence pinned.
        var alphaRoot = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111", "AlphaOnlyType");
        var betaRoot = MakeUnityProject("bbbbccccddddeeeeffff000011112222", "BetaOnlyType");

        using var factory = FactoryWithFakeRoots(new FakeRootsProvider(betaRoot));
        var projects = factory.Services.GetRequiredService<ProjectService>();
        projects.AdoptAndIndex(alphaRoot);
        projects.AdoptAndIndex(betaRoot);

        var envelope = await McpTestClient.CallTool(factory, "search_by_name", new { namePattern = "OnlyType" });

        // Sanity first: the call itself resolved via roots to beta. A trace assertion on a call
        // that never resolved would be testing nothing.
        Assert.Equal("BetaOnlyType",
            Structured(envelope).GetProperty("results")[0].GetProperty("name").GetString());

        var paths = factory.Services.GetRequiredService<AppPaths>();
        using (var store = TraceStore.Open(paths.TracesDb("bbbbccccddddeeeeffff000011112222")))
        {
            Assert.True(store.RecentTraces().Any(t => t.ToolName == "search_by_name"),
                "the roots-resolved call left no trace in the resolved project's own traces.db");
        }

        // And under beta specifically - a trace filed under alpha would misattribute which
        // project the call ran against, worse than no trace at all.
        var alphaDb = paths.TracesDb("aaaabbbbccccddddeeeeffff00001111");
        if (File.Exists(alphaDb))
        {
            using var alphaStore = TraceStore.Open(alphaDb);
            Assert.DoesNotContain(alphaStore.RecentTraces(), t => t.ToolName == "search_by_name");
        }
    }

    // -----------------------------------------------------------------------------------------
    // Family C finding 1 [Primary]: the mutating/live-Editor tools (scene_apply, material_apply,
    // prefab_apply, asset_manage, scene_manage, animation_apply, project_settings_apply,
    // inspector_inspect) passed their raw 'project' argument straight to
    // EditorProxy.SendCommandAsync, with none of the roots-based auto-resolution the 17 read/graph
    // tools get through ToolSupport.ResolveProjectAsync (proven above, for the read tools, by
    // NoProjectArgument_WithTwoKnownProjects_ConsultsTheRootsProviderAndRoutesToTheMatch) - so in a
    // roots-capable client with two registered projects, omitting 'project' on (say) scene_apply
    // hit the synchronous ProjectResolver.Resolve's ambiguous "needs a 'project' argument" error
    // immediately, never once asking the client's roots for the answer a same-shaped
    // search_by_name call already gets right.
    // -----------------------------------------------------------------------------------------

    public static IEnumerable<object[]> MutatingEditorToolsWithMinimalValidArguments()
    {
        // Each argument set is the minimal shape that clears local validation (a known op, and -
        // since OperationFieldValidator only ever rejects an UNKNOWN field, never a missing one -
        // no other fields are required at all) and reaches EditorProxy.SendCommandAsync, the point
        // where project resolution actually happens.
        yield return new object[] { "scene_apply", new { operations = new[] { new Dictionary<string, object> { ["op"] = "select" } } } };
        yield return new object[] { "material_apply", new { operations = new[] { new Dictionary<string, object> { ["op"] = "create" } } } };
        yield return new object[] { "prefab_apply", new { operations = new[] { new Dictionary<string, object> { ["op"] = "applyOverrides" } } } };
        yield return new object[] { "asset_manage", new { operations = new[] { new Dictionary<string, object> { ["op"] = "refresh" } } } };
        yield return new object[] { "scene_manage", new { operations = new[] { new Dictionary<string, object> { ["op"] = "save" } } } };
        yield return new object[] { "animation_apply", new { operations = new[] { new Dictionary<string, object> { ["op"] = "assignClip" } } } };
        yield return new object[] { "project_settings_apply", new { operations = new[] { new Dictionary<string, object> { ["op"] = "createTag" } } } };
        yield return new object[] { "inspector_inspect", new { path = "Foo" } };
    }

    /// <summary>
    /// Proven WITHOUT a live fake-Unity connection at all - deliberately: once roots resolve a
    /// productGuid, EditorProxy.SendCommandAsync's own NotAttachedError names the resolved
    /// project's Name once GetCharonStatus reports no Editor attached, so a tool that actually
    /// consulted the roots provider fails LATER, for an entirely different and correctly-SCOPED
    /// reason, instead of failing immediately on the ambiguous "which project?" question it should
    /// never have had to ask at all. The two halves this composes - roots-to-guid resolution
    /// (RootsRouterTests, and this class's own read-tool tests above) and guid-to-successful-wire-
    /// call (each tool's own EditorToolTestBase-based single-project tests, e.g.
    /// SceneApplyTests.SceneApply_FullOperationSweep...) - are each already proven exhaustively
    /// elsewhere; this is the seam between them, for every one of the eight tools that needed it
    /// wired.
    /// </summary>
    [Theory]
    [MemberData(nameof(MutatingEditorToolsWithMinimalValidArguments))]
    public async Task NoProjectArgument_WithTwoKnownProjects_MutatingEditorToolConsultsRootsInsteadOfDemandingAHandle(
        string toolName, object arguments)
    {
        var alphaRoot = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111", "AlphaOnlyType");
        var betaRoot = MakeUnityProject("bbbbccccddddeeeeffff000011112222", "BetaOnlyType");

        using var factory = FactoryWithFakeRoots(new FakeRootsProvider(betaRoot));
        var projects = factory.Services.GetRequiredService<ProjectService>();
        projects.AdoptAndIndex(alphaRoot);
        projects.AdoptAndIndex(betaRoot);

        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(factory, toolName, arguments));

        // BEFORE the fix: the raw pass-through reaches ProjectResolver.Resolve(projects, null)
        // directly, with 2 known projects and no handle - the ambiguous "needs a 'project'
        // argument" error, byte-identical to
        // ProjectHandleTests.OmittingTheHandleFailsWithTheCandidateList (which proves that error
        // fires when roots are genuinely unavailable). Here roots ARE available and name beta
        // specifically - a tool that actually consults them never reaches that error at all: it
        // resolves to beta and fails for a completely different, later reason (no Editor
        // attached), naming beta's own path.
        Assert.DoesNotContain("needs a 'project' argument", text);
        Assert.Contains(Path.GetFileName(betaRoot), text);
    }

    /// <summary>
    /// Known, PRE-EXISTING limitation of the shared announcement hand-off - <see
    /// cref="ToolSupport.ResolveProjectAsync(ProjectService,string?,ModelContextProtocol.Server.RequestContext{ModelContextProtocol.Protocol.CallToolRequestParams})"/>
    /// records the announcement onto <c>context.Items</c> the moment it auto-adopts; Program.cs's
    /// CallToolFilters chain reads it back afterward via <see cref="ToolSupport.AppendAnnouncement"/>,
    /// whose own doc comment claims it is "Appended regardless of CallToolResult.IsError" - true
    /// when the SAME call goes on to SUCCEED (<see cref="AutoAdoptedProject_AnnouncementAppearsInTheVisibleResult"/>
    /// above), but NOT when the same call's own McpException (thrown afterward, from further down
    /// the SAME method - here, EditorProxy.SendCommandAsync's NotAttachedError) ends up escaping
    /// Program.cs's own CallToolFilters try/catch instead of being converted in place: in that shape
    /// the announcement recorded moments earlier on the identical context.Items is never read back
    /// at all. Pinned here as a KNOWN gap, not fixed: it is NOT specific to any of the eight tools
    /// this finding wired up (scene_apply below is only the representative example) - <c>
    /// script_editing_session</c> (EditorProjectTools.cs, untouched by this change, and the
    /// established PATTERN these eight tools copy) hits the byte-identical gap for the byte-identical
    /// shape, proven side by side below. Fixing it would mean touching ToolSupport.cs's
    /// <see cref="ToolSupport.AppendAnnouncement"/>/Program.cs's CallToolFilters ordering - both
    /// outside this fix's own file boundary - so this is flagged, not silently patched over; see the
    /// same "asserted so a future improvement is noticed" convention
    /// <c>ToolCallTests.OmittingARequiredArgumentIsReportedAsAToolError</c> already established for
    /// a different known SDK-boundary limitation.
    /// </summary>
    [Fact]
    public async Task AutoAdoptedProject_KnownLimitation_AnnouncementIsLostWhenTheSameCallThenErrors()
    {
        var sceneApplyRoot = MakeUnityProject("ddddeeeeffff000011112222aaaa3333", "FreshMutatingType");
        var scriptEditingRoot = MakeUnityProject("eeeeffff000011112222aaaabbbb4444", "FreshReferenceType");

        async Task<string[]> TextBlocksAsync(WebApplicationFactory<Program> f, string tool, object args, string expectedRootBasename)
        {
            var envelope = await McpTestClient.CallTool(f, tool, args);

            // Sanity: this reached the auto-adopted project specifically (a "not attached" error
            // naming it), not the pre-fix, zero-known-projects error ("Hades does not know about
            // any project yet...") - which would make "no announcement" trivially, uselessly true.
            Assert.Contains(expectedRootBasename, McpTestClient.ErrorText(envelope));

            var blocks = ContentBlocks(envelope);
            return Enumerable.Range(0, blocks.GetArrayLength())
                .Select(i => blocks[i])
                .Where(b => b.GetProperty("type").GetString() == "text")
                .Select(b => b.GetProperty("text").GetString() ?? "")
                .ToArray();
        }

        using (var factory = FactoryWithFakeRoots(new FakeRootsProvider(sceneApplyRoot)))
        {
            var texts = await TextBlocksAsync(factory, "scene_apply",
                new { operations = new[] { new Dictionary<string, object> { ["op"] = "select" } } },
                Path.GetFileName(sceneApplyRoot.TrimEnd(Path.DirectorySeparatorChar)));

            Assert.DoesNotContain(texts, t => t.Contains("Registered", StringComparison.Ordinal));
        }

        using (var factory = FactoryWithFakeRoots(new FakeRootsProvider(scriptEditingRoot)))
        {
            var texts = await TextBlocksAsync(factory, "script_editing_session", new { action = "begin" },
                Path.GetFileName(scriptEditingRoot.TrimEnd(Path.DirectorySeparatorChar)));

            Assert.DoesNotContain(texts, t => t.Contains("Registered", StringComparison.Ordinal));
        }
    }

    // -----------------------------------------------------------------------------------------
    // T1: guard refusals leave no trace on multi-project servers. A refusal is hoisted BEFORE
    // ToolSupport.ResolveProjectAsync ever runs (see SettingsTools.ProjectSettings's own comment
    // on why), so context.Items never carries ResolvedProjectItemsKey for these calls -
    // Program.cs's RecordTrace(...) sync fallback then throws the instant 2+ projects are known
    // and no 'project' argument was given, silently dropped by its own blanket catch.
    // RecordTraceForRefusalAsync (Program.cs's own two refusal call sites only - F13a's own
    // rejection, and a tool call that threw all the way out to the filter's catch block) recovers
    // this with a side-effect-free peek at the client's current roots.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task F13aRejection_WithTwoKnownProjectsAndNoProjectArgument_IsTracedIntoTheRootsIdentifiedProject()
    {
        var alphaRoot = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111", "AlphaOnlyType");
        var betaRoot = MakeUnityProject("bbbbccccddddeeeeffff000011112222", "BetaOnlyType");

        using var factory = FactoryWithFakeRoots(new FakeRootsProvider(betaRoot));
        var projects = factory.Services.GetRequiredService<ProjectService>();
        projects.AdoptAndIndex(alphaRoot);
        projects.AdoptAndIndex(betaRoot);

        // 'bogusParam' is not a real search_by_name parameter - F13a (Program.cs's own
        // UnknownParameterRejection) refuses the call BEFORE the tool body - and so before
        // ToolSupport.ResolveProjectAsync - ever runs, the same "refused, not ignored" shape
        // ToolCallTests.cs's own F13a tests already pin.
        var envelope = await McpTestClient.CallTool(factory, "search_by_name",
            new { namePattern = "OnlyType", bogusParam = "x" });

        Assert.Contains("bogusParam", McpTestClient.ErrorText(envelope));

        var paths = factory.Services.GetRequiredService<AppPaths>();
        using var betaStore = TraceStore.Open(paths.TracesDb("bbbbccccddddeeeeffff000011112222"));
        Assert.True(betaStore.RecentTraces().Any(t => t.ToolName == "search_by_name" && t.Status == "error"),
            "the F13a-rejected call left no trace in the roots-identified project's own traces.db");
    }

    [Fact]
    public async Task HoistedThrowRefusal_WithTwoKnownProjectsAndNoProjectArgument_IsTracedIntoTheRootsIdentifiedProject()
    {
        var alphaRoot = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111", "AlphaOnlyType");
        var betaRoot = MakeUnityProject("bbbbccccddddeeeeffff000011112222", "BetaOnlyType");

        using var factory = FactoryWithFakeRoots(new FakeRootsProvider(betaRoot));
        var projects = factory.Services.GetRequiredService<ProjectService>();
        projects.AdoptAndIndex(alphaRoot);
        projects.AdoptAndIndex(betaRoot);

        // project_settings validates 'section' BEFORE ever resolving a project (SettingsTools.
        // ProjectSettings's own hoisting comment) - an unrecognised section throws McpException
        // straight out of the tool method, past Program.cs's own try/catch as a genuine throw (see
        // that catch block's own comment), never through ToolSupport.ResolveProjectAsync at all -
        // so context.Items never carries a resolved project for this call.
        var envelope = await McpTestClient.CallTool(factory, "project_settings",
            new { section = "not-a-real-section" });

        Assert.Contains("is not a recognised project_settings section", McpTestClient.ErrorText(envelope));

        var paths = factory.Services.GetRequiredService<AppPaths>();
        using var betaStore = TraceStore.Open(paths.TracesDb("bbbbccccddddeeeeffff000011112222"));
        Assert.True(betaStore.RecentTraces().Any(t => t.ToolName == "project_settings" && t.Status == "error"),
            "the hoisted-throw refusal left no trace in the roots-identified project's own traces.db");
    }

    [Fact]
    public async Task RefusedCall_WhenRootsMatchNoKnownProject_LogsAStructuredDropLine_AndTracesNowhere()
    {
        var alphaRoot = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111", "AlphaOnlyType");
        var betaRoot = MakeUnityProject("bbbbccccddddeeeeffff000011112222", "BetaOnlyType");

        // A real, plain directory - not a Unity project, and never adopted - so it matches neither
        // known project. TryPeekKnownProjectFromRootsAsync never auto-adopts (unlike
        // RootsRouter.ResolveAsync), so even a root that WAS a fresh, valid Unity project would
        // still match nothing here - this directory being non-Unity just keeps the test unambiguous.
        var unrelatedRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(unrelatedRoot);
        _tempDirs.Add(unrelatedRoot);

        var logs = new CapturingLoggerProvider();
        using var factory = FactoryWithFakeRoots(new FakeRootsProvider(unrelatedRoot), logs);
        var projects = factory.Services.GetRequiredService<ProjectService>();
        projects.AdoptAndIndex(alphaRoot);
        projects.AdoptAndIndex(betaRoot);

        var envelope = await McpTestClient.CallTool(factory, "project_settings",
            new { section = "not-a-real-section" });

        Assert.Contains("is not a recognised project_settings section", McpTestClient.ErrorText(envelope));

        // The never-fail guarantee still holds: the call is refused for its own reason above, and
        // no trace lands ANYWHERE - not misattributed to either known project.
        var paths = factory.Services.GetRequiredService<AppPaths>();
        foreach (var guid in new[] { "aaaabbbbccccddddeeeeffff00001111", "bbbbccccddddeeeeffff000011112222" })
        {
            var dbPath = paths.TracesDb(guid);
            if (!File.Exists(dbPath)) continue;
            using var store = TraceStore.Open(dbPath);
            Assert.DoesNotContain(store.RecentTraces(), t => t.ToolName == "project_settings");
        }

        // But the drop itself is diagnosable, not silent - one structured line naming the tool.
        Assert.Contains(logs.Messages, m =>
            m.Contains("project_settings", StringComparison.Ordinal) && m.Contains("Trace dropped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefusedCall_WhenAReportedRootIsAnInvalidPathString_DegradesToNoMatch_NeverThrows()
    {
        // Program.cs's CanonicalizeForPeek wraps ProjectStore.Canonicalize(reportedRoot) in a
        // try/catch(ArgumentException) that degrades to returning the raw, uncanonicalized input -
        // "a canonicalization failure must never break the trace-naming peek this exists for, only
        // make it match less" (that method's own doc comment). An embedded NUL is exactly what
        // makes Path.GetFullPath (the first thing Canonicalize calls) throw ArgumentException.
        //
        // Without that catch, the SAME exception would propagate out of MatchAgainstKnownProjectsOnly
        // and TryPeekKnownProjectFromRootsAsync into RecordTraceForRefusalAsync's own outer
        // try/catch(bare) - which swallows it, so the call's own refusal message would still reach
        // the caller either way (the never-fail guarantee holds regardless). What the fix actually
        // changes is diagnosability: swallowed there, execution never reaches the "productGuid is
        // null" branch that logs the structured "Trace dropped" line below - the drop would go
        // silent instead. That is the narrow, observable difference this test pins.
        var alphaRoot = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111", "AlphaOnlyType");
        var betaRoot = MakeUnityProject("bbbbccccddddeeeeffff000011112222", "BetaOnlyType");

        // FakeRootsProvider hands roots back verbatim (no URI parsing/validation) - a plain string
        // with an embedded NUL is exactly what Path.GetFullPath rejects.
        var hostileRoot = "/tmp/bad\0path";

        var logs = new CapturingLoggerProvider();
        using var factory = FactoryWithFakeRoots(new FakeRootsProvider(hostileRoot), logs);
        var projects = factory.Services.GetRequiredService<ProjectService>();
        projects.AdoptAndIndex(alphaRoot);
        projects.AdoptAndIndex(betaRoot);

        // 'edgeKind' is validated eagerly, before ToolSupport.ResolveProjectAsync ever runs (see
        // QueryTools.ValidateEdgeKind's own doc comment) - the same hoisted-throw refusal shape as
        // HoistedThrowRefusal_WithTwoKnownProjectsAndNoProjectArgument_IsTracedIntoTheRootsIdentifiedProject
        // above, here with a hostile roots answer instead of an unrelated-but-valid one.
        var envelope = await McpTestClient.CallTool(factory, "graph_query",
            new { edgeKind = "not-a-real-edge-kind" });

        // The call still errors with ITS OWN refusal message - never a path/ArgumentException
        // leaking out in its place.
        Assert.Contains("is not a recognised graph_query 'edgeKind'", McpTestClient.ErrorText(envelope));

        // No trace lands anywhere - the hostile root matches no known project either way.
        var paths = factory.Services.GetRequiredService<AppPaths>();
        foreach (var guid in new[] { "aaaabbbbccccddddeeeeffff00001111", "bbbbccccddddeeeeffff000011112222" })
        {
            var dbPath = paths.TracesDb(guid);
            if (!File.Exists(dbPath)) continue;
            using var store = TraceStore.Open(dbPath);
            Assert.DoesNotContain(store.RecentTraces(), t => t.ToolName == "graph_query");
        }

        // The drop itself is still diagnosable, not silently swallowed by a wider catch - this is
        // the line that would go missing without CanonicalizeForPeek's own degrade.
        Assert.Contains(logs.Messages, m =>
            m.Contains("graph_query", StringComparison.Ordinal) && m.Contains("Trace dropped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefusedCall_WithAnExplicitProjectArgument_StillTracesSynchronously_NoRootsRoundTripNeeded()
    {
        // (1) from RecordTraceForRefusalAsync's own doc comment: an explicit 'project' argument
        // resolves synchronously, exactly as it did before this fix - roots are never even asked
        // (asserted below via FakeRootsProvider.CallCount), so this also proves the roots peek is
        // additive, not a replacement for the existing explicit-handle path.
        var alphaRoot = MakeUnityProject("aaaabbbbccccddddeeeeffff00001111", "AlphaOnlyType");
        var betaRoot = MakeUnityProject("bbbbccccddddeeeeffff000011112222", "BetaOnlyType");

        var fakeRoots = new FakeRootsProvider(alphaRoot);
        using var factory = FactoryWithFakeRoots(fakeRoots);
        var projects = factory.Services.GetRequiredService<ProjectService>();
        projects.AdoptAndIndex(alphaRoot);
        projects.AdoptAndIndex(betaRoot);

        var envelope = await McpTestClient.CallTool(factory, "project_settings",
            new { section = "not-a-real-section", project = "bbbbccccddddeeeeffff000011112222" });

        Assert.Contains("is not a recognised project_settings section", McpTestClient.ErrorText(envelope));
        Assert.Equal(0, fakeRoots.CallCount);

        var paths = factory.Services.GetRequiredService<AppPaths>();
        using var betaStore = TraceStore.Open(paths.TracesDb("bbbbccccddddeeeeffff000011112222"));
        Assert.True(betaStore.RecentTraces().Any(t => t.ToolName == "project_settings" && t.Status == "error"),
            "an explicit 'project' argument on a refused call must still trace synchronously");
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

/// <summary>Captures every message logged through DI's own <see cref="ILoggerFactory"/> - the seam
/// Program.cs's RecordTraceForRefusalAsync uses for its own "trace dropped" line - so a test can
/// assert a specific drop was actually LOGGED, not merely infer it from the absence of a trace
/// (which "logging itself failed too" would also produce). Registered as an extra provider via
/// <see cref="ILoggingBuilder.AddProvider"/> (see FactoryWithFakeRoots's own <c>logging</c>
/// parameter) rather than replacing the host's own logging outright, so ordinary console/debug
/// logging from the rest of the test host is unaffected.</summary>
sealed class CapturingLoggerProvider : ILoggerProvider
{
    readonly object _gate = new();
    readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages { get { lock (_gate) return [.. _messages]; } }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose() { }

    sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (owner._gate) owner._messages.Add(message);
        }
    }
}
