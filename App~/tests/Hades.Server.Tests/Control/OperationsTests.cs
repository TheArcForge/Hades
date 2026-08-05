using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hades.Core;
using Hades.Core.Storage;
using Hades.Server.Control;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests.Control;

/// <summary>
/// Direct, deterministic tests of <see cref="OperationRegistry"/> - the in-memory start/poll/prune
/// mechanism behind every long-running control-API action (today, only <c>rebuild</c> - see
/// <see cref="ProjectsEndpoint.Rebuild"/>). No HTTP, no <see cref="ControlListener"/> - see
/// <see cref="OperationsGetTests"/> for the wire-shape translation and
/// <see cref="OperationsEndpointHttpTests"/>/<see cref="RebuildOperationRoundTripTests"/> for proof
/// this is actually reachable over the real route and actually closes Plan 11 Task 3's own gap (an
/// operation id <c>rebuild</c> returned but nothing could poll).
/// </summary>
public sealed class OperationRegistryTests
{
    [Fact]
    public async Task Start_ReturnsAnIdImmediately_NeverBlocksOnTheWorkItself()
    {
        var gate = new TaskCompletionSource();
        var registry = new OperationRegistry();

        var stopwatch = Stopwatch.StartNew();
        var id = registry.Start("test", () => { gate.Task.GetAwaiter().GetResult(); return "unused"; });
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Start must return before the work completes - the gate is not set yet, so blocking would hang. Took {stopwatch.Elapsed}.");
        Assert.False(string.IsNullOrWhiteSpace(id));

        gate.SetResult(); // release the background work so the test does not leak a hung task
        await registry.WhenComplete(id);
    }

    [Fact]
    public void Start_WhileRunning_GetReportsRunningState_NoErrorNoResult()
    {
        var gate = new TaskCompletionSource();
        var registry = new OperationRegistry();

        var id = registry.Start("rebuild", () => { gate.Task.GetAwaiter().GetResult(); return "unused"; });

        var op = registry.Get(id);
        Assert.NotNull(op);
        Assert.Equal("rebuild", op!.Kind);
        Assert.Equal(OperationState.Running, op.State);
        Assert.Null(op.FinishedAtUtc);
        Assert.Null(op.Error);
        Assert.Null(op.Result);

        gate.SetResult();
    }

    [Fact]
    public async Task Start_WorkSucceeds_GetReportsDoneWithTheResult()
    {
        var registry = new OperationRegistry();

        var id = registry.Start("test", () => "the-result");
        await registry.WhenComplete(id);

        var op = registry.Get(id);
        Assert.NotNull(op);
        Assert.Equal(OperationState.Done, op!.State);
        Assert.NotNull(op.FinishedAtUtc);
        Assert.Null(op.Error);
        Assert.Equal("the-result", op.Result);
    }

    [Fact]
    public async Task Start_WorkThrows_GetReportsFailedWithTheExceptionsMessage_ActionableNotOpaque()
    {
        var registry = new OperationRegistry();

        var id = registry.Start("test", () =>
            throw new InvalidOperationException("Could not reach graph.db - it may be locked by another process."));
        await registry.WhenComplete(id);

        var op = registry.Get(id);
        Assert.NotNull(op);
        Assert.Equal(OperationState.Failed, op!.State);
        Assert.NotNull(op.FinishedAtUtc);
        Assert.Null(op.Result);
        Assert.Equal("Could not reach graph.db - it may be locked by another process.", op.Error);
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull_NotAnEmptyRecord()
    {
        Assert.Null(new OperationRegistry().Get("not-a-real-operation-id"));
    }

    [Fact]
    public async Task CompletedOperation_StaysAvailable_UntilTheRetentionWindowElapses_ThenIsPruned()
    {
        // A shell polling late (a brief app-background, a network hiccup) must still get an answer
        // - so a completed operation must survive for a while, not vanish the instant it finishes.
        // Pruning is opportunistic (swept on the next Start call, not a background timer - see
        // OperationRegistry's own class doc comment for why that is an acceptable trade-off here),
        // so this test advances the injected clock and starts throwaway operations to trigger it,
        // exactly the "inject utcNow, control time explicitly" convention every other Control test
        // in this codebase already uses.
        var now = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        var registry = new OperationRegistry(utcNow: () => now, retention: TimeSpan.FromMinutes(1));

        var id = registry.Start("test", () => "done");
        await registry.WhenComplete(id);
        Assert.NotNull(registry.Get(id));

        now = now.AddSeconds(30); // still inside the 1-minute retention window
        registry.Start("other-1", () => "noop");
        Assert.NotNull(registry.Get(id));

        now = now.AddMinutes(2); // now well past the window
        registry.Start("other-2", () => "noop");
        Assert.Null(registry.Get(id));
    }

    [Fact]
    public void RunningOperation_IsNeverPruned_RegardlessOfHowLongItHasBeenRunning()
    {
        var now = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        var registry = new OperationRegistry(utcNow: () => now, retention: TimeSpan.FromMinutes(1));
        var gate = new TaskCompletionSource();

        var id = registry.Start("slow", () => { gate.Task.GetAwaiter().GetResult(); return "unused"; });

        now = now.AddHours(1); // long past any reasonable retention window, but still RUNNING
        registry.Start("other", () => "noop");

        Assert.NotNull(registry.Get(id));
        Assert.Equal(OperationState.Running, registry.Get(id)!.State);

        gate.SetResult();
    }
}

/// <summary>
/// <see cref="Operations.Get"/> - the thin HTTP-shape translator over <see cref="OperationRegistry"/>
/// - called directly (no real HTTP), same division of labour as ProjectsTests.cs's own
/// ProjectsBuildAsyncTests for action methods: proves status codes and JSON field VALUES. See
/// <see cref="OperationsEndpointHttpTests"/> for proof that an inapplicable field (progress/error/
/// result) is genuinely ABSENT rather than JSON null - that property depends on
/// <see cref="ControlListener"/>'s own <c>ConfigureHttpJsonOptions</c>, which the bare
/// <see cref="ServiceCollection"/> this class's own NewContext helper builds does not carry
/// (identical caveat to every other *Tests.cs in this directory).
/// </summary>
public sealed class OperationsGetTests
{
    static Func<DateTimeOffset> RealClock => () => DateTimeOffset.UtcNow;

    [Fact]
    public async Task UnknownId_Returns404_NamesTheIdAndSaysWhy_NotAnEmptyBody()
    {
        var response = Operations.Get(new OperationRegistry(), "not-a-real-id", RealClock);

        Assert.Equal(StatusCodes.Status404NotFound, StatusCodeOf(response));
        var json = await ResultBodyAsync(response);
        var message = json.GetProperty("error").GetString();
        Assert.Contains("not-a-real-id", message);
    }

    [Fact]
    public async Task KnownRunningId_Returns200_StateIsTheLiteralStringRunning()
    {
        var gate = new TaskCompletionSource();
        var registry = new OperationRegistry();
        var id = registry.Start("rebuild", () => { gate.Task.GetAwaiter().GetResult(); return "unused"; });

        var response = Operations.Get(registry, id, RealClock);
        var json = await ResultBodyAsync(response);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(response));
        Assert.Equal(id, json.GetProperty("id").GetString());
        Assert.Equal("rebuild", json.GetProperty("kind").GetString());
        Assert.Equal("running", json.GetProperty("state").GetString());

        gate.SetResult();
    }

    [Fact]
    public async Task KnownDoneId_StateIsTheLiteralStringDone()
    {
        var registry = new OperationRegistry();
        var id = registry.Start("test", () => "ok");
        await registry.WhenComplete(id);

        var json = await ResultBodyAsync(Operations.Get(registry, id, RealClock));

        Assert.Equal("done", json.GetProperty("state").GetString());
    }

    [Fact]
    public async Task KnownFailedId_StateIsTheLiteralStringFailed_ErrorIsThePlainMessage()
    {
        var registry = new OperationRegistry();
        var id = registry.Start("test", () => throw new InvalidOperationException("disk is full"));
        await registry.WhenComplete(id);

        var json = await ResultBodyAsync(Operations.Get(registry, id, RealClock));

        Assert.Equal("failed", json.GetProperty("state").GetString());
        Assert.Equal("disk is full", json.GetProperty("error").GetString());
    }

    // ---------------------------------------------------------------- Plan 11 Task 7 audit fix:
    // elapsedSeconds - startedAtUtc/finishedAtUtc alone forced a shell showing progress ("running
    // for Xs") to subtract a raw timestamp from "now" itself, the same "raw timestamps where a
    // display string is needed" violation TraceSequenceRow's own durationMs already avoids at the
    // trace level. A fixed injected clock (same "control time explicitly" convention as
    // CompletedOperation_StaysAvailable_... above) proves both halves: it keeps growing while
    // running, and freezes at the instant of completion rather than continuing to grow afterward.

    [Fact]
    public async Task Running_ElapsedSecondsReflectsNowMinusStarted_AndKeepsGrowing()
    {
        var now = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        var registry = new OperationRegistry(utcNow: () => now);
        var gate = new TaskCompletionSource();
        var id = registry.Start("slow", () => { gate.Task.GetAwaiter().GetResult(); return "unused"; });

        var firstJson = await ResultBodyAsync(Operations.Get(registry, id, () => now.AddSeconds(7)));
        Assert.Equal(7, firstJson.GetProperty("elapsedSeconds").GetInt32());

        var laterJson = await ResultBodyAsync(Operations.Get(registry, id, () => now.AddSeconds(42)));
        Assert.Equal(42, laterJson.GetProperty("elapsedSeconds").GetInt32());

        gate.SetResult();
    }

    [Fact]
    public async Task Done_ElapsedSecondsIsFrozenAtFinishedMinusStarted_NeverKeepsGrowingAfterCompletion()
    {
        var now = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        var registry = new OperationRegistry(utcNow: () => now);
        var id = registry.Start("test", () => "ok");
        await registry.WhenComplete(id);

        // Finished at t=0 (the injected clock never moved before completion). Polling from a "now"
        // 100s later must still read 0 - elapsed for a DONE operation is finishedAtUtc-startedAtUtc,
        // not now-startedAtUtc, or a shell that polls late would see an operation's duration keep
        // climbing forever after it already finished.
        var json = await ResultBodyAsync(Operations.Get(registry, id, () => now.AddSeconds(100)));

        Assert.Equal("done", json.GetProperty("state").GetString());
        Assert.Equal(0, json.GetProperty("elapsedSeconds").GetInt32());
    }

    // ---------------------------------------------------------------- helpers (duplicated locally
    // per this test directory's own established convention - see ProjectsTests.cs/EditorsTests.cs)

    static DefaultHttpContext NewContext()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        return context;
    }

    static async Task<JsonElement> ResultBodyAsync(IResult result)
    {
        var context = NewContext();
        await result.ExecuteAsync(context);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        return JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.Clone();
    }

    static int StatusCodeOf(IResult result)
    {
        var context = NewContext();
        result.ExecuteAsync(context).GetAwaiter().GetResult();
        return context.Response.StatusCode;
    }
}

/// <summary>
/// <c>GET /control/operations/{id}</c> over real HTTP against a directly-constructed
/// <see cref="ControlListener"/> - same style as every other Control *EndpointHttpTests class:
/// proving auth/Origin/routing/null-omission, not re-proving the resolution logic already covered
/// above.
/// </summary>
public sealed class OperationsEndpointHttpTests : IDisposable
{
    readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    string ConnectionFilePath => Path.Combine(_tempDir, "control.token");

    static HttpRequestMessage Request(HttpMethod method, string path, string? token, string? origin = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    static HttpClient ClientFor(ControlListener listener) => new() { BaseAddress = new Uri($"http://127.0.0.1:{listener.Port}") };

    [Fact]
    public async Task GetOperation_NoToken_IsRefused()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/control/operations/some-id", token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetOperation_ForeignOrigin_IsRejectedWith403_EvenWithAValidToken()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/control/operations/some-id", listener.Token, origin: "https://evil.example.com"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetOperation_UnknownId_Returns404()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/control/operations/nope", listener.Token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetOperation_Running_ProgressErrorResultAreAllAbsent_NotPresentAsNull()
    {
        var operations = new OperationRegistry();
        var gate = new TaskCompletionSource();
        var id = operations.Start("rebuild", () => { gate.Task.GetAwaiter().GetResult(); return "unused"; });

        using var listener = new ControlListener(ConnectionFilePath, operations: operations);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/operations/{id}", listener.Token));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("running", body.GetProperty("state").GetString());
        Assert.False(body.TryGetProperty("progress", out _), "progress must be ABSENT, not present-as-null, when nothing is known");
        Assert.False(body.TryGetProperty("error", out _), "error must be ABSENT while running");
        Assert.False(body.TryGetProperty("result", out _), "result must be ABSENT while running");

        gate.SetResult();
        await operations.WhenComplete(id);
    }

    [Fact]
    public async Task GetOperation_Done_ResultIsPresent_ErrorIsAbsent()
    {
        var operations = new OperationRegistry();
        var id = operations.Start("test", () => new { nodesBefore = 1, nodesAfter = 5 });
        await operations.WhenComplete(id);

        using var listener = new ControlListener(ConnectionFilePath, operations: operations);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/operations/{id}", listener.Token));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("done", body.GetProperty("state").GetString());
        Assert.False(body.TryGetProperty("error", out _), "error must be ABSENT on success");
        Assert.True(body.TryGetProperty("result", out var result));
        Assert.Equal(5, result.GetProperty("nodesAfter").GetInt32());
    }

    [Fact]
    public async Task GetOperation_Failed_ErrorIsPresent_ResultIsAbsent()
    {
        var operations = new OperationRegistry();
        var id = operations.Start("test", () => throw new InvalidOperationException("boom"));
        await operations.WhenComplete(id);

        using var listener = new ControlListener(ConnectionFilePath, operations: operations);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/operations/{id}", listener.Token));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("failed", body.GetProperty("state").GetString());
        Assert.Equal("boom", body.GetProperty("error").GetString());
        Assert.False(body.TryGetProperty("result", out _), "result must be ABSENT on failure");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}

/// <summary>
/// Proves Plan 11 Task 5 actually closes Task 3's own gap: <c>rebuild</c>'s operation id used to go
/// nowhere (a bare, un-awaited <see cref="Task.Run(Action)"/> with no store behind it - see
/// ProjectsEndpoint's own former "design decisions" note). Now it must be pollable end to end
/// through the real <see cref="ControlListener"/> route, sharing the SAME <see cref="OperationRegistry"/>
/// singleton <c>POST rebuild</c> started it in - not an isolated, empty default that would make
/// every poll 404 even for an id <c>rebuild</c> itself just returned.
/// </summary>
public sealed class RebuildOperationRoundTripTests : IDisposable
{
    const string ProjectGuid = "aaaabbbbccccddddeeeeffff55500005";

    readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    string ConnectionFilePath => Path.Combine(_tempDir, "control.token");

    public RebuildOperationRoundTripTests()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"), $"  productGUID: {ProjectGuid}\n");
    }

    [Fact]
    public async Task PostRebuild_ThenGetOperations_ReportsDoneWithNodeCounts_OverTheSameListener()
    {
        var projectService = new ProjectService(new AppPaths(Path.Combine(_tempDir, "app")));
        projectService.Adopt(_projectRoot); // adopted but never indexed

        using var listener = new ControlListener(ConnectionFilePath, projects: projectService);
        listener.Start();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{listener.Port}") };

        var rebuildRequest = new HttpRequestMessage(HttpMethod.Post, $"/control/projects/{ProjectGuid}/rebuild");
        rebuildRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", listener.Token);
        var rebuildResponse = await client.SendAsync(rebuildRequest);
        Assert.Equal(HttpStatusCode.OK, rebuildResponse.StatusCode);
        var operationId = (await rebuildResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("operationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(operationId));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        JsonElement body = default;
        while (DateTime.UtcNow < deadline)
        {
            var pollRequest = new HttpRequestMessage(HttpMethod.Get, $"/control/operations/{operationId}");
            pollRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", listener.Token);
            var pollResponse = await client.SendAsync(pollRequest);
            Assert.Equal(HttpStatusCode.OK, pollResponse.StatusCode); // never 404 - this app started it, it must know about it
            body = await pollResponse.Content.ReadFromJsonAsync<JsonElement>();

            if (body.GetProperty("state").GetString() != "running") break;
            await Task.Delay(25);
        }

        Assert.Equal("done", body.GetProperty("state").GetString());
        Assert.True(body.GetProperty("result").GetProperty("nodesAfter").GetInt32() >= 0);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _tempDir, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}

/// <summary>
/// Proves Program.cs actually threads ONE shared <see cref="OperationRegistry"/> singleton into
/// <see cref="ControlListener"/> - same division of labour as every other *ProgramWiringTests class
/// in this directory: the direct-construction tests above cover behaviour in isolation, this covers
/// the real wiring, which is exactly what a second, isolated default registry would silently break
/// (an operation id `rebuild` returns would 404 on poll even though the app itself started it).
/// </summary>
public sealed class OperationsProgramWiringTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public OperationsProgramWiringTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppPaths>();
                services.AddSingleton(new AppPaths(_appRoot));
            }));
    }

    [Fact]
    public async Task ControlListener_OperationsRoute_SeesTheSameRegistry_AnOperationStartedElsewhereIsPollable()
    {
        var registry = _factory.Services.GetRequiredService<OperationRegistry>();
        var id = registry.Start("test", () => "ok");
        await registry.WhenComplete(id);

        var listener = _factory.Services.GetRequiredService<ControlListener>();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{listener.Port}") };
        var request = new HttpRequestMessage(HttpMethod.Get, $"/control/operations/{id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", listener.Token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("done", body.GetProperty("state").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_appRoot)) Directory.Delete(_appRoot, recursive: true);
    }
}
