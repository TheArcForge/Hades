using System.Net;
using Hades.Control.Client;
using Hades.Control.Client.Dtos;

namespace Hades.Control.Client.Tests;

/// <summary>
/// Exercises <see cref="ControlClient"/> against a stub <see cref="HttpMessageHandler"/> - no
/// socket, no real <see cref="Hades.Server.Control.ControlListener"/> involved. Mirrors the shape
/// of the case analysis in the Swift original's own test suite
/// (Mac/HadesControl/Sources/HadesControl/ControlClient.swift's <c>ControlClientError</c> doc
/// comments): one test per way a call can either succeed or fail to hand back a decoded DTO.
/// </summary>
public class ControlClientTests
{
    sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? SeenAuthorization { get; private set; }
        public Uri? SeenUri { get; private set; }
        public HttpMethod? SeenMethod { get; private set; }

        /// <summary>The serialized request body, or empty for a request that carried none. Needed
        /// once the client gained a POST that sends one - asserting the route alone would not catch
        /// a body with the wrong property name on it.</summary>
        public string SeenBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SeenAuthorization = request.Headers.Authorization?.ToString();
            SeenUri = request.RequestUri;
            SeenMethod = request.Method;

            if (request.Content is not null)
            {
                SeenBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    /// <summary>Throws instead of returning a response - simulates the core not being reachable at
    /// all (not running, stale port, connection refused), as opposed to every other test's "got a
    /// response, just not a happy one".</summary>
    sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw exception;
    }

    const string SettingsBody =
        """{"mcpPort":{"port":7823,"inUse":false,"message":"ok"},"logLevel":{"level":"Information"}}""";

    static ControlClient Make(StubHandler handler) =>
        new(new ControlConnection { Port = 1234, Token = "tok" }, new HttpClient(handler));

    static ControlClient Make(HttpMessageHandler handler) =>
        new(new ControlConnection { Port = 1234, Token = "tok" }, new HttpClient(handler));

    [Fact]
    public async Task Settings_PresentsBearerToken()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SettingsBody);
        var client = Make(handler);

        await client.SettingsAsync();

        Assert.Equal("Bearer tok", handler.SeenAuthorization);
    }

    [Fact]
    public async Task Settings_RequestsLoopbackPortAndPath()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SettingsBody);
        var client = Make(handler);

        await client.SettingsAsync();

        Assert.NotNull(handler.SeenUri);
        Assert.Equal("http", handler.SeenUri!.Scheme);
        Assert.Equal("127.0.0.1", handler.SeenUri.Host);
        Assert.Equal(1234, handler.SeenUri.Port);
        Assert.Equal("/control/settings", handler.SeenUri.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.SeenMethod);
    }

    [Fact]
    public async Task Settings_DecodesSuccessfulBody()
    {
        var client = Make(new StubHandler(HttpStatusCode.OK, SettingsBody));

        var result = await client.SettingsAsync();

        Assert.Equal(7823, result.McpPort.Port);
        Assert.False(result.McpPort.InUse);
        Assert.Equal("ok", result.McpPort.Message);
        Assert.Equal("Information", result.LogLevel.Level);
    }

    [Fact]
    public async Task Unauthorized_RaisesStaleTokenSpecifically()
    {
        var client = Make(new StubHandler(HttpStatusCode.Unauthorized, """{"error":"Missing or invalid token"}"""));

        var ex = await Assert.ThrowsAsync<ControlClientException>(() => client.SettingsAsync());

        Assert.Equal(ControlClientError.StaleToken, ex.Error);
    }

    [Fact]
    public async Task OtherError_SurfacesServerAuthoredMessageVerbatim()
    {
        var client = Make(new StubHandler(HttpStatusCode.NotFound, """{"error":"Unknown project 'x'."}"""));

        var ex = await Assert.ThrowsAsync<ControlClientException>(() => client.SettingsAsync());

        Assert.Equal(ControlClientError.Server, ex.Error);
        Assert.Equal("Unknown project 'x'.", ex.Message);
    }

    [Fact]
    public async Task MalformedBody_RaisesDecodingNotUnhandledJsonException()
    {
        var client = Make(new StubHandler(HttpStatusCode.OK, "not valid json"));

        var ex = await Assert.ThrowsAsync<ControlClientException>(() => client.SettingsAsync());

        Assert.Equal(ControlClientError.Decoding, ex.Error);
    }

    [Fact]
    public async Task TransportFailure_RaisesTransportNotUnhandledException()
    {
        var client = Make(new ThrowingHandler(new HttpRequestException("connection refused")));

        var ex = await Assert.ThrowsAsync<ControlClientException>(() => client.SettingsAsync());

        Assert.Equal(ControlClientError.Transport, ex.Error);
    }

    // ---- The Projects section's routes -------------------------------------------------------
    //
    // Each asserts the METHOD and PATH as well as decoding, because a client that posts to the
    // wrong path still decodes a stubbed body perfectly well. Every path below was read off
    // ControlListener.cs rather than guessed.

    const string ActionBody = """{"success":true,"message":"Removed 'Hades-Unity-Client'."}""";

    [Fact]
    public async Task AddProject_PostsThePathInTheBody()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TestBodies.ProjectRow);
        var client = Make(handler);

        var row = await client.AddProjectAsync("/Users/mike/Projects/Hades-Unity-Client");

        Assert.Equal(HttpMethod.Post, handler.SeenMethod);
        Assert.Equal("/control/projects/add", handler.SeenUri!.AbsolutePath);
        Assert.Contains("\"path\"", handler.SeenBody);
        Assert.Contains("Hades-Unity-Client", handler.SeenBody);
        Assert.Equal("Hades-Unity-Client", row.Name);
    }

    [Fact]
    public async Task RemoveProject_PostsToTheProjectsRemoveRoute()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ActionBody);
        var client = Make(handler);

        var result = await client.RemoveProjectAsync("abc123");

        Assert.Equal(HttpMethod.Post, handler.SeenMethod);
        Assert.Equal("/control/projects/abc123/remove", handler.SeenUri!.AbsolutePath);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task RebuildProject_ReturnsTheOperationIdToPoll()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"operationId":"op-1"}""");
        var client = Make(handler);

        var started = await client.RebuildProjectAsync("abc123");

        Assert.Equal("/control/projects/abc123/rebuild", handler.SeenUri!.AbsolutePath);
        Assert.Equal("op-1", started.OperationId);
    }

    [Fact]
    public async Task InstallPlugin_DecodesNeedsRestartAndMessage()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"success":true,"needsRestart":true,"message":"Plugin installed. Restart Unity to pick it up."}""");
        var client = Make(handler);

        var result = await client.InstallPluginAsync("abc123");

        Assert.Equal("/control/projects/abc123/installPlugin", handler.SeenUri!.AbsolutePath);
        Assert.True(result.NeedsRestart);
        Assert.Equal("Plugin installed. Restart Unity to pick it up.", result.Message);
    }

    [Fact]
    public async Task RevealInFinder_KeepsTheServersRouteNameOnEveryPlatform()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ActionBody);
        var client = Make(handler);

        await client.RevealInFinderAsync("abc123");

        // "revealInFinder" even on Windows: the route is the server's, and the server decides what
        // revealing means per platform. A client that renamed it would simply 404.
        Assert.Equal("/control/projects/abc123/revealInFinder", handler.SeenUri!.AbsolutePath);
    }

    [Fact]
    public async Task OpenInUnity_PostsToTheOpenInUnityRoute()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ActionBody);
        var client = Make(handler);

        await client.OpenInUnityAsync("abc123");

        Assert.Equal("/control/projects/abc123/openInUnity", handler.SeenUri!.AbsolutePath);
    }

    [Fact]
    public async Task Operation_GetsTheOperationById()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TestBodies.OperationResult);
        var client = Make(handler);

        var result = await client.OperationAsync("op-1");

        Assert.Equal(HttpMethod.Get, handler.SeenMethod);
        Assert.Equal("/control/operations/op-1", handler.SeenUri!.AbsolutePath);
        Assert.Equal(OperationState.Done, result.State);
    }

    /// <summary>
    /// A productGuid or operation id is interpolated into the path, so anything needing escaping
    /// must be escaped - otherwise a stray slash silently addresses a different route.
    /// </summary>
    [Fact]
    public async Task IdsAreEscapedIntoThePath()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ActionBody);
        var client = Make(handler);

        await client.RemoveProjectAsync("a/b c");

        Assert.Equal("/control/projects/a%2Fb%20c/remove", handler.SeenUri!.AbsolutePath);
    }

    /// <summary>
    /// The status has to survive onto the exception. The Projects section tells a pruned operation
    /// (404 - ordinary, the rebuild finished a while ago) apart from a real server error by exactly
    /// this, and without it the two are indistinguishable.
    /// </summary>
    [Fact]
    public async Task ServerError_CarriesTheStatusCodeAndTheServersOwnMessage()
    {
        var client = Make(new StubHandler(
            HttpStatusCode.NotFound,
            """{"error":"Unknown operation 'op-9'. It may have completed and been pruned, or the id is wrong."}"""));

        var ex = await Assert.ThrowsAsync<ControlClientException>(() => client.OperationAsync("op-9"));

        Assert.Equal(ControlClientError.Server, ex.Error);
        Assert.Equal(404, ex.StatusCode);
        Assert.StartsWith("Unknown operation 'op-9'.", ex.Message);
    }

    [Fact]
    public async Task StaleToken_HasNoStatusCode()
    {
        var client = Make(new StubHandler(HttpStatusCode.Unauthorized, """{"error":"nope"}"""));

        var ex = await Assert.ThrowsAsync<ControlClientException>(() => client.SettingsAsync());

        Assert.Equal(ControlClientError.StaleToken, ex.Error);
        Assert.Null(ex.StatusCode);
    }

    // ---- The Charon (traces) routes ----------------------------------------------------------

    const string SequencesBody = """{"sequences":[],"truncated":false}""";

    [Fact]
    public async Task TracesSequences_WithNoFilters_SendsNoQueryStringAtAll()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SequencesBody);
        var client = Make(handler);

        await client.TracesSequencesAsync();

        Assert.Equal("/control/traces/sequences", handler.SeenUri!.AbsolutePath);
        Assert.Equal(string.Empty, handler.SeenUri.Query);
    }

    /// <summary>
    /// limit is omitted when null rather than defaulted to today's 200: the route's own default is
    /// the single source of truth, and a client that hardcoded it would be keeping a stale copy of a
    /// server-owned policy value.
    /// </summary>
    [Fact]
    public async Task TracesSequences_OmitsLimitWhenNotGiven()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SequencesBody);
        var client = Make(handler);

        await client.TracesSequencesAsync(project: "abc");

        Assert.DoesNotContain("limit", handler.SeenUri!.Query);
        Assert.Contains("project=abc", handler.SeenUri.Query);
    }

    [Fact]
    public async Task TracesSequences_SendsEveryFilterItWasGiven()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SequencesBody);
        var client = Make(handler);

        await client.TracesSequencesAsync(
            project: "abc", tool: "read_file", outcome: "error",
            minDurationMs: 10, maxDurationMs: 500, limit: 50);

        var query = handler.SeenUri!.Query;
        Assert.Contains("project=abc", query);
        Assert.Contains("tool=read_file", query);
        Assert.Contains("outcome=error", query);
        Assert.Contains("minDurationMs=10", query);
        Assert.Contains("maxDurationMs=500", query);
        Assert.Contains("limit=50", query);
    }

    [Fact]
    public async Task TracesFailuresAndSlow_HitTheirOwnEndpoints()
    {
        var failures = new StubHandler(HttpStatusCode.OK, """{"failures":[],"truncated":false}""");
        await Make(failures).TracesFailuresAsync(project: "abc");
        Assert.Equal("/control/traces/failures", failures.SeenUri!.AbsolutePath);

        var slow = new StubHandler(HttpStatusCode.OK, """{"tools":[],"truncated":false}""");
        await Make(slow).TracesSlowAsync(project: "abc");
        Assert.Equal("/control/traces/slow", slow.SeenUri!.AbsolutePath);
    }

    [Fact]
    public async Task TraceDetail_GetsOneTraceByIdWithTheProjectFilter()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"traceId":"t-1","tool":"read_file","startUtcMs":1,"outcome":"ok","spans":[]}""");
        var client = Make(handler);

        var detail = await client.TraceDetailAsync("t-1", project: "abc");

        Assert.Equal("/control/traces/t-1", handler.SeenUri!.AbsolutePath);
        Assert.Contains("project=abc", handler.SeenUri.Query);
        Assert.Equal(TraceOutcome.Ok, detail.Outcome);
    }

    /// <summary>
    /// A trace id is a path segment, so one containing a slash must be escaped or it silently
    /// addresses a different route.
    /// </summary>
    [Fact]
    public async Task TraceDetail_EscapesTheTraceIdIntoThePath()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"traceId":"x","tool":"t","startUtcMs":1,"outcome":"ok","spans":[]}""");

        await Make(handler).TraceDetailAsync("a/b");

        Assert.Equal("/control/traces/a%2Fb", handler.SeenUri!.AbsolutePath);
    }

    /// <summary>
    /// An unrecognised outcome must decode to Unknown rather than throwing: a NEWER core adding a
    /// case cannot be allowed to crash an OLDER shell. That is what UnknownFallbackConverter is for,
    /// and this pins it on a real wire value.
    /// </summary>
    [Fact]
    public async Task AnUnrecognisedOutcome_DecodesToUnknownRatherThanThrowing()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"traceId":"t-1","tool":"read_file","startUtcMs":1,"outcome":"somethingNewerCoresSay","spans":[]}""");

        var detail = await Make(handler).TraceDetailAsync("t-1");

        Assert.Equal(TraceOutcome.Unknown, detail.Outcome);
    }

    // ---- The Asphodel (memory) routes --------------------------------------------------------

    [Fact]
    public async Task Memory_GetsDocumentsAndProposalsInOneRoundTrip()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"documents":[],"proposals":[]}""");
        var client = Make(handler);

        await client.MemoryAsync(project: "abc");

        Assert.Equal("/control/memory", handler.SeenUri!.AbsolutePath);
        Assert.Contains("project=abc", handler.SeenUri.Query);
    }

    /// <summary>
    /// name is a QUERY parameter, not a route segment - a document name can contain characters that
    /// would otherwise have to be path-escaped, and the endpoint is defined that way.
    /// </summary>
    [Fact]
    public async Task MemoryDocument_SendsTheNameAsAQueryParameter()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"name":"Conventions.md","content":"# hi"}""");
        var client = Make(handler);

        var document = await client.MemoryDocumentAsync("Conventions.md", project: "abc");

        Assert.Equal("/control/memory/document", handler.SeenUri!.AbsolutePath);
        Assert.Contains("name=Conventions.md", handler.SeenUri.Query);
        Assert.Equal("# hi", document.Content);
    }

    [Fact]
    public async Task WriteMemoryDocument_PostsTheContentInTheBody()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ActionBody);
        var client = Make(handler);

        await client.WriteMemoryDocumentAsync("Conventions.md", "new content", project: "abc");

        Assert.Equal(HttpMethod.Post, handler.SeenMethod);
        Assert.Equal("/control/memory/document", handler.SeenUri!.AbsolutePath);
        Assert.Contains("name=Conventions.md", handler.SeenUri.Query);
        Assert.Contains("new content", handler.SeenBody);
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("defer")]
    public async Task ProposalActions_PostToTheirOwnRoutes(string action)
    {
        var handler = new StubHandler(HttpStatusCode.OK, ActionBody);
        var client = Make(handler);

        if (action == "accept") await client.AcceptMemoryProposalAsync("p-1.md", project: "abc");
        else await client.DeferMemoryProposalAsync("p-1.md", project: "abc");

        Assert.Equal($"/control/memory/proposals/{action}", handler.SeenUri!.AbsolutePath);
        Assert.Contains("fileName=p-1.md", handler.SeenUri.Query);
    }

    /// <summary>
    /// Dismiss DELETES the proposal file, and the endpoint defaults confirm to false and refuses
    /// without it. The flag must therefore actually reach the wire - a client that dropped it would
    /// turn every dismissal into a confusing server error.
    /// </summary>
    [Fact]
    public async Task DismissProposal_SendsTheConfirmFlagTheServerRequires()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ActionBody);
        var client = Make(handler);

        await client.DismissMemoryProposalAsync("p-1.md", confirm: true, project: "abc");

        Assert.Equal("/control/memory/proposals/dismiss", handler.SeenUri!.AbsolutePath);
        Assert.Contains("confirm=true", handler.SeenUri.Query);
    }

    [Fact]
    public async Task DismissProposal_SendsConfirmFalseWhenNotConfirmed()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ActionBody);
        var client = Make(handler);

        await client.DismissMemoryProposalAsync("p-1.md", confirm: false);

        Assert.Contains("confirm=false", handler.SeenUri!.Query);
    }

    static class TestBodies
    {
        public const string ProjectRow =
            """{"name":"Hades-Unity-Client","path":"/p","productGuid":"abc123","unityVersion":"2022.3.10f1","indexState":"indexed","indexStatus":"Indexed","nodeCount":1,"edgeCount":2,"editor":{"state":"attached","status":"Editor attached"},"warnings":[]}""";

        public const string OperationResult =
            """{"id":"op-1","kind":"rebuild","state":"done","startedAtUtc":"2026-06-01T12:00:00+00:00","elapsedSeconds":5}""";
    }
}
