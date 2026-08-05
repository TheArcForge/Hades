using System.Diagnostics;
using System.Text.Json;
using Hades.Core;
using Hades.Core.Editors;
using Hades.Core.Observation;
using Hades.Core.Storage;
using Hades.Core.Tracing;
using Hades.Server.Control;
using Hades.Server.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

// The MCP endpoint's own address, deliberate and fixed - see MapMcp("/mcp")'s own comment below
// for why the PATH is pinned (the Claude Code plugin declares it statically) and McpBinding's own
// class doc comment for why the exact same reasoning fixes the PORT too. Left to Kestrel's bare
// default, this binds 127.0.0.1:5000 - which on any current Mac is already held by ControlCenter's
// own AirPlay Receiver (Plan 12 Task 4's blocker). ASPNETCORE_URLS, when the environment already
// sets one, always wins - see McpBinding.ResolveBindUrl's own doc comment for every caller that
// relies on that (WebApplicationFactory-based tests, CI, launchSettings.json,
// e2e-editor-attach.sh).
var mcpBindUrl = McpBinding.ResolveBindUrl(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));
builder.WebHost.UseUrls(mcpBindUrl);

// A failure to bind mcpBindUrl is handled - specifically and actionably - by McpBinding.Run's own
// catch, below. Without this filter, ASP.NET Core's own Host.StartAsync logs that same failure
// itself first, raw AddressInUseException stack trace included, via the default console logger,
// which McpBinding.Run cannot prevent (the log happens inside Host.StartAsync, before the
// exception ever reaches a caller's catch block) - verified empirically by actually occupying the
// port and running the real server. This is a pure logging-configuration change (no socket I/O),
// so - unlike a proactive bind probe, see McpBinding's own doc comment for why that alternative
// was tried and reverted - it cannot race or collide with the dozens of concurrent
// WebApplicationFactory-backed instances of this same Program.cs the test suite constructs.
builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None);

// HADES_HOME overrides the application-data root. AppPaths has always supported this (its own
// doc comment: "Instance-based with an override root so tests never touch the real
// application-data directory") but nothing exposed it to a running server, so two app instances
// on one machine unavoidably shared projects/, editor.token and every graph. That is not a
// test-only concern: the E2E check could not isolate itself, and a stale project left in the
// shared store made hades_charon_status fail with "Hades knows 2 projects".
// Null when unset, which is exactly the previous behaviour.
builder.Services.AddSingleton(new AppPaths(Environment.GetEnvironmentVariable("HADES_HOME")));
builder.Services.AddSingleton<EditorRegistry>();
builder.Services.AddSingleton<LeaseRegistry>();
builder.Services.AddSingleton<OperationRegistry>();
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton<RootsRouter>();
builder.Services.AddSingleton<ObservationService>();
builder.Services.AddSingleton(sp =>
    new EditorListener(sp.GetRequiredService<AppPaths>().EditorTokenFile, sp.GetRequiredService<EditorRegistry>(),
        sp.GetRequiredService<LeaseRegistry>()));

// EditorProxy: the one path every Editor-dependent MCP tool (EditorSceneTools,
// EditorComponentTools, and everything the rest of the "52 Editor tools" plan still adds) takes
// to reach the plugin - see that class's own doc comment. Registered BEFORE ControlListener below
// so the same DI-resolved singleton (sharing the SAME EditorRegistry every MCP tool and
// EditorListener itself use) backs Plan 11 Task 4's force-release action too - see
// EditorsEndpoint's own class doc comment and ControlListener's own "editorProxy" constructor
// parameter doc comment for why passing a mismatched, separately-constructed EditorProxy here
// would silently break release.
builder.Services.AddSingleton<EditorProxy>();

// Control API: the second loopback HTTP+JSON listener - see ControlListener's own class doc
// comment for why it is a separate listener rather than a second route on the MCP endpoint below.
// projects/leases/editorProxy are the SAME singletons every other surface (MCP tools,
// ObservationService) uses - see ControlListener's own constructor doc comment for why that
// matters: without this, /control/summary would observe an isolated, permanently-empty default
// instead of this app's real projects and leases, and /control/leases/{id}/release would never see
// any Editor as attached.
builder.Services.AddSingleton(sp => new ControlListener(
    sp.GetRequiredService<AppPaths>().ControlTokenFile,
    projects: sp.GetRequiredService<ProjectService>(),
    leases: sp.GetRequiredService<LeaseRegistry>(),
    editorProxy: sp.GetRequiredService<EditorProxy>(),
    operations: sp.GetRequiredService<OperationRegistry>(),
    configuration: sp.GetRequiredService<IConfiguration>(),
    // The port THIS process's own MCP endpoint is actually bound to (mcpBindUrl, above) - not
    // always the documented 7823, when ASPNETCORE_URLS overrides it. SettingsEndpoint.Build uses
    // this to tell "running on the documented port" apart from "moved elsewhere," which is what
    // makes GET /control/settings's mcpPort.inUse trustworthy instead of an unwinnable self-probe
    // - see that method's own doc comment.
    actualMcpPort: McpBinding.ResolveBoundPort(mcpBindUrl)));

// The SDK owns the Streamable HTTP transport, JSON-RPC dispatch, and protocol-version
// negotiation. Verified behaviour: GET and DELETE on the endpoint return 405 (revision
// 2026-07-28 removed the GET stream and protocol-level sessions), and requests are answered
// as SSE. What it does NOT do is validate Origin — that middleware below is ours.
// Plan 10 Task 6: the hard cutover. 27 tool classes shrank to 16 - EditorSceneTools,
// InspectionTools, ReferenceTools, EditorMaterialTools, EditorTagLayerTools, and EditorPrefabTools
// were deleted outright (every one of their MCP tools folded into a consolidated tool below, no
// method bodies left worth keeping standalone); TypedAssetTools, EditorAssetTools, and
// EditorSceneManagementTools lost every MCP tool but kept one shared record type each
// (RenderPipelineResult/ClipImportConfig/BuildSceneSpec, reused by SettingsTools/
// ProjectSettingsApplyTool) and so are no longer registered here; EditorComponentTools and
// EditorAnimationTools lost every MCP tool but kept shared static helpers other tool classes still
// call directly, likewise no longer registered. See the tool-surface-consolidation plan's own
// "Capability audit" section for the full old-tool -> new-call mapping, with a passing test per row.
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<HadesTools>()
    .WithTools<GraphTools>()
    .WithTools<SummaryTools>()
    .WithTools<SettingsTools>()
    .WithTools<InspectTool>()
    .WithTools<QueryTools>()
    .WithTools<SceneApplyTool>()
    .WithTools<MaterialApplyTool>()
    .WithTools<AnimationApplyTool>()
    .WithTools<SceneManageTool>()
    .WithTools<AssetManageTool>()
    .WithTools<ProjectSettingsApplyTool>()
    .WithTools<EditorInspectorTools>()
    .WithTools<PrefabApplyTool>()
    .WithTools<EditorProjectTools>()
    .WithTools<MemoryTools>();

// Tool-call tracing: every call to a registered tool passes through this filter, success or
// failure, because CallToolFilters wraps the SDK's own dispatch to the McpServerTool collection,
// not just its fallback handler for tools that AREN'T in that collection (verified empirically —
// the doc comment on CallToolFilters reads ambiguously on this point). Recording itself happens
// inside RecordTrace's own try/catch, so nothing here can fail the call being traced; see
// ToolCallTracer's class doc comment for the guarantee this rests on.
builder.Services.Configure<McpServerOptions>(options =>
{
    options.Filters.Request.CallToolFilters.Add(next => async (context, cancellationToken) =>
    {
        var toolName = context.Params.Name;
        var startUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stopwatch = Stopwatch.StartNew();

        CallToolResult result;
        try
        {
            result = await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A tool throwing all the way out to here is unusual — the SDK normally converts a
            // tool's own exception (e.g. McpException) into a non-throwing CallToolResult with
            // IsError = true before it reaches a request filter — but it is not impossible, so
            // both paths are traced as a failure.
            RecordTrace(context, toolName, startUtcMs, stopwatch.ElapsedMilliseconds, ok: false, ex.Message, result: null);
            throw;
        }

        var errorMessage = result.IsError == true ? DescribeError(result) : null;
        RecordTrace(context, toolName, startUtcMs, stopwatch.ElapsedMilliseconds, errorMessage is null, errorMessage, result);
        return result;
    });
});

var app = builder.Build();

// Any non-flag command-line argument is a Unity project path to adopt and index at startup.
// This is the walking skeleton's only way to tell the server about a project; continuous
// FSEvents discovery, Unity Hub import, and the control API all belong to later plans.
// Note this is deliberately NOT how a tool call picks its project — that uses an explicit
// handle (see HadesTools), because MCP roots are deprecated as of revision 2026-07-28.
// Kept small on purpose: it is a seam, not a feature.
foreach (var projectPath in args.Where(a => !a.StartsWith('-')))
{
    var service = app.Services.GetRequiredService<ProjectService>();

    // Walk up first, so a path pointing inside a project (Assets/Scripts, a single file)
    // still seeds the project rather than being rejected.
    var resolved = Hades.Core.Projects.ProjectIdentity.FindProjectRoot(projectPath) ?? projectPath;
    var adopted = service.AdoptAndIndex(resolved);

    if (adopted is null)
    {
        app.Logger.LogWarning("Not a Unity project (no readable ProjectSettings/ProjectSettings.asset): {Path}",
            projectPath);
        continue;
    }

    var summary = service.Summary(adopted.ProductGuid);
    app.Logger.LogInformation("Indexed {Name} ({Guid}): {Nodes} nodes",
        adopted.Name, adopted.ProductGuid, summary?.TotalNodes ?? 0);
}

// Editor link: the loopback listener a Unity Editor plugin dials into (see EditorListener's own
// class doc comment for the handshake and why Unity dials out rather than listening). Started and
// stopped the same way ObservationService is, immediately below: unconditionally on boot, disposed
// on ApplicationStopping so no accept loop or live session outlives the process.
var editorListener = app.Services.GetRequiredService<EditorListener>();
editorListener.Start();
app.Lifetime.ApplicationStopping.Register(editorListener.Dispose);

// Control API listener: deliberately NOT started unconditionally like the editor listener above.
// /control/ping is the ONLY signal CoreSupervisor (Shell~/HadesSupervision) has for "did the
// spawn succeed" - see CoreSupervisor.spawnOnce's own doc comment. This used to start eagerly,
// here, unconditionally - which completes well before McpBinding.Run below ever attempts the
// fixed-port MCP bind, so a core whose bind was about to fail still answered ping for a real (if
// short - measured live at ~100ms) window. A supervisor whose poll happened to land in that
// window declared the spawn successful for a process that was, at that exact moment, already
// doomed - Plan 13 Task 8's root cause for a measured 49 spawn attempts in 75 seconds with no
// ceiling (see CoreSupervisor.Configuration's own `minimumStableUptime` for the other half of
// that fix).
//
// ApplicationStarted fires once, only after every hosted service - including the web host
// service that performs the real Kestrel bind below - has started successfully; if that bind
// throws, the hosted-service startup loop aborts and ApplicationStarted never fires at all (this
// is Generic Host's own documented contract, not something specific to this app). Gating the
// control listener's Start() on it means readiness now genuinely means "fully started": on a
// failed bind, this listener never opens its port and never writes its discovery file, closing
// the race entirely rather than narrowing its window.
var controlListener = app.Services.GetRequiredService<ControlListener>();
app.Lifetime.ApplicationStarted.Register(controlListener.Start);
app.Lifetime.ApplicationStopping.Register(controlListener.Dispose);

// Continuous observation: a catch-up sweep for whatever changed while this process was not
// running, then a watcher per project plus a periodic sweep. The sweep is the source of truth;
// the watcher only shortens the delay before a change reaches the graph.
var observation = app.Services.GetRequiredService<ObservationService>();
observation.ProjectSynced += (guid, sweep) =>
    app.Logger.LogInformation(
        "Synced {Guid}: +{Added} ~{Changed} -{Deleted} in {Ms:F0}ms",
        guid, sweep.Added.Count, sweep.Changed.Count, sweep.Deleted.Count, sweep.Duration.TotalMilliseconds);
observation.Start();
app.Lifetime.ApplicationStopping.Register(observation.Dispose);

// Trace retention: unbounded growth is not hypothetical (traces.db held 1,631 spans for a modest
// amount of use on the real project). Pruning runs on its own timer, off the tool-call request
// path entirely — a call's own trace write never waits on it — and a failed sweep (a locked file,
// a full disk) is swallowed rather than taking the server down, the same stance observation's own
// sync takes on a briefly-unreadable project.
const int TraceRetentionDays = 7;
var tracePaths = app.Services.GetRequiredService<AppPaths>();
var traceProjects = app.Services.GetRequiredService<ProjectService>();
var traceRetention = new Timer(_ =>
{
    var cutoffUtcMs = DateTimeOffset.UtcNow.AddDays(-TraceRetentionDays).ToUnixTimeMilliseconds();
    foreach (var project in traceProjects.KnownProjects())
    {
        try
        {
            using var store = TraceStore.Open(tracePaths.TracesDb(project.ProductGuid));
            store.Prune(cutoffUtcMs);
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Trace retention sweep failed for {Guid}", project.ProductGuid);
        }
    }
}, null, TimeSpan.FromMinutes(10), TimeSpan.FromHours(1));
app.Lifetime.ApplicationStopping.Register(traceRetention.Dispose);

app.UseMcpOriginValidation();

// MapMcp defaults to "/". The path is pinned here because the Claude Code plugin declares
// this URL statically, so it must never move silently.
app.MapMcp("/mcp");

// A bind failure on mcpBindUrl - in practice, almost always port 7823 already held by something
// else - must never reach a user as a raw AddressInUseException stack trace (spec #1 §8's
// error-handling standard: "specific and actionable... rather than opaque error codes or
// tracebacks", applied here to startup instead of a tool call). See McpBinding.Run's own doc
// comment for the exact translation, and its own tests for the exact message this prints.
try
{
    McpBinding.Run(app, mcpBindUrl);
}
catch (McpPortBindException ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.Exit(1);
}

/// <summary>
/// Resolves which project's traces.db this call belongs to and records it — entirely inside its
/// own try/catch, so an ambiguous or unknown project (e.g. hades_status, which takes no 'project'
/// argument, on a server that knows several projects) is treated exactly like an unwritable trace
/// database: tracing is skipped for this call, never surfaced as a failure of the call itself.
/// </summary>
static void RecordTrace(RequestContext<CallToolRequestParams> context, string toolName, long startUtcMs,
    long durationMs, bool ok, string? errorMessage, CallToolResult? result)
{
    try
    {
        if (context.Services is not { } services) return;

        var projects = services.GetRequiredService<ProjectService>();
        var paths = services.GetRequiredService<AppPaths>();

        var projectHandle = context.Params.Arguments is { } arguments
            && arguments.TryGetValue("project", out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        var productGuid = ToolSupport.ResolveProject(projects, projectHandle);

        paths.EnsureProjectDir(productGuid);
        var tracer = new ToolCallTracer(paths.TracesDb(productGuid));
        tracer.RecordOutcome(toolName, SerializeArguments(context.Params.Arguments), startUtcMs, durationMs,
            ok, errorMessage, result);
    }
    catch
    {
        // Tracing must never fail the call it traces - see ToolCallTracer's class doc comment.
    }
}

static string? SerializeArguments(IDictionary<string, JsonElement>? arguments) =>
    arguments is null ? null : JsonSerializer.Serialize(arguments);

/// <summary>The text of a failed tool's error content, for the trace record — the same
/// human-readable text an agent calling the tool would see back, for the "graceful" IsError path
/// (a thrown exception is described from the exception itself instead - see the catch block
/// above).</summary>
static string DescribeError(CallToolResult result) =>
    string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text)) is { Length: > 0 } text
        ? text
        : "Tool reported an error.";

/// <summary>Exposed so WebApplicationFactory&lt;Program&gt; can host the app in tests.</summary>
public partial class Program;
