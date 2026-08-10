using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hades.Contract.Wire;
using Hades.Core;
using Hades.Core.Editors;
using Hades.Core.Storage;
using Hades.Server.Control;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests.Control;

/// <summary>
/// Pure, deterministic tests of <see cref="SummaryEndpoint.Resolve"/> - the precedence and
/// string-formatting logic behind <c>GET /control/summary</c>, exercised directly against
/// hand-built <see cref="ProjectSnapshot"/> inputs with a fixed <see cref="DateTimeOffset"/>, no
/// clock, no sockets, no HTTP. This is deliberately where the strict "verbatim" assertions live
/// (see the plan's own "the test that matters most"): every expected string below is a literal,
/// hand-typed constant, never built by concatenating or formatting a field pulled from the SAME
/// response under test - doing that would let a future regression that moves this formatting to
/// the client (e.g. returning raw counts/timestamps instead of a resolved string) pass unnoticed.
/// See <see cref="SummaryBuildAsyncTests"/> for proof this logic is actually fed real
/// <see cref="ProjectService"/>/<see cref="LeaseRegistry"/> state, and
/// <see cref="SummaryEndpointHttpTests"/>/<see cref="SummaryProgramWiringTests"/> for proof the
/// route itself is wired, authenticated, and reachable.
/// </summary>
public sealed class SummaryResolveTests
{
    static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    static LeaseStatus Lease(DateTimeOffset acquiredAtUtc, DateTimeOffset expiresAtUtc) => new()
    {
        ProductGuid = "guid",
        LeaseId = "lease-id",
        AcquiredAtUtc = acquiredAtUtc,
        ExpiresAtUtc = expiresAtUtc,
    };

    // ---------------------------------------------------------------- no projects

    [Fact]
    public void NoProjects_IsAWellFormedIdleResponse_NotAnEmptyArrayToInterpret()
    {
        var result = SummaryEndpoint.Resolve([], Now);

        Assert.Equal(ControlIconState.Idle, result.IconState);
        Assert.Equal("No projects yet — add a Unity project to get started.", result.Headline);
        Assert.Empty(result.Rows);
        Assert.Null(result.Lease);
    }

    // ---------------------------------------------------------------- the plan's own example, verbatim

    [Fact]
    public void HeldLease_HeadlineAndLeaseObject_MatchThePlansOwnExampleExactly()
    {
        // Mirrors Plan 11 Task 2's own example response byte-for-byte on the parts it specifies:
        // one attached, recently-indexed project holding a lease for 12s with 18s left. Every
        // assertion is a literal - see this class's own doc comment for why that matters here
        // specifically.
        var snapshot = new ProjectSnapshot
        {
            Name = "Hades-Unity-Client",
            PathExists = true,
            Attached = true,
            Busy = false,
            LastIndexedUtc = Now.AddMinutes(-2),
            Lease = Lease(acquiredAtUtc: Now.AddSeconds(-12), expiresAtUtc: Now.AddSeconds(18)),
        };

        var result = SummaryEndpoint.Resolve([snapshot], Now);

        Assert.Equal(ControlIconState.LeaseHeld, result.IconState);
        Assert.Equal("Holding script reload for Hades-Unity-Client — 12s", result.Headline);

        var row = Assert.Single(result.Rows);
        Assert.Equal("Hades-Unity-Client", row.Project);
        Assert.Equal("Editor attached · indexed 2m ago", row.Status);
        Assert.Equal(ControlSeverity.Ok, row.Severity);

        Assert.NotNull(result.Lease);
        Assert.Equal("Hades-Unity-Client", result.Lease!.Project);
        Assert.Equal("guid", result.Lease.LeaseId); // Lease(...)'s own hardcoded ProductGuid, above
        Assert.Equal(12, result.Lease.HeldForSeconds);
        Assert.Equal(18, result.Lease.ExpiresInSeconds);
        Assert.True(result.Lease.Releasable);
    }

    // ---------------------------------------------------------------- Task 4: leaseId names which lease to release

    [Fact]
    public void LeaseId_IsTheHoldingProjectsProductGuid_DisambiguatesAcrossProjects()
    {
        // Plan 11 Task 4's own resolution of the question Task 2 left open: the plugin's OWN
        // lease id is a single constant regardless of which project holds it (ReloadGate's
        // ScriptEditingLeaseId, "hades-script-editing") - it cannot tell two projects' held leases
        // apart. The project's productGuid can: ReloadGate allows at most one held lease per
        // Editor/project, so productGuid is already a correct, sufficient per-lease identity - the
        // SAME key LeaseRegistry itself already uses. Proven here via two INDEPENDENT
        // single-project resolutions producing two DIFFERENT ids for the SAME plugin-side lease id.
        var alpha = new ProjectSnapshot
        {
            Name = "Alpha", PathExists = true, Attached = true, Busy = false, LastIndexedUtc = Now,
            Lease = new LeaseStatus { ProductGuid = "guid-alpha", LeaseId = "hades-script-editing", AcquiredAtUtc = Now.AddSeconds(-1), ExpiresAtUtc = Now.AddSeconds(29) },
        };
        var beta = new ProjectSnapshot
        {
            Name = "Beta", PathExists = true, Attached = true, Busy = false, LastIndexedUtc = Now,
            Lease = new LeaseStatus { ProductGuid = "guid-beta", LeaseId = "hades-script-editing", AcquiredAtUtc = Now.AddSeconds(-1), ExpiresAtUtc = Now.AddSeconds(29) },
        };

        var alphaResult = SummaryEndpoint.Resolve([alpha], Now);
        var betaResult = SummaryEndpoint.Resolve([beta], Now);

        Assert.Equal("guid-alpha", alphaResult.Lease!.LeaseId);
        Assert.Equal("guid-beta", betaResult.Lease!.LeaseId);
        Assert.NotEqual(alphaResult.Lease.LeaseId, betaResult.Lease.LeaseId);
    }

    // ---------------------------------------------------------------- every row wording, pinned literally

    [Fact]
    public void NotAttached_Indexed_RowStatusIsExactLiteral()
    {
        var snapshot = new ProjectSnapshot
        {
            Name = "Hades-Unity-Client", PathExists = true, Attached = false, Busy = false,
            LastIndexedUtc = Now.AddSeconds(-45),
        };

        var result = SummaryEndpoint.Resolve([snapshot], Now);

        Assert.Equal(ControlIconState.Idle, result.IconState);
        Assert.Equal("No Unity Editor attached", result.Headline);
        Assert.Equal("No Editor attached · indexed 45s ago", Assert.Single(result.Rows).Status);
        Assert.Equal(ControlSeverity.Ok, Assert.Single(result.Rows).Severity);
    }

    [Fact]
    public void AttachedAndBusy_RowStatusIsExactLiteral_SeverityWarning_NotIdle()
    {
        // Plan 7's three-state distinction: busy must never read as idle - neither in severity
        // nor in iconState.
        var snapshot = new ProjectSnapshot
        {
            Name = "Hades-Unity-Client", PathExists = true, Attached = true, Busy = true,
            LastIndexedUtc = Now.AddMinutes(-2),
        };

        var result = SummaryEndpoint.Resolve([snapshot], Now);

        Assert.Equal(ControlIconState.Attached, result.IconState);
        var row = Assert.Single(result.Rows);
        Assert.Equal("Editor attached (busy) · indexed 2m ago", row.Status);
        Assert.Equal(ControlSeverity.Warning, row.Severity);
    }

    [Fact]
    public void NeverIndexed_RowStatusIsExactLiteral_AndIconStateIsIndexing()
    {
        var snapshot = new ProjectSnapshot
        {
            Name = "Hades-Unity-Client", PathExists = true, Attached = false, Busy = false,
            LastIndexedUtc = null,
        };

        var result = SummaryEndpoint.Resolve([snapshot], Now);

        Assert.Equal(ControlIconState.Indexing, result.IconState);
        Assert.Equal("Indexing Hades-Unity-Client…", result.Headline);
        Assert.Equal("No Editor attached · not yet indexed", Assert.Single(result.Rows).Status);
    }

    [Fact]
    public void PathMissing_RowStatusIsExactLiteral_AndIconStateIsError()
    {
        var snapshot = new ProjectSnapshot
        {
            Name = "Hades-Unity-Client", PathExists = false, Attached = false, Busy = false,
            LastIndexedUtc = Now.AddMinutes(-5),
        };

        var result = SummaryEndpoint.Resolve([snapshot], Now);

        Assert.Equal(ControlIconState.Error, result.IconState);
        Assert.Equal("Hades-Unity-Client: project path not found — check that the volume is mounted.", result.Headline);
        var row = Assert.Single(result.Rows);
        Assert.Equal("Project path not found — check that the volume is mounted.", row.Status);
        Assert.Equal(ControlSeverity.Error, row.Severity);
    }

    // ---------------------------------------------------------------- Error headline with more than one project

    [Fact]
    public void PathMissing_TwoProjects_HeadlineIsACountNotARepeatOfTheRowBelow()
    {
        // The live bug report, verbatim: a healthy "Hades-Unity-Client" plus a second project
        // named "project" whose path is missing. The old headline was
        // "project: project path not found — check that the volume is mounted." - the exact same
        // sentence as the row directly beneath it. The fix must show neither that sentence nor any
        // other row's status text - only a count.
        var healthy = new ProjectSnapshot
        {
            Name = "Hades-Unity-Client", PathExists = true, Attached = true, Busy = false,
            LastIndexedUtc = Now.AddSeconds(-12),
        };
        var broken = new ProjectSnapshot
        {
            Name = "project", PathExists = false, Attached = false, Busy = false, LastIndexedUtc = Now.AddMinutes(-5),
        };

        var result = SummaryEndpoint.Resolve([healthy, broken], Now);

        Assert.Equal(ControlIconState.Error, result.IconState);
        Assert.Equal("1 of 2 projects needs attention", result.Headline);
        Assert.DoesNotContain(result.Rows, row => row.Status == result.Headline);
        Assert.DoesNotContain("project path not found", result.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PathMissing_ThreeProjectsTwoBroken_HeadlineCountsBothAndPluralizesNeed()
    {
        var healthy = new ProjectSnapshot { Name = "A", PathExists = true, Attached = true, Busy = false, LastIndexedUtc = Now };
        var brokenOne = new ProjectSnapshot { Name = "B", PathExists = false, Attached = false, Busy = false, LastIndexedUtc = Now };
        var brokenTwo = new ProjectSnapshot { Name = "C", PathExists = false, Attached = false, Busy = false, LastIndexedUtc = Now };

        var result = SummaryEndpoint.Resolve([healthy, brokenOne, brokenTwo], Now);

        Assert.Equal(ControlIconState.Error, result.IconState);
        Assert.Equal("2 of 3 projects need attention", result.Headline);
    }

    // ---------------------------------------------------------------- precedence: one condition at a time

    public static IEnumerable<object[]> SingleConditionCases()
    {
        // (pathMissing, hasLease, neverIndexed, attached) -> expected iconState, ALL OTHER
        // projects absent - proves each condition's own outcome in isolation. The cross-project
        // Facts below prove the RANKING when two conditions land on two different projects at once.
        yield return new object[] { true, false, false, false, ControlIconState.Error };
        yield return new object[] { false, true, false, false, ControlIconState.LeaseHeld };
        yield return new object[] { false, false, true, false, ControlIconState.Indexing };
        yield return new object[] { false, false, false, true, ControlIconState.Attached };
        yield return new object[] { false, false, false, false, ControlIconState.Idle };
    }

    [Theory]
    [MemberData(nameof(SingleConditionCases))]
    public void SingleConditionOnOneProject_ProducesTheExpectedIconState(
        bool pathMissing, bool hasLease, bool neverIndexed, bool attached, ControlIconState expected)
    {
        var snapshot = new ProjectSnapshot
        {
            Name = "P",
            PathExists = !pathMissing,
            Attached = attached,
            Busy = false,
            LastIndexedUtc = neverIndexed ? null : Now.AddMinutes(-5),
            Lease = hasLease ? Lease(Now.AddSeconds(-1), Now.AddSeconds(29)) : null,
        };

        var result = SummaryEndpoint.Resolve([snapshot], Now);

        Assert.Equal(expected, result.IconState);
    }

    // ---------------------------------------------------------------- precedence: across different projects

    [Fact]
    public void ErrorOnOneProject_OutranksLeaseHeldOnAnother_ButTheLeaseStaysVisible()
    {
        // Net #7 of the reload-safety design: a user must never be confused about why their code
        // stopped compiling. A DIFFERENT project's worse problem must not hide THIS project's
        // held lease - the `lease` field is independent of which icon wins.
        var broken = new ProjectSnapshot
        {
            Name = "Broken", PathExists = false, Attached = false, Busy = false, LastIndexedUtc = Now.AddMinutes(-5),
        };
        var leased = new ProjectSnapshot
        {
            Name = "Leased", PathExists = true, Attached = true, Busy = false, LastIndexedUtc = Now.AddMinutes(-5),
            Lease = Lease(Now.AddSeconds(-3), Now.AddSeconds(27)),
        };

        var result = SummaryEndpoint.Resolve([broken, leased], Now);

        Assert.Equal(ControlIconState.Error, result.IconState);
        Assert.NotNull(result.Lease);
        Assert.Equal("Leased", result.Lease!.Project);
        Assert.Equal(3, result.Lease.HeldForSeconds);
        Assert.Equal(27, result.Lease.ExpiresInSeconds);
    }

    [Fact]
    public void LeaseHeldOnOneProject_OutranksIndexingOnAnother()
    {
        var indexing = new ProjectSnapshot { Name = "Indexing", PathExists = true, Attached = false, Busy = false, LastIndexedUtc = null };
        var leased = new ProjectSnapshot
        {
            Name = "Leased", PathExists = true, Attached = true, Busy = false, LastIndexedUtc = Now.AddMinutes(-1),
            Lease = Lease(Now.AddSeconds(-2), Now.AddSeconds(28)),
        };

        var result = SummaryEndpoint.Resolve([indexing, leased], Now);

        Assert.Equal(ControlIconState.LeaseHeld, result.IconState);
    }

    [Fact]
    public void IndexingOnOneProject_OutranksAttachedOnAnother()
    {
        var indexing = new ProjectSnapshot { Name = "Indexing", PathExists = true, Attached = false, Busy = false, LastIndexedUtc = null };
        var attached = new ProjectSnapshot { Name = "Attached", PathExists = true, Attached = true, Busy = false, LastIndexedUtc = Now.AddMinutes(-1) };

        var result = SummaryEndpoint.Resolve([indexing, attached], Now);

        Assert.Equal(ControlIconState.Indexing, result.IconState);
    }

    [Fact]
    public void AttachedOnOneProject_OutranksIdleOnAnother()
    {
        var idle = new ProjectSnapshot { Name = "Idle", PathExists = true, Attached = false, Busy = false, LastIndexedUtc = Now.AddMinutes(-1) };
        var attached = new ProjectSnapshot { Name = "Attached", PathExists = true, Attached = true, Busy = false, LastIndexedUtc = Now.AddMinutes(-1) };

        var result = SummaryEndpoint.Resolve([idle, attached], Now);

        Assert.Equal(ControlIconState.Attached, result.IconState);
    }

    [Fact]
    public void MultipleSimultaneousLeases_PicksTheOneExpiringSoonest()
    {
        var soon = new ProjectSnapshot
        {
            Name = "Soon", PathExists = true, Attached = true, Busy = false, LastIndexedUtc = Now.AddMinutes(-1),
            Lease = Lease(Now.AddSeconds(-25), Now.AddSeconds(5)),
        };
        var later = new ProjectSnapshot
        {
            Name = "Later", PathExists = true, Attached = true, Busy = false, LastIndexedUtc = Now.AddMinutes(-1),
            Lease = Lease(Now.AddSeconds(-1), Now.AddSeconds(50)),
        };

        // Deliberately passed in "Later, Soon" order - the choice must come from urgency
        // (soonest to expire), not input order.
        var result = SummaryEndpoint.Resolve([later, soon], Now);

        Assert.Equal(ControlIconState.LeaseHeld, result.IconState);
        Assert.Equal("Soon", result.Lease!.Project);
        Assert.Equal(5, result.Lease.ExpiresInSeconds);
        Assert.Equal("Holding script reload for Soon — 25s", result.Headline);
    }
}

/// <summary>
/// Proves <see cref="SummaryEndpoint.BuildAsync"/> - the async orchestration layer between real
/// <see cref="ProjectService"/>/<see cref="LeaseRegistry"/> state and the pure
/// <see cref="SummaryEndpoint.Resolve"/> tested above - actually reuses
/// <see cref="ProjectService.GetCharonStatus"/> rather than a second attached/busy detector: a
/// real <see cref="EditorRegistry"/> and a real (lightweight, directly-constructed - see
/// EditorSessionTests for the same technique) <see cref="EditorSession"/> drive the SAME busy
/// probe hades_charon_status answers. No HTTP, no <see cref="ControlListener"/>, no
/// WebApplicationFactory - see <see cref="SummaryEndpointHttpTests"/> and
/// <see cref="SummaryProgramWiringTests"/> for those.
/// </summary>
public sealed class SummaryBuildAsyncTests : IDisposable
{
    const string ProjectGuid = "aaaabbbbccccddddeeeeffff10000001";

    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly List<IDisposable> _toDispose = [];
    readonly List<TcpListener> _listeners = [];
    readonly EditorRegistry _editorRegistry = new();
    readonly ProjectService _projects;

    public SummaryBuildAsyncTests()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"), $"  productGUID: {ProjectGuid}\n");

        _projects = new ProjectService(new AppPaths(_appRoot), _editorRegistry)
        {
            CharonProbeTimeout = TimeSpan.FromMilliseconds(300),
        };
    }

    static Hello MakeHello() => new()
    {
        ProjectGuid = ProjectGuid,
        ProjectPath = "/tmp/fake-unity-project",
        UnityVersion = "6000.3.2f1",
        PluginVersion = "1.2.0",
        ProcessId = 4321,
    };

    /// <summary>A real loopback socket pair wrapped directly in an <see cref="EditorSession"/> and
    /// registered into <see cref="_editorRegistry"/> - the same construction technique
    /// EditorSessionTests uses, deliberately skipping EditorListener's token+hello wire handshake
    /// since this class is testing <see cref="SummaryEndpoint.BuildAsync"/>, not the handshake.</summary>
    async Task<(StreamReader UnityReads, StreamWriter UnityWrites)> RegisterFakeEditorAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);

        var acceptTask = listener.AcceptTcpClientAsync();
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var server = await acceptTask;
        _toDispose.Add(client);
        _toDispose.Add(server);

        var session = new EditorSession(server.GetStream(), MakeHello());
        _toDispose.Add(session);
        session.Start();

        var unityReads = new StreamReader(client.GetStream(), new UTF8Encoding(false));
        var unityWrites = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        _editorRegistry.Register(new AttachedEditor { Hello = session.Hello, ConnectedAtUtc = DateTimeOffset.UtcNow, Session = session });

        return (unityReads, unityWrites);
    }

    static Task RespondToNextProbeAsync(StreamReader reads, StreamWriter writes) => Task.Run(async () =>
    {
        var line = await reads.ReadLineAsync();
        if (line is not null && JsonRpcRequest.TryParse(line, out var request, out _) && request is not null)
        {
            await writes.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(request.Id!, JsonValue.Bool(true)).ToJson()));
        }
    });

    [Fact]
    public async Task NotAttached_ProjectIndexed_RowSaysNoEditorAttached()
    {
        _projects.AdoptAndIndex(_projectRoot);

        var result = await SummaryEndpoint.BuildAsync(_projects, new LeaseRegistry(), () => DateTimeOffset.UtcNow);

        Assert.Equal(ControlIconState.Idle, result.IconState);
        var row = Assert.Single(result.Rows);
        Assert.Contains("No Editor attached", row.Status);
        Assert.Equal(ControlSeverity.Ok, row.Severity);
    }

    [Fact]
    public async Task AttachedAndResponsive_ReusesGetCharonStatus_RowReflectsAttached()
    {
        _projects.AdoptAndIndex(_projectRoot);
        var (unityReads, unityWrites) = await RegisterFakeEditorAsync();
        var responder = RespondToNextProbeAsync(unityReads, unityWrites);

        var result = await SummaryEndpoint.BuildAsync(_projects, new LeaseRegistry(), () => DateTimeOffset.UtcNow);
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ControlIconState.Attached, result.IconState);
        var row = Assert.Single(result.Rows);
        Assert.Contains("Editor attached", row.Status);
        Assert.DoesNotContain("busy", row.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ControlSeverity.Ok, row.Severity);
    }

    [Fact]
    public async Task AttachedButBusy_ReusesGetCharonStatus_RowIsWarningAndSaysBusy()
    {
        _projects.AdoptAndIndex(_projectRoot);
        await RegisterFakeEditorAsync();
        // Deliberately never answers the probe - the same "busy" condition CharonStatusTests'
        // own EditorAttachedButMainThreadBlocked_ReportsBusyNotGone proves at the tool level.

        var result = await SummaryEndpoint.BuildAsync(_projects, new LeaseRegistry(), () => DateTimeOffset.UtcNow);

        Assert.Equal(ControlIconState.Attached, result.IconState);
        var row = Assert.Single(result.Rows);
        Assert.Contains("busy", row.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ControlSeverity.Warning, row.Severity);
    }

    [Fact]
    public async Task NeverIndexed_IsIndexingIconState()
    {
        _projects.Adopt(_projectRoot); // registered, never indexed

        var result = await SummaryEndpoint.BuildAsync(_projects, new LeaseRegistry(), () => DateTimeOffset.UtcNow);

        Assert.Equal(ControlIconState.Indexing, result.IconState);
        Assert.Contains("not yet indexed", Assert.Single(result.Rows).Status);
    }

    [Fact]
    public async Task HeldLease_IsSurfaced_ReleasableReflectsWhetherAnEditorIsAttached()
    {
        _projects.AdoptAndIndex(_projectRoot);
        var leases = new LeaseRegistry();
        leases.RecordHeld(ProjectGuid, "lease-build-async", DateTimeOffset.UtcNow.AddSeconds(30));

        var result = await SummaryEndpoint.BuildAsync(_projects, leases, () => DateTimeOffset.UtcNow);

        Assert.Equal(ControlIconState.LeaseHeld, result.IconState);
        Assert.NotNull(result.Lease);
        Assert.False(result.Lease!.Releasable); // no Editor attached in this scenario
    }

    [Fact]
    public async Task PathMissing_AfterVolumeUnmount_IsErrorIconState()
    {
        _projects.AdoptAndIndex(_projectRoot);
        Directory.Delete(_projectRoot, recursive: true); // simulate the volume going away

        var result = await SummaryEndpoint.BuildAsync(_projects, new LeaseRegistry(), () => DateTimeOffset.UtcNow);

        Assert.Equal(ControlIconState.Error, result.IconState);
        Assert.Equal(ControlSeverity.Error, Assert.Single(result.Rows).Severity);
    }

    public void Dispose()
    {
        foreach (var d in _toDispose) d.Dispose();
        foreach (var l in _listeners) l.Stop();

        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}

/// <summary>
/// <c>GET /control/summary</c> over real HTTP against a directly-constructed
/// <see cref="ControlListener"/> - same style as ControlAuthTests, proving auth/Origin/wiring
/// rather than re-proving the resolution logic already covered above.
/// </summary>
public sealed class SummaryEndpointHttpTests : IDisposable
{
    readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    string ConnectionFilePath => Path.Combine(_tempDir, "control.token");

    static HttpRequestMessage SummaryRequest(string? token, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/control/summary");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    static HttpClient ClientFor(ControlListener listener) => new() { BaseAddress = new Uri($"http://127.0.0.1:{listener.Port}") };

    [Fact]
    public async Task NoToken_IsRefused()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(SummaryRequest(token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ForeignOrigin_IsRejectedWith403_EvenWithAValidToken()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(SummaryRequest(listener.Token, origin: "https://evil.example.com"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ValidToken_NoProjectsAdopted_Returns200WithTheWellFormedIdleBody()
    {
        // Deliberately supplies neither `projects` nor `leases`: exercises ControlListener's own
        // safe, isolated default (see its constructor's doc comment) - proving that default never
        // touches this machine's real ~/Library/Application Support/Hades, yet still answers
        // correctly, and that `lease` is genuinely ABSENT (not JSON null) when nothing is held.
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(SummaryRequest(listener.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("idle", body.GetProperty("iconState").GetString());
        Assert.Equal("No projects yet — add a Unity project to get started.", body.GetProperty("headline").GetString());
        Assert.Equal(0, body.GetProperty("rows").GetArrayLength());
        Assert.False(body.TryGetProperty("lease", out _), "lease must be ABSENT, not present-as-null, when nothing is held");
    }

    [Fact]
    public async Task ValidToken_HeldLease_ReflectsOverRealHttp_EnumsSerializeAsLowerCamelCase()
    {
        var projectRoot = Path.Combine(_tempDir, "proj");
        Directory.CreateDirectory(Path.Combine(projectRoot, "ProjectSettings"));
        const string guid = "aaaabbbbccccddddeeeeffff20000002";
        File.WriteAllText(Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset"), $"  productGUID: {guid}\n");

        var projectService = new ProjectService(new AppPaths(Path.Combine(_tempDir, "app")));
        projectService.AdoptAndIndex(projectRoot);

        var leases = new LeaseRegistry();
        leases.RecordHeld(guid, "lease-http", DateTimeOffset.UtcNow.AddSeconds(30));

        using var listener = new ControlListener(ConnectionFilePath, projects: projectService, leases: leases);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(SummaryRequest(listener.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("leaseHeld", body.GetProperty("iconState").GetString());

        Assert.True(body.TryGetProperty("lease", out var lease));
        Assert.Equal(Path.GetFileName(projectRoot), lease.GetProperty("project").GetString());
        Assert.Equal(guid, lease.GetProperty("leaseId").GetString());
        Assert.True(lease.GetProperty("heldForSeconds").GetInt32() >= 0);
        Assert.False(lease.GetProperty("releasable").GetBoolean()); // no Editor attached here

        var row = Assert.Single(body.GetProperty("rows").EnumerateArray());
        Assert.Equal("ok", row.GetProperty("severity").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}

/// <summary>
/// Proves Program.cs actually threads the app's real, shared <see cref="ProjectService"/> and
/// <see cref="LeaseRegistry"/> singletons into <see cref="ControlListener"/> - the same division
/// of labour ControlListenerProgramWiringTests uses for the listener itself: the direct-
/// construction tests above cover the endpoint's behaviour in isolation, this covers the wiring.
/// </summary>
public sealed class SummaryProgramWiringTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    const string ProjectGuid = "aaaabbbbccccddddeeeeffff30000003";

    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public SummaryProgramWiringTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"), $"  productGUID: {ProjectGuid}\n");

        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppPaths>();
                services.AddSingleton(new AppPaths(_appRoot));
            }));
    }

    [Fact]
    public async Task ControlListener_SeesTheSameProjectServiceEverythingElseInTheAppUses()
    {
        var projects = _factory.Services.GetRequiredService<ProjectService>();
        projects.AdoptAndIndex(_projectRoot);

        var listener = _factory.Services.GetRequiredService<ControlListener>();
        var port = await ProgramWiringPort.WaitAsync(listener);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var request = new HttpRequestMessage(HttpMethod.Get, "/control/summary");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", listener.Token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = Assert.Single(body.GetProperty("rows").EnumerateArray());
        Assert.Equal(Path.GetFileName(_projectRoot.TrimEnd(Path.DirectorySeparatorChar)), row.GetProperty("project").GetString());
    }

    public void Dispose()
    {
        // See EditorToolTestBase.Dispose's own comment: _factory is a fresh per-test
        // WebApplicationFactory whose own background services can still be touching
        // _appRoot/_projectRoot until the host itself is disposed - which must happen before
        // the recursive delete below.
        _factory.Dispose();

        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}

/// <summary>
/// The gap found during Plan 11 Task 4: EditorListener deregistered a disconnected Editor from
/// EditorRegistry but never told LeaseRegistry the Editor was gone, so /control/summary kept
/// showing a held lease - and its Release button - for a lease that no longer existed (the
/// plugin's own ReloadGate.ReleaseOnDisconnect, plan 8, frees the real lock immediately on
/// socket death). This is the same defect class as plan 9's hades_charon_status.leaseHeld gap,
/// pointing the other way: the mechanism is right and the VISIBILITY lies - exactly what net #7
/// of the reload-safety design exists to prevent.
///
/// Real Program.cs wiring (WebApplicationFactory), a real EditorListener dialed into over a real
/// loopback socket exactly as EditorsProgramWiringTests does (reusing EditorToolTestBase rather
/// than a third copy of the fake-Unity dial-in) - this is the one property none of
/// SummaryResolveTests/SummaryBuildAsyncTests (plain data/direct calls) or EditorListenerTests
/// (no HTTP) can prove alone: that a real disconnect on the app's real EditorListener is actually
/// visible through the app's real /control/summary route.
/// </summary>
public sealed class SummaryLeaseClearedOnDisconnectTests(WebApplicationFactory<Program> factory) : EditorToolTestBase(factory)
{
    static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }

        return condition();
    }

    static async Task<JsonElement> GetSummaryAsync(HttpClient client, ControlListener listener)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/control/summary");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", listener.Token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task EditorDisconnects_LeaseRowDisappearsFromControlSummary()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        // Connect first, record the believed lease SECOND - the realistic order (see
        // EditorsProgramWiringTests.ControlListener_ReleaseAction_...'s own comment on why).
        Factory.Services.GetRequiredService<LeaseRegistry>()
            .RecordHeld(ProjectGuid, "hades-script-editing", DateTimeOffset.UtcNow.AddSeconds(30));

        var listener = Factory.Services.GetRequiredService<ControlListener>();
        var port = await ProgramWiringPort.WaitAsync(listener);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        // Before disconnect: the lease row is present, same as HeldLease_... proves at the
        // Resolve layer above, now confirmed over the real route with a real attached Editor.
        var before = await GetSummaryAsync(client, listener);
        Assert.Equal("leaseHeld", before.GetProperty("iconState").GetString());
        Assert.True(before.TryGetProperty("lease", out var beforeLease));
        Assert.Equal(ProjectGuid, beforeLease.GetProperty("leaseId").GetString());

        // The Editor disconnects - disposing the reader/writer closes the underlying
        // NetworkStream (both wrap the same client.GetStream() instance - see
        // EditorToolTestBase.ConnectAsFakeUnityAsync), the same real socket teardown
        // EditorListenerTests drives via TcpClient.Close(). ReloadGate.ReleaseOnDisconnect frees
        // the ACTUAL lock on the plugin side the moment this happens (proven live in plan 8);
        // this app's own BELIEVED lease must not outlive it.
        reads.Dispose();
        writes.Dispose();

        Assert.True(await Eventually(() => Factory.Services.GetRequiredService<LeaseRegistry>().Get(ProjectGuid) is null),
            "LeaseRegistry still believes the lease is held after the Editor disconnected");

        var after = await GetSummaryAsync(client, listener);
        Assert.False(after.TryGetProperty("lease", out _),
            "the lease row must be gone once the Editor that held it disconnected - the Release button now points at nothing");
        Assert.NotEqual("leaseHeld", after.GetProperty("iconState").GetString());
    }
}
