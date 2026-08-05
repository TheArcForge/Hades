using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Hades.Core;
using Hades.Core.Editors;
using Hades.Core.Storage;
using Hades.Server.Mcp;

namespace Hades.Server.Control;

/// <summary>
/// The app-side loopback HTTP+JSON listener the control API is served from - a second listener in
/// the same process as the MCP endpoint (see Program.cs), separately authenticated, for the Swift
/// shell (spec #3) and a future <c>hades</c> CLI. Distinct from the MCP endpoint on purpose:
/// different consumer, different trust boundary, different lifecycle (spec #1 §7) - the MCP
/// endpoint serves a language model with read plus scoped project mutation, while this one can add
/// and remove projects, force-release a reload lease, edit authored memory, and change settings.
/// Collapsing them onto one port would make that difference invisible.
///
/// Security posture mirrors <see cref="Hades.Core.Editors.EditorListener"/> throughout - loopback-only
/// binding, a fresh random token per <see cref="Start"/> written to a 0600 discovery file,
/// constant-time token comparison, Origin validation - adapted from a raw TCP handshake to ASP.NET
/// Core minimal APIs, and from a one-time handshake to a per-request check (see
/// <see cref="ControlAuth"/> for both middlewares and the discovery-file format).
/// </summary>
public sealed class ControlListener : IDisposable
{
    readonly string _connectionFilePath;
    readonly int _requestedPort;
    readonly ProjectService _projects;
    readonly LeaseRegistry _leases;
    readonly Func<DateTimeOffset> _utcNow;
    readonly ProjectsEndpoint.ProcessLauncher _launchProcess;
    readonly EditorProxy _editorProxy;
    readonly OperationRegistry _operations;
    readonly IConfiguration _configuration;
    readonly int _actualMcpPort;

    WebApplication? _app;
    bool _disposed;

    /// <param name="connectionFilePath">Where the discovery file (port + token) is written on
    /// every <see cref="Start"/> - typically <see cref="Hades.Core.Storage.AppPaths.ControlTokenFile"/>.</param>
    /// <param name="port">TCP port to bind, or 0 (the default) to let the OS assign a free one -
    /// the actual bound port is read back via <see cref="Port"/> after <see cref="Start"/>. There
    /// is no fixed control-API port: unlike the MCP endpoint (7823, statically declared by the
    /// Claude Code plugin), nothing needs to know this port in advance - clients discover it from
    /// the connection file, exactly as the editor link's plugin does.</param>
    /// <param name="projects">Backs every project-aware control endpoint (currently just
    /// <c>/control/summary</c> - see <see cref="SummaryEndpoint"/>). Optional, like
    /// <see cref="Hades.Core.ProjectService"/>'s own <c>EditorRegistry</c> parameter: Program.cs
    /// always supplies the app's real, shared instance (see its own ControlListener registration),
    /// but a caller that only cares about auth/Origin/ping (every existing ControlAuthTests case)
    /// should not have to construct one. The default is rooted in a fresh temp directory, never
    /// this machine's real application-data folder - <see cref="ProjectService.KnownProjects"/>
    /// degrades to an empty list when that directory does not exist, so the default is inert
    /// unless actually queried.</param>
    /// <param name="leases">Same optional-with-a-safe-default convention as
    /// <paramref name="projects"/>; a fresh <see cref="LeaseRegistry"/> is in-memory only and
    /// already believes nothing is held.</param>
    /// <param name="utcNow">"Now" for every elapsed/remaining-time computation
    /// <see cref="SummaryEndpoint"/> makes. Defaults to the real clock; overridable so tests can
    /// pin exact seconds instead of tolerating wall-clock jitter.</param>
    /// <param name="launchProcess">Backs <c>/control/projects/{id}/revealInFinder</c> and
    /// <c>.../openInUnity</c> - see <see cref="ProjectsEndpoint.ProcessLauncher"/>'s own doc
    /// comment. Defaults to <see cref="ProjectsEndpoint.DefaultProcessLauncher"/>, the real
    /// implementation; tests substitute a recording fake so they never actually spawn Finder or
    /// Unity.</param>
    /// <param name="editorProxy">Backs <c>POST /control/leases/{id}/release</c> - see
    /// <see cref="EditorsEndpoint.ReleaseAsync"/>. Same optional-with-a-safe-default convention as
    /// <paramref name="projects"/>: Program.cs always supplies the app's real, shared instance (the
    /// exact same one <see cref="Hades.Server.Mcp.EditorProjectTools"/> and every other
    /// Editor-dependent MCP tool uses), sharing its own <see cref="EditorRegistry"/> with whatever
    /// <see cref="Hades.Core.ProjectService"/> the caller passed - see Program.cs's own
    /// ControlListener registration for why passing one WITHOUT the other would silently break
    /// release (an <see cref="EditorProxy"/> built over a DIFFERENT, empty <see cref="EditorRegistry"/>
    /// than the one <paramref name="projects"/> uses internally would never see any Editor
    /// <paramref name="projects"/> itself reports as attached). A caller that only cares about the
    /// read-only surfaces (every existing test before this parameter existed) gets the same
    /// fresh-and-inert default <paramref name="projects"/> itself uses.</param>
    /// <param name="operations">Backs <c>GET /control/operations/{id}</c> and
    /// <c>POST /control/projects/{id}/rebuild</c> (see <see cref="Operations"/> and
    /// <see cref="ProjectsEndpoint.Rebuild"/>) - the SAME instance both routes must share, since an
    /// operation id <c>rebuild</c> hands back is only pollable through a registry that actually
    /// started it (see <see cref="OperationRegistry"/>'s own class doc comment for the gap Plan 11
    /// Task 5 closes). Defaults to a fresh, empty, real-clock <see cref="OperationRegistry"/> - safe
    /// for every existing caller that never touches long operations at all.</param>
    /// <param name="configuration">Backs <c>GET /control/settings</c>'s logLevel field (see
    /// <see cref="SettingsEndpoint"/>) - the app's real, running configuration
    /// (appsettings.json's own <c>Logging:LogLevel:Default</c>), so that field reflects what this
    /// process is actually logging at rather than a second, independently-maintained copy. Defaults
    /// to a fresh, empty <see cref="ConfigurationBuilder"/> result - <see cref="SettingsEndpoint.Build"/>
    /// already falls back to "Information" when a key is absent, so this default is inert, matching
    /// every other optional dependency's own safe-default convention.</param>
    /// <param name="actualMcpPort">Backs <c>GET /control/settings</c>'s mcpPort.inUse field (see
    /// <see cref="SettingsEndpoint.Build"/>'s own doc comment for the defect this fixes) - the port
    /// THIS process's own MCP endpoint is actually bound to. Program.cs supplies
    /// <see cref="Mcp.McpBinding.ResolveBoundPort"/> of <see cref="Mcp.McpBinding.ResolveBindUrl"/>'s
    /// own result. Defaults to <see cref="SettingsEndpoint.McpPort"/> (7823) - the ordinary,
    /// unmodified case every existing caller of this constructor before this parameter existed
    /// implicitly assumed, and still the correct assumption for any caller that does not override
    /// it.</param>
    public ControlListener(string connectionFilePath, int port = 0,
        ProjectService? projects = null, LeaseRegistry? leases = null, Func<DateTimeOffset>? utcNow = null,
        ProjectsEndpoint.ProcessLauncher? launchProcess = null, EditorProxy? editorProxy = null,
        OperationRegistry? operations = null, IConfiguration? configuration = null,
        int actualMcpPort = SettingsEndpoint.McpPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionFilePath);
        _connectionFilePath = connectionFilePath;
        _requestedPort = port;
        _projects = projects ?? new ProjectService(new AppPaths(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())));
        _leases = leases ?? new LeaseRegistry();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _launchProcess = launchProcess ?? ProjectsEndpoint.DefaultProcessLauncher;
        _editorProxy = editorProxy ?? new EditorProxy(_projects, new EditorRegistry());
        _operations = operations ?? new OperationRegistry();
        _configuration = configuration ?? new ConfigurationBuilder().Build();
        _actualMcpPort = actualMcpPort;
    }

    /// <summary>The port actually bound - valid only after <see cref="Start"/>.</summary>
    public int Port { get; private set; }

    /// <summary>The token generated by the most recent <see cref="Start"/>, kept in memory so
    /// callers (tests, an in-process CLI) do not need to re-read the discovery file.</summary>
    public string Token { get; private set; } = "";

    /// <summary>Generates a fresh token, starts a loopback-only Kestrel host behind Origin and
    /// token middleware, and writes the actual bound port together with the token to the
    /// connection file at mode 0600.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_app is not null) throw new InvalidOperationException("ControlListener is already started.");

        Token = ControlAuth.GenerateToken();

        // No args passed through: WebApplication.CreateBuilder() - unlike the outer app's
        // CreateBuilder(args) in Program.cs - never parses the process's own command-line
        // arguments, which are Unity project paths meant for the OTHER host, not this one.
        var builder = WebApplication.CreateBuilder();

        // 127.0.0.1 specifically, never 0.0.0.0: the same security boundary EditorListener binds
        // with IPAddress.Loopback, expressed here as a URL host because Kestrel's dynamic-port
        // ("0") support is configured through WebHost.UseUrls rather than a raw socket bind. There
        // is deliberately no way to override this - see this class's own doc comment.
        builder.WebHost.UseUrls($"http://127.0.0.1:{_requestedPort}");

        // Every control response follows the same "resolved, not ingredients" rule
        // SummaryEndpoint's own class doc comment describes: a field the shell should treat as
        // simply not there (e.g. SummaryResult.Lease when nothing is held) must actually be
        // absent from the JSON, not present-as-null for the shell to check itself. ASP.NET Core's
        // own web defaults do NOT omit nulls (verified: DefaultIgnoreCondition defaults to Never
        // even under CreateBuilder()'s "web defaults"), so this is set explicitly, once, for every
        // endpoint this listener maps - not per-response.
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

        var app = builder.Build();

        // Origin, then token: a foreign-origin request is rejected before its token is even
        // inspected. Both run before any endpoint is mapped below, so neither can be bypassed by
        // a future route that forgets to opt in - see ControlAuth's own doc comments.
        app.UseControlOriginValidation();
        app.UseControlTokenAuth(Token);

        // The one endpoint Task 1 adds: proves the auth path end to end. Deliberately touches no
        // project and no database - same reasoning as hades_ping (SummaryTools.Ping): it answers
        // "is the app alive" when the database is the suspect, so it must not share a failure mode
        // with the thing it would be diagnosing.
        app.MapGet("/control/ping", () =>
        {
            using var process = Process.GetCurrentProcess();
            var uptime = DateTime.UtcNow - process.StartTime.ToUniversalTime();
            return new PingResult { Version = HadesTools.ServerVersion, UptimeSeconds = uptime.TotalSeconds };
        });

        // Task 2: the menu bar's one endpoint - the smallest surface in the control API and, per
        // the plan, the reason the app exists. See SummaryEndpoint's own class doc comment for the
        // pure-resolve/async-orchestrate split; this listener only ever calls the orchestrator.
        app.MapGet("/control/summary", () => SummaryEndpoint.BuildAsync(_projects, _leases, _utcNow));

        // Task 3: the Projects surface (spec #3 §3.2) - per-project state, resolved warnings, and
        // its six actions. See ProjectsEndpoint's own class doc comment for the pure-resolve/
        // async-orchestrate split and every action's design decisions; this listener only ever
        // calls into it, same division of labour as /control/summary above.
        app.MapGet("/control/projects", () => ProjectsEndpoint.BuildAsync(_projects, _utcNow));
        app.MapPost("/control/projects/add", (AddProjectRequest request) => ProjectsEndpoint.AddAsync(_projects, _utcNow, request));
        app.MapPost("/control/projects/{productGuid}/remove", (string productGuid) => ProjectsEndpoint.Remove(_projects, productGuid));
        app.MapPost("/control/projects/{productGuid}/rebuild", (string productGuid) => ProjectsEndpoint.Rebuild(_projects, _operations, productGuid));
        app.MapPost("/control/projects/{productGuid}/installPlugin", (string productGuid) => ProjectsEndpoint.InstallPluginAsync(_projects, productGuid));
        app.MapPost("/control/projects/{productGuid}/revealInFinder", (string productGuid) => ProjectsEndpoint.RevealInFinder(_projects, productGuid, _launchProcess));
        app.MapPost("/control/projects/{productGuid}/openInUnity", (string productGuid) => ProjectsEndpoint.OpenInUnity(_projects, productGuid, _launchProcess));

        // Task 4: attached editors and force-release - the Release button behind the menu bar's
        // most prominent row (/control/summary's own "lease" object - see SummaryEndpoint's own
        // updated doc comment). See EditorsEndpoint's own class doc comment for the full design,
        // including why "{id}" is the holding project's productGuid.
        app.MapGet("/control/editors", () => EditorsEndpoint.BuildAsync(_projects));
        app.MapPost("/control/leases/{id}/release", (string id) => EditorsEndpoint.ReleaseAsync(_projects, _leases, _editorProxy, id));

        // Task 5: the poll side of every long control-API action - see Operations' own class doc
        // comment. Shares _operations with the rebuild route above, which is what makes an
        // operation id rebuild just returned actually pollable here, not a 404 against an empty,
        // unrelated registry.
        app.MapGet("/control/operations/{id}", (string id) => Operations.Get(_operations, id, _utcNow));

        // Task 6: settings - current values and resolved conflict states (spec #3 §3.5). See
        // SettingsEndpoint's own class doc comment for exactly what each field means and why two
        // of the spec's six settings are deliberately absent here. _actualMcpPort is this
        // process's own real MCP bind port (see this class's own constructor doc comment) - what
        // makes mcpPort.inUse a trustworthy signal rather than an unwinnable self-probe.
        app.MapGet("/control/settings", () => SettingsEndpoint.Build(_configuration, actualMcpPort: _actualMcpPort));

        // Task 6: traces, sequence-first (spec #3 §3.3) - see TracesEndpoint's own class doc
        // comment for the full design. The three literal-segment routes below are always matched
        // ahead of the "{traceId}" route that follows regardless of registration order (ASP.NET
        // Core's routing prefers a literal segment over a route parameter at the same position),
        // so "sequences"/"slow"/"failures" can never be mistaken for a trace id.
        app.MapGet("/control/traces/sequences", (string? project, string? tool, string? outcome,
                long? minDurationMs, long? maxDurationMs, int limit = 200) =>
            TracesEndpoint.GetSequences(_projects, project, tool, outcome, minDurationMs, maxDurationMs, limit));
        app.MapGet("/control/traces/slow", (string? project, int limit = 20) =>
            TracesEndpoint.GetSlowTools(_projects, project, limit));
        app.MapGet("/control/traces/failures", (string? project, int limit = 50) =>
            TracesEndpoint.GetFailures(_projects, project, limit));
        app.MapGet("/control/traces/{traceId}", (string traceId, string? project) =>
            TracesEndpoint.GetTraceDetail(_projects, project, traceId));

        // Task 6: memory (spec #3 §3.4) - list documents, read/write one, and the proposal queue's
        // Accept/Dismiss/Defer. See MemoryEndpoint's own class doc comment for the full design,
        // including why name/fileName are query-string parameters rather than route segments.
        app.MapGet("/control/memory", (string? project) => MemoryEndpoint.Get(_projects, project, _utcNow));
        app.MapGet("/control/memory/document", (string? project, string? name) =>
            MemoryEndpoint.GetDocument(_projects, project, name));
        app.MapPost("/control/memory/document", (string? project, string? name, WriteMemoryDocumentRequest request) =>
            MemoryEndpoint.WriteDocument(_projects, project, name, request));
        app.MapPost("/control/memory/proposals/accept", (string? project, string? fileName) =>
            MemoryEndpoint.AcceptProposal(_projects, project, fileName));
        app.MapPost("/control/memory/proposals/dismiss", (string? project, string? fileName, bool confirm = false) =>
            MemoryEndpoint.DismissProposal(_projects, project, fileName, confirm));
        app.MapPost("/control/memory/proposals/defer", (string? project, string? fileName) =>
            MemoryEndpoint.DeferProposal(_projects, project, fileName));

        // Synchronous Start(), not RunAsync/StartAsync: mirrors EditorListener.Start() and lets
        // Program.cs start this listener the same unawaited way it starts that one. IHost.Start()
        // is the standard blocking-until-listening wrapper over StartAsync().
        app.Start();
        _app = app;

        // The actual bound port, resolved only now that Start() has asked Kestrel to bind - see
        // EditorListener.Start's own comment on why writing the connection file any earlier would
        // leak a pre-bind placeholder.
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Port = new Uri(addresses.Single()).Port;

        ControlAuth.WriteConnectionFile(_connectionFilePath, Port, Token);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_app is not { } app) return;

        // An already-cancelled token skips Kestrel's graceful drain of in-flight/keep-alive
        // connections: this listener's contract, like EditorListener's, is immediate teardown so
        // nothing outlives the process - not a leisurely wait for callers to disconnect.
        app.StopAsync(new CancellationToken(canceled: true)).GetAwaiter().GetResult();
        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
