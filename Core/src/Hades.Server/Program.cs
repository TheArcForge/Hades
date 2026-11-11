using System.Diagnostics;
using System.Text.Json;
using Hades.Core;
using Hades.Core.Editors;
using Hades.Core.Projects;
using Hades.Core.Observation;
using Hades.Core.Storage;
using Hades.Core.Tracing;
using Hades.Server.Control;
using Hades.Server.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
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

// F15: hades_regression's recording seam, moved here from the attached Editor - see that class's
// own doc comment. A singleton (not scoped) because a recording session spans many separate HTTP
// requests (action='start' on one call, the calls being recorded on others, action='stop' on a
// later one) - a scoped/per-request instance would forget everything the instant 'start' returned.
builder.Services.AddSingleton<RegressionRecorder>();
builder.Services.AddSingleton(sp =>
    new EditorListener(sp.GetRequiredService<AppPaths>().EditorTokenFile, sp.GetRequiredService<EditorRegistry>(),
        sp.GetRequiredService<LeaseRegistry>(), sp.GetRequiredService<ProjectService>()));

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
    .AddMcpServer(options =>
    {
        // F5: left unset, the SDK reports the ENTRY ASSEMBLY's own version here (Hades.Server.dll's
        // AssemblyVersion, an incidental build artifact - "1.0.0.0" on the machine that found this
        // defect, something else on another build) rather than the product's own version. Wired
        // from HadesTools.ServerVersion - the existing constant, never a re-typed literal - so this
        // can never drift from what hades_status itself reports. Matters most exactly when the tool
        // list fails to load at all (F1): serverInfo.version is then the ONE version string a
        // tester can still read, and it must not lie about being a v1 server.
        options.ServerInfo = new Implementation { Name = "Hades", Version = HadesTools.ServerVersion };
    })
    .WithHttpTransport()
    .WithTools<HadesTools>()
    .WithTools<GraphTools>()
    .WithTools<SummaryTools>()
    .WithTools<SettingsTools>()
    .WithTools<InspectTool>()
    .WithTools<QueryTools>()
    .WithTools<SceneApplyTool>()
    .WithTools<MaterialApplyTool>()
    .WithTools<ScriptableObjectApplyTool>()
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

        // F13a: an unknown argument name (a typo, or a caller remembering a retired parameter
        // name) was previously silently dropped by the SDK's own parameter binding - a caller
        // asking for a filtered result got an unfiltered one that LOOKS filtered, with no error
        // anywhere. Refused here, BEFORE next() runs - the whole call, zero side effects - the
        // same "refused, not ignored" convention OperationFieldValidator already established one
        // level down, for an unrecognised FIELD inside a batch operation (see that class's own
        // doc comment); this is the missing sibling check for a top-level PARAMETER, which
        // nothing previously checked at all.
        if (UnknownParameterRejection(context) is { } rejection)
        {
            await RecordTraceForRefusalAsync(context, toolName, startUtcMs, stopwatch.ElapsedMilliseconds,
                DescribeError(rejection), rejection, cancellationToken).ConfigureAwait(false);
            return rejection;
        }

        // L1: a required TOP-LEVEL parameter the caller never sent at all. MissingRequiredFieldRejection
        // below walks only INTO arguments that WERE sent, so it cannot see this; the SDK's own binding
        // then failed with the opaque "An error occurred invoking '<tool>'." and nothing appended, for
        // the 19 of 33 tools that declare a required parameter - the single most common call mistake
        // there is. Refused here, in the same position and for the same reason as its two siblings;
        // see MissingRequiredParameterRejection's own doc comment.
        if (MissingRequiredParameterRejection(context) is { } missingParameterRejection)
        {
            await RecordTraceForRefusalAsync(context, toolName, startUtcMs, stopwatch.ElapsedMilliseconds,
                DescribeError(missingParameterRejection), missingParameterRejection, cancellationToken).ConfigureAwait(false);
            return missingParameterRejection;
        }

        // F7: a required field missing from a typed operation array (e.g. animation_apply's
        // setEvents sending 'functionName' instead of 'function') used to bind successfully at the
        // TOP level - 'operations' is itself just a JSON array - and only fail deep inside the SDK's
        // own per-element deserialization into the typed record, where a required-member failure
        // becomes a JsonException the SDK's tool dispatch already catches and reports as the opaque
        // "An error occurred invoking '<tool>'." (never this call's own actionable validation
        // messages - those never got a chance to run). Checked here instead, BEFORE next() ever
        // reaches that deserialization, the same "refused, not ignored" position as
        // UnknownParameterRejection just above - see MissingRequiredFieldRejection's own doc comment.
        if (MissingRequiredFieldRejection(context) is { } missingFieldRejection)
        {
            await RecordTraceForRefusalAsync(context, toolName, startUtcMs, stopwatch.ElapsedMilliseconds,
                DescribeError(missingFieldRejection), missingFieldRejection, cancellationToken).ConfigureAwait(false);
            return missingFieldRejection;
        }

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
            // both paths are traced as a failure. It is also exactly the shape a HOISTED guard
            // refusal takes (a tool validating and throwing McpException before ever calling
            // ToolSupport.ResolveProjectAsync — see SettingsTools.ProjectSettings's own comment on
            // why those checks run first) — see RecordTraceForRefusalAsync's own doc comment for
            // why that needs more than the synchronous fallback RecordTrace below still uses.
            await RecordTraceForRefusalAsync(context, toolName, startUtcMs, stopwatch.ElapsedMilliseconds,
                ex.Message, result: null, cancellationToken).ConfigureAwait(false);
            throw;
        }

        // Seamless project resolution's auto-adopt announcement: a tool that resolved its project
        // via ToolSupport.ResolveProjectAsync(..., context) and triggered auto-adopt recorded the
        // announcement on THIS call's own context.Items (RootsRouter.ResolveAsync's own doc
        // comment: "the tool result says so") — appended here, once, for every tool uniformly,
        // rather than each tool's own result type carrying an Announcement field. Before
        // RecordTrace/CaptureForRegression below, so both observe the SAME content a real caller
        // actually receives.
        result = ToolSupport.AppendAnnouncement(context, result);

        var errorMessage = result.IsError == true ? DescribeError(result) : null;
        RecordTrace(context, toolName, startUtcMs, stopwatch.ElapsedMilliseconds, errorMessage is null, errorMessage, result);

        // F15: hades_regression's recording seam. The SAME wrap-every-tool-dispatch position as
        // RecordTrace just above - see RegressionRecorder's own class doc comment for why this
        // layer, not the attached Editor's own wire dispatch, is what closes the graph/disk
        // observability gap. hades_regression's own calls are excluded here, not inside
        // RegressionRecorder itself, so a session recording other tools never records its own
        // start/stop/replay - the same exclusion CaptureIfRecording used to make wire-side.
        if (toolName != "hades_regression") CaptureForRegression(context, toolName, result);

        return result;
    });

    // tools/list payload-size cut: outputSchema is OPTIONAL per the MCP spec, and every tool here
    // carries one only because UseStructuredContent=true on its [McpServerTool] attribute asks for
    // both it AND CallToolResult.StructuredContent together (see McpServerToolCreateOptions.
    // UseStructuredContent's own doc comment) - the two are not independently toggleable on the
    // attribute. Mutating the registered McpServerTool.ProtocolTool.OutputSchema directly (the
    // same seam the boolean-schema-fix loop below uses for InputSchema/OutputSchema) was tried
    // first and reverted: the SDK's own tool-invocation path reads that SAME Tool instance to
    // decide whether to populate CallToolResult.StructuredContent, so nulling it there silently
    // stopped every real caller — and every test's own Structured(...) helper — from ever seeing
    // structured content again (217 test failures caught this, not a static read of the SDK).
    // This filter instead rewrites only the OUTGOING tools/list response - a fresh Tool copy per
    // tool, OutputSchema stripped - leaving the shared, registered McpServerTool.ProtocolTool
    // instances (and therefore every real invocation's StructuredContent) untouched. Advertising
    // it was ~24KB of a ~101KB payload at 33 tools - the single largest field, and almost entirely
    // redundant with prose the description/inputSchema already carry. Kept for
    // project_get_console_log alone: its outputSchema is the only place 'totalMatching' (whether
    // more buffered console entries exist than 'count' returned) is documented anywhere a caller
    // can discover without an extra call - see EditorProjectToolsTests.
    // ProjectGetConsoleLog_OutputSchema_DeclaresTotalMatching, which pins that one exception.
    options.Filters.Request.ListToolsFilters.Add(next => async (context, cancellationToken) =>
    {
        var result = await next(context, cancellationToken).ConfigureAwait(false);

        if (result.Tools is { } tools)
        {
            result.Tools = tools.Select(tool =>
                tool.OutputSchema is null || tool.Name == "project_get_console_log"
                    ? tool
                    : new Tool
                    {
                        Name = tool.Name,
                        Title = tool.Title,
                        Description = tool.Description,
                        InputSchema = tool.InputSchema,
                        OutputSchema = null,
                        Annotations = tool.Annotations,
                        Icons = tool.Icons,
                        Meta = tool.Meta,
                    }).ToList();
        }

        return result;
    });
});

var app = builder.Build();

// F1: a member typed object/JsonElement/JsonValue (InspectAssetResult.Value, SceneApplyOperation.
// Values, a Dictionary<string, object?>'s own additionalProperties, ...) exports as the JSON
// Schema boolean `true` ("matches anything") by default - legal JSON Schema, but real Claude Code
// validates tools/list CLIENT-SIDE and requires an object at every schema position, so one bare
// boolean anywhere drops the ENTIRE 32-tool list, not just the offending tool (verified externally:
// rewriting the one offending field, inspect_asset.outputSchema.properties.value, to `{}` restores
// every tool). 16 of these exist today, across both inputSchema and outputSchema, spread over
// several tools - see TransportConformanceTests.NoToolSchemaContainsABooleanSubschemaAnywhere for
// the exact, currently-live count and the durable structural guard this must keep satisfying.
//
// There is no clean creation-time hook for this: WithTools<T>() below never accepts a
// McpServerToolCreateOptions/AIJsonSchemaCreateOptions of its own (only an optional
// JsonSerializerOptions), and every property on both AIJsonSchemaCreateOptions AND
// AIJsonSchemaTransformOptions - including AIJsonSchemaCreateOptions.Default itself, the shared
// singleton schema generation falls back to - is init-only (verified empirically: assigning
// AIJsonSchemaCreateOptions.Default.TransformOptions post-construction is a compile error, CS8852),
// so there is no seam that runs BEFORE a tool's schema is built.
//
// This instead does exactly what the alternative the defect report itself allows for: post-process
// every already-built tool's schema once, here, right after ToolCollection is fully populated by
// the WithTools<T>() chain above and before the endpoint is mapped below, so no request ever
// observes an un-rewritten schema. Tool.InputSchema/OutputSchema are ordinary, publicly mutable
// JsonElement properties (unlike the create-time options above), and AIJsonUtilities.TransformSchema
// is the SDK's own standalone entry point for transforming an EXISTING schema - the identical logic
// ConvertBooleanSchemas drives during creation, exposed as a pure function - so the rewrite is
// authoritative (every genuine schema-valued position: properties/items/additionalProperties/...),
// never a hand-rolled walk that could miss a nesting shape or corrupt an unrelated boolean keyword
// (e.g. a future "readOnly": true) by treating every JSON boolean as if it were a subschema.
var booleanSchemaFix = new AIJsonSchemaTransformOptions { ConvertBooleanSchemas = true };
var registeredTools = app.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection;
if (registeredTools is not null)
{
    foreach (var tool in registeredTools)
    {
        tool.ProtocolTool.InputSchema = AIJsonUtilities.TransformSchema(tool.ProtocolTool.InputSchema, booleanSchemaFix);

        if (tool.ProtocolTool.OutputSchema is { } outputSchema)
            tool.ProtocolTool.OutputSchema = AIJsonUtilities.TransformSchema(outputSchema, booleanSchemaFix);
    }
}

// Any non-flag command-line argument is a Unity project path to adopt and index at startup.
// This is the walking skeleton's only way to tell the server about a project; continuous
// FSEvents discovery, Unity Hub import, and the control API all belong to later plans.
// Note this is deliberately NOT how a tool call picks its project — a call falls back to live
// MCP roots (ToolSupport.ResolveProjectAsync, RootsRouter.cs) only once an explicit handle and
// the sole-known-project case both come up empty. Roots themselves are not the reason this
// startup seam still exists: the SDK's [Obsolete] on them (MCP9005, SEP-2577) is an advisory,
// suppressible warning on a fully functional feature, not a working blocker — see
// Architecture.md §2.4 for the corrected writeup and how the two used to disagree on this.
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
// /control/ping is the ONLY signal CoreSupervisor (Mac/HadesSupervision) has for "did the
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
/// Resolves which project's traces.db this call belongs to and records it — preferring the guid
/// the tool itself already resolved (ToolSupport.ResolveProjectAsync leaves it on this call's own
/// context.Items, the only place a roots-based answer survives to; the synchronous fallback below
/// cannot re-derive one). Entirely inside its own try/catch, so a call that resolved nowhere by
/// either route (e.g. hades_status, which takes no 'project' argument, on a server that knows
/// several projects) is treated exactly like an unwritable trace database: tracing is skipped for
/// this call, never surfaced as a failure of the call itself.
/// </summary>
static void RecordTrace(RequestContext<CallToolRequestParams> context, string toolName, long startUtcMs,
    long durationMs, bool ok, string? errorMessage, CallToolResult? result)
{
    try
    {
        if (context.Services is not { } services) return;

        var projects = services.GetRequiredService<ProjectService>();
        var paths = services.GetRequiredService<AppPaths>();

        string productGuid;
        if (context.Items is { } items
            && items.TryGetValue(ToolSupport.ResolvedProjectItemsKey, out var resolved)
            && resolved is string alreadyResolved)
        {
            productGuid = alreadyResolved;
        }
        else
        {
            // Tools not routed through ResolveProjectAsync (the Editor-proxy family, whose
            // resolution lives in Hades.Core) leave nothing on Items; for those the old
            // synchronous resolution still answers exactly as before.
            var projectHandle = context.Params.Arguments is { } arguments
                && arguments.TryGetValue("project", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            productGuid = ToolSupport.ResolveProject(projects, projectHandle);
        }

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

/// <summary>
/// The failure-path counterpart to <see cref="RecordTrace"/> above, used ONLY by the filter's two
/// refusal sites (F13a's own rejection, and a tool call that threw all the way out to the filter's
/// own catch block) — never by the common call after <see cref="ToolSupport.AppendAnnouncement"/>,
/// which stays on <see cref="RecordTrace"/>, unchanged, exactly as it behaved before this method
/// existed: that call site already has a resolved project on <c>context.Items</c> whenever a tool
/// actually reached <see cref="ToolSupport.ResolveProjectAsync(ProjectService,string?,RequestContext{CallToolRequestParams})"/>,
/// so it never hits the gap this method exists to close.
///
/// A HOISTED refusal — one that validates and throws before ever calling that resolution (the whole
/// point of hoisting — see SettingsTools.ProjectSettings's own comment) — never stamps
/// <see cref="ToolSupport.ResolvedProjectItemsKey"/> on <c>context.Items</c>, so <see cref="RecordTrace"/>'s
/// own synchronous fallback is all that would be left, and it throws the instant a caller has 2+
/// known projects and no explicit 'project' argument — silently dropped by RecordTrace's own blanket
/// catch (RecordTrace itself is unchanged; this method has its own, identically-shaped one). This
/// recovers exactly that case: (1) an explicit 'project' argument still resolves synchronously, no
/// roots needed — byte-identical to today; (2) otherwise, a BEST-EFFORT, side-effect-free peek at
/// the client's current roots (<see cref="TryPeekKnownProjectFromRootsAsync"/> — deliberately never
/// <see cref="RootsRouter.ResolveAsync"/>, which can auto-adopt; see that method's own doc comment
/// for why this file implements its own peek instead) matches them against ALREADY-KNOWN projects
/// only. When neither names a project, the never-fail guarantee still holds — the trace is skipped,
/// same as today — but now with one structured log line naming the tool and why, so the drop itself
/// is diagnosable instead of silent.
/// </summary>
static async Task RecordTraceForRefusalAsync(RequestContext<CallToolRequestParams> context, string toolName,
    long startUtcMs, long durationMs, string? errorMessage, CallToolResult? result, CancellationToken cancellationToken)
{
    try
    {
        if (context.Services is not { } services) return;

        var projects = services.GetRequiredService<ProjectService>();

        var productGuid = ResolvedProjectFromItems(context) ?? TryResolveExplicitProjectHandle(context, projects);
        productGuid ??= await TryPeekKnownProjectFromRootsAsync(context, projects, cancellationToken).ConfigureAwait(false);

        if (productGuid is null)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger("Hades.Server.Tracing").LogInformation(
                "Trace dropped for {Tool}: refused before project resolution, and no already-known "
                + "project matched the client's current roots.", toolName);
            return;
        }

        var paths = services.GetRequiredService<AppPaths>();
        paths.EnsureProjectDir(productGuid);
        var tracer = new ToolCallTracer(paths.TracesDb(productGuid));
        tracer.RecordOutcome(toolName, SerializeArguments(context.Params.Arguments), startUtcMs, durationMs,
            ok: false, errorMessage, result);
    }
    catch
    {
        // Tracing must never fail the call it traces - see ToolCallTracer's class doc comment.
    }
}

/// <summary>The same <c>context.Items</c> read <see cref="RecordTrace"/> does above — factored out
/// only because <see cref="RecordTraceForRefusalAsync"/> needs the identical check before it ever
/// attempts a roots peek (a project already resolved by the SAME call, before it went on to throw,
/// must still win — see that method's own doc comment).</summary>
static string? ResolvedProjectFromItems(RequestContext<CallToolRequestParams> context) =>
    context.Items is { } items
        && items.TryGetValue(ToolSupport.ResolvedProjectItemsKey, out var resolved)
        && resolved is string alreadyResolved
        ? alreadyResolved
        : null;

/// <summary>An explicit 'project' argument, resolved synchronously — null on a missing argument OR
/// an unresolvable one (unknown handle), in which case <see cref="RecordTraceForRefusalAsync"/>
/// tries the roots peek next rather than giving up immediately; a caller that DID pass a handle
/// still deserves the chance for roots to name a project for tracing purposes, same as the
/// no-handle case.</summary>
static string? TryResolveExplicitProjectHandle(RequestContext<CallToolRequestParams> context, ProjectService projects)
{
    var handle = context.Params.Arguments is { } arguments
        && arguments.TryGetValue("project", out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    if (handle is null) return null;

    try
    {
        return ToolSupport.ResolveProject(projects, handle);
    }
    catch (McpException)
    {
        return null;
    }
}

/// <summary>
/// A BEST-EFFORT, side-effect-free peek at the calling client's current MCP roots, used only for
/// naming a trace's project on <see cref="RecordTraceForRefusalAsync"/>'s two refusal paths —
/// deliberately NOT <see cref="RootsRouter.ResolveAsync"/>, which this file must not call here: it
/// calls <see cref="ProjectService.Adopt"/> on every match it makes, even one against an
/// ALREADY-known project (re-persisting project.json, importing .arcforge memory the first time,
/// and raising <see cref="ProjectService.ProjectAdopted"/>, which <c>ObservationService</c> listens
/// to — see <see cref="ProjectService.Adopt"/>'s own doc comment) — real writes a mere OBSERVABILITY
/// decision (which traces.db to file a dropped call's trace under) must never trigger. This instead
/// matches each reported root against ALREADY-REGISTERED projects only, with pure path arithmetic —
/// a root that is not already one of <see cref="ProjectService.KnownProjects"/>'s own stored paths
/// matches nothing here, on purpose; naming a project for a trace must never itself register one.
/// Ambiguous (roots naming more than one distinct known project) also matches nothing, same as "no
/// roots capability" or a timeout — every one of those collapses to the same "could not resolve"
/// null a caller treats identically.
///
/// Reuses the exact DI-substitution seam
/// <see cref="ToolSupport.ResolveProjectAsync(ProjectService,string?,RequestContext{CallToolRequestParams})"/>
/// itself uses (<c>services.GetService&lt;IRootsProvider&gt;()</c> first, a real
/// <see cref="McpRootsProvider"/> over this call's own live <see cref="McpServer"/> otherwise), so
/// the same test seam (a fake roots provider registered in DI) that already proves the success path
/// also proves this one — no live MCP client roots capability needed to test it.
/// </summary>
static async Task<string?> TryPeekKnownProjectFromRootsAsync(RequestContext<CallToolRequestParams> context,
    ProjectService projects, CancellationToken cancellationToken)
{
    var known = projects.KnownProjects();
    if (known.Count == 0) return null;
    if (context.Services is not { } services || context.Server is not { } server) return null;

    var rootsProvider = services.GetService<IRootsProvider>() ?? new McpRootsProvider(server);

    // Bounded independently of the caller's own cancellationToken (which, at this point, belongs to
    // a call that already finished — refused or thrown — so it must never let a slow/unresponsive
    // client hang the response a refusal is about to return). Same default duration as RootsRouter's
    // own RootsRequestTimeout, for the same "short enough not to matter, long enough for a real
    // client" reasoning — this is a fresh, uncached request every time (see this method's own
    // "why not RootsRouter" note above), so unlike RootsRouter there is no cache to keep this rare.
    IReadOnlyList<string> roots;
    using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
    {
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            roots = await rootsProvider.GetRootsAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // The 3s timeout fired, not the caller's own cancellation.
        }
    }

    if (roots.Count == 0) return null;

    var matchedGuids = new HashSet<string>(StringComparer.Ordinal);
    foreach (var root in roots)
    {
        if (MatchAgainstKnownProjectsOnly(root, known) is { } guid) matchedGuids.Add(guid);
    }

    return matchedGuids.Count == 1 ? matchedGuids.Single() : null;
}

/// <summary>Equals-or-nested match of one reported root against every already-known project — the
/// deepest (longest/most specific) match wins when several known projects nest inside one another,
/// mirroring <see cref="RootsRouter"/>'s own private <c>MatchAgainstKnownProjects</c> (duplicated
/// here, smaller in scope and never adopting — see <see cref="TryPeekKnownProjectFromRootsAsync"/>'s
/// own doc comment for why this file cannot just call that one). Never reads a project's
/// ProjectSettings.asset off disk, unlike RootsRouter's own auto-adopt path — an unknown root simply
/// matches nothing here.</summary>
static string? MatchAgainstKnownProjectsOnly(string reportedRoot, IReadOnlyList<Hades.Core.Projects.UnityProject> known)
{
    var canonicalRoot = CanonicalizeForPeek(reportedRoot);

    string? deepestGuid = null;
    var deepestLength = -1;

    foreach (var project in known)
    {
        // ProjectService.KnownProjects's own Path is ALREADY fully canonical (ProjectStore.Adopt
        // resolves it at adoption time - every path component, not just the leaf) - canonicalizing
        // it again here is idempotent, and keeping the same call on both sides (rather than trusting
        // it is already resolved) is what makes this correct even if that ever changes.
        var knownPath = CanonicalizeForPeek(project.Path);

        var contains = canonicalRoot.Equals(knownPath, StringComparison.Ordinal)
            || canonicalRoot.StartsWith(knownPath + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (!contains) continue;

        if (knownPath.Length > deepestLength)
        {
            deepestGuid = project.ProductGuid;
            deepestLength = knownPath.Length;
        }
    }

    return deepestGuid;
}

/// <summary>
/// The trace-naming peek's canonicalization — a thin delegate to
/// <see cref="ProjectStore.Canonicalize"/>, the method that decides what
/// <see cref="ProjectService.KnownProjects"/>'s own <c>UnityProject.Path</c> values actually look
/// like once stored. Peek-time matching and adopt-time storage must resolve a path identically or
/// a known project silently stops matching here — see ProjectStore.Canonicalize's own doc comment
/// for that invariant and how two drifting copies already broke it once for RootsRouter; one shared
/// implementation keeps it true by construction. The one local addition: a root string
/// <see cref="Path.GetFullPath(string)"/> rejects outright (Adopt rightly lets that throw) must
/// here degrade to "matches nothing" — a canonicalization failure must never break the
/// trace-naming peek this exists for, only make it match less.
/// </summary>
static string CanonicalizeForPeek(string path)
{
    try
    {
        return ProjectStore.Canonicalize(path);
    }
    catch (ArgumentException)
    {
        return path;
    }
}

/// <summary>
/// F15: offers one completed tool call to <see cref="RegressionRecorder"/> - a no-op unless a
/// recording session is currently active (see that class's own Capture). Entirely inside its own
/// try/catch, same guarantee as <see cref="RecordTrace"/> just above and for the same reason:
/// capturing a call for later replay must never be what makes that call fail.
/// </summary>
static void CaptureForRegression(RequestContext<CallToolRequestParams> context, string toolName, CallToolResult result)
{
    try
    {
        if (context.Services?.GetService<RegressionRecorder>() is { } recorder)
            recorder.Capture(toolName, context.Params.Arguments, result);
    }
    catch
    {
        // Never fails the call being captured - see this method's own doc comment.
    }
}

/// <summary>The text of a failed tool's error content, for the trace record — the same
/// human-readable text an agent calling the tool would see back, for the "graceful" IsError path
/// (a thrown exception is described from the exception itself instead - see the catch block
/// above).</summary>
static string DescribeError(CallToolResult result) =>
    string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text)) is { Length: > 0 } text
        ? text
        : "Tool reported an error.";

/// <summary>
/// F13a: null when every argument name on <paramref name="context"/>'s call is a real parameter of
/// the matched tool; otherwise a ready-to-return error <see cref="CallToolResult"/> naming the
/// unknown parameter(s) and the tool's actual ones, so a caller can self-correct without guessing.
/// Reads the accepted parameter names from <c>context.MatchedPrimitive</c>'s own
/// <c>ProtocolTool.InputSchema</c> - the EXACT schema <c>tools/list</c> already advertises for this
/// tool, via its own <c>properties</c> keys - rather than a second, hand-maintained list that could
/// silently drift from it as tools gain or rename parameters. <c>MatchedPrimitive</c> is only ever
/// an <see cref="McpServerTool"/> here (CallToolFilters wraps dispatch to an already name-matched
/// tool - see this file's own comment on CallToolFilters, above - an unknown TOOL name never reaches
/// this filter at all), checked with an <c>is</c> pattern anyway rather than assumed, so a future SDK
/// change that widens what can be matched degrades to "skip the check" instead of throwing.
/// </summary>
static CallToolResult? UnknownParameterRejection(RequestContext<CallToolRequestParams> context)
{
    if (context.Params.Arguments is not { Count: > 0 } arguments) return null;
    if (context.MatchedPrimitive is not McpServerTool tool) return null;

    if (!tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties)
        || properties.ValueKind != JsonValueKind.Object)
    {
        return null;
    }

    var unknown = arguments.Keys.Where(key => !properties.TryGetProperty(key, out _)).ToList();
    if (unknown.Count == 0) return null;

    var validNames = properties.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
    var quotedUnknown = string.Join(", ", unknown.Select(name => $"'{name}'"));
    var noun = unknown.Count == 1 ? "a parameter" : "parameters";

    return new CallToolResult
    {
        IsError = true,
        Content =
        [
            new TextContentBlock
            {
                Text = $"{context.Params.Name} does not accept {noun} named {quotedUnknown}. "
                     + $"Valid parameters: {string.Join(", ", validNames)}.",
            },
        ],
    };
}

/// <summary>
/// L1: null when every parameter the matched tool's own JSON Schema marks <c>required</c> at the TOP
/// level is present in <c>context.Params.Arguments</c>; otherwise a ready-to-return error
/// <see cref="CallToolResult"/> naming the missing parameter(s), the tool's full parameter list, and
/// which of them are required - so a caller can self-correct without guessing.
///
/// <para>The ROOT-level sibling of <see cref="MissingRequiredFieldRejection"/> just below, which
/// iterates the arguments the caller actually SENT and walks into each one; a required parameter
/// that was never sent at all appears nowhere in that iteration, so nothing checked it. The SDK's
/// own binding then failed with the opaque "An error occurred invoking '&lt;tool&gt;'." and nothing
/// appended - confirmed live against 6 tools, with 19 of 33 declaring a required parameter and so
/// exposed - found by the 2026-11-10 pre-merge live pass (finding L1; that pass's own notes live
/// under the gitignored docs/, so the substance is restated here rather than referenced). Reads
/// <c>tool.ProtocolTool.InputSchema</c>, the SAME schema <c>tools/list</c> advertises and both
/// siblings already read, rather than a second hand-maintained list.</para>
///
/// <para>Unlike both siblings this deliberately runs even when the call carried NO arguments at
/// all: a tool with a required parameter called with <c>{}</c> is exactly the case that must be
/// refused, and their shared <c>Arguments is not { Count: &gt; 0 }</c> early-out is what let it
/// through. <c>graph_query</c>, which declares no <c>required</c> and validates its filter
/// combination by hand, is unaffected - no <c>required</c> array means nothing to enforce.</para>
///
/// <para>Presence only, never the value's type - the same deliberate narrowness
/// <see cref="MissingRequiredFieldRejection"/> documents for itself. A present-but-null required
/// parameter counts as sent; whether null is acceptable is the tool's own business, and reporting
/// it here would claim the caller omitted something they did not.</para>
/// </summary>
static CallToolResult? MissingRequiredParameterRejection(RequestContext<CallToolRequestParams> context)
{
    if (context.MatchedPrimitive is not McpServerTool tool) return null;

    var schema = tool.ProtocolTool.InputSchema;
    if (!schema.TryGetProperty("required", out var requiredElement) || requiredElement.ValueKind != JsonValueKind.Array)
    {
        return null;
    }

    var required = requiredElement.EnumerateArray()
        .Select(element => element.GetString())
        .Where(name => !string.IsNullOrEmpty(name))
        .Select(name => name!)
        .ToList();
    if (required.Count == 0) return null;

    var arguments = context.Params.Arguments;
    var missing = required.Where(name => arguments is null || !arguments.ContainsKey(name)).ToList();
    if (missing.Count == 0) return null;

    var validNames = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
        ? properties.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToList()
        : required;
    var quotedMissing = string.Join(", ", missing.Select(name => $"'{name}'"));
    var noun = missing.Count == 1 ? "parameter" : "parameters";

    return new CallToolResult
    {
        IsError = true,
        Content =
        [
            new TextContentBlock
            {
                Text = $"{context.Params.Name} is missing required {noun} {quotedMissing}. "
                     + $"Valid parameters: {string.Join(", ", validNames)}; "
                     + $"required: {string.Join(", ", required)}.",
            },
        ],
    };
}

/// <summary>
/// F7: null when every 'required' property the matched tool's OWN JSON Schema names (at any
/// nesting depth reachable from a top-level argument the caller actually sent - typically a typed
/// operation array's own element type, e.g. animation_apply's setEvents 'events[i]') is genuinely
/// present in <c>context.Params.Arguments</c>; otherwise a ready-to-return error <see
/// cref="CallToolResult"/> naming the exact position and the missing field, so a caller can
/// self-correct without the SDK's own opaque "An error occurred invoking '&lt;tool&gt;'." (which
/// this specifically stands in front of - see this filter's own comment at the call site).
///
/// <para>Driven entirely by <c>tool.ProtocolTool.InputSchema</c> - the SAME schema <c>tools/list</c>
/// already advertises, and the SAME one <see cref="UnknownParameterRejection"/> reads one level up -
/// rather than a per-tool, hand-maintained field list that could drift from the DTOs' own
/// <c>required</c> members. A tool's typed operation record marks a member <c>required</c>
/// (AnimationEventSpec.Function, SceneManageOperation has none, etc.) precisely so ITS OWN schema
/// already states this; this check simply enforces what the schema already promises, before the
/// SDK's own strict binding gets a chance to enforce the identical rule with a far worse
/// error.</para>
///
/// <para>Deliberately narrow: only checks PRESENCE of a required key, never its VALUE's type - a
/// present-but-wrong-typed field (e.g. a string where 'time' wants a number) is a different failure
/// this does not attempt to cover, and still falls through to the SDK's own (opaque) handling. Only
/// walks into an object/array the CALLER actually sent (<c>TryGetProperty</c> on the instance) - an
/// omitted OPTIONAL nested value is never treated as if it were sent empty.</para>
/// </summary>
static CallToolResult? MissingRequiredFieldRejection(RequestContext<CallToolRequestParams> context)
{
    if (context.Params.Arguments is not { Count: > 0 } arguments) return null;
    if (context.MatchedPrimitive is not McpServerTool tool) return null;

    if (!tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties)
        || properties.ValueKind != JsonValueKind.Object)
    {
        return null;
    }

    foreach (var argument in arguments)
    {
        // An unrecognised top-level argument name is UnknownParameterRejection's own job, just
        // above - skip it here rather than reporting it twice under a different message.
        if (!properties.TryGetProperty(argument.Key, out var parameterSchema)) continue;

        if (FindMissingRequiredField(parameterSchema, argument.Value, argument.Key) is not { } missing) continue;

        return new CallToolResult
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = $"{context.Params.Name}: '{missing.Path}' is missing required field '{missing.Field}'. "
                         + $"Valid fields there: {missing.ValidFields}.",
                },
            ],
        };
    }

    return null;
}

/// <summary>Recursively walks <paramref name="instance"/> (a value the caller actually sent) against
/// its own <paramref name="schema"/>, depth-first, returning the FIRST missing-required-property
/// violation found - array items are walked by index (against the array schema's own "items"
/// sub-schema), object properties by name (against that property's own sub-schema), matching
/// exactly the nesting <c>System.Text.Json</c>'s own required-member deserialization would walk.
///
/// <para>Dispatches on the INSTANCE's own JSON kind plus the schema's "items"/"properties" shape,
/// never on the schema's own "type" keyword: a nullable array/object member's generated schema
/// states <c>"type": ["array", "null"]</c> (a JSON array of strings, not the single string a plain
/// non-nullable member gets) - checking for the literal string "array" would silently never match
/// any of these typed operation DTOs, since every field but 'op' itself is nullable.</para></summary>
static MissingRequiredField? FindMissingRequiredField(JsonElement schema, JsonElement instance, string path)
{
    if (schema.ValueKind != JsonValueKind.Object) return null;

    if (instance.ValueKind == JsonValueKind.Array)
    {
        if (!schema.TryGetProperty("items", out var itemSchema)) return null;

        var index = 0;
        foreach (var item in instance.EnumerateArray())
        {
            if (FindMissingRequiredField(itemSchema, item, $"{path}[{index}]") is { } found) return found;
            index++;
        }

        return null;
    }

    if (instance.ValueKind != JsonValueKind.Object) return null;

    if (schema.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
    {
        foreach (var requiredNameElement in requiredElement.EnumerateArray())
        {
            var requiredName = requiredNameElement.GetString();
            if (requiredName is null || instance.TryGetProperty(requiredName, out _)) continue;

            var validFields = schema.TryGetProperty("properties", out var ownProperties) && ownProperties.ValueKind == JsonValueKind.Object
                ? string.Join(", ", ownProperties.EnumerateObject().Select(p => p.Name))
                : "(none declared)";
            return new MissingRequiredField(path, requiredName, validFields);
        }
    }

    if (schema.TryGetProperty("properties", out var childProperties) && childProperties.ValueKind == JsonValueKind.Object)
    {
        foreach (var childProperty in childProperties.EnumerateObject())
        {
            if (instance.TryGetProperty(childProperty.Name, out var childInstance)
                && FindMissingRequiredField(childProperty.Value, childInstance, $"{path}.{childProperty.Name}") is { } found)
            {
                return found;
            }
        }
    }

    return null;
}

/// <summary>One JSON Schema "required" violation MissingRequiredFieldRejection found: 'Path' names
/// where (e.g. "operations[0].events[1]"), 'Field' names the missing property, 'ValidFields' lists
/// every property that position's own schema actually accepts.</summary>
readonly record struct MissingRequiredField(string Path, string Field, string ValidFields);

/// <summary>Exposed so WebApplicationFactory&lt;Program&gt; can host the app in tests.</summary>
public partial class Program;
