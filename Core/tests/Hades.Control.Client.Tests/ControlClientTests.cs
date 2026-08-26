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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SeenAuthorization = request.Headers.Authorization?.ToString();
            SeenUri = request.RequestUri;
            SeenMethod = request.Method;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
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
}
