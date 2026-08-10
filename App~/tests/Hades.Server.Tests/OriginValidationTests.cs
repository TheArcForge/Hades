using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

public class OriginValidationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public OriginValidationTests(WebApplicationFactory<Program> factory)
    {
        // Isolates AppPaths to a throwaway directory - without this, EditorListener (started
        // unconditionally by Program.cs) would write a real token file into this machine's actual
        // ~/Library/Application Support/Hades/, capable of hijacking a real Editor's reconnect
        // target. Same pattern as every other WebApplicationFactory-based fixture in this project.
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppPaths>();
                services.AddSingleton(new AppPaths(_appRoot));
            }));
    }

    public void Dispose()
    {
        // See EditorToolTestBase.Dispose's own comment: _factory is a fresh per-test
        // WebApplicationFactory whose own background services can still be touching _appRoot
        // until the host itself is disposed - which must happen before the delete below.
        _factory.Dispose();

        if (Directory.Exists(_appRoot)) Directory.Delete(_appRoot, recursive: true);
    }

    static HttpRequestMessage McpPost(string? origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    [Fact]
    public async Task RejectsForeignOriginWith403()
    {
        var response = await _factory.CreateClient().SendAsync(McpPost("https://evil.example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AllowsLoopbackOrigin()
    {
        var response = await _factory.CreateClient().SendAsync(McpPost("http://127.0.0.1:7823"));
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AllowsLocalhostByName()
    {
        var response = await _factory.CreateClient().SendAsync(McpPost("http://localhost:7823"));
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AllowsAbsentOrigin()
    {
        // The spec conditions rejection on the header being "present and invalid".
        var response = await _factory.CreateClient().SendAsync(McpPost(null));
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RejectsMalformedOrigin()
    {
        var response = await _factory.CreateClient().SendAsync(McpPost("not-a-url"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RejectsGetOnMcpEndpointWith405()
    {
        // Revision 2026-07-28 removed the GET stream endpoint.
        var response = await _factory.CreateClient().GetAsync("/mcp");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task RejectsDeleteOnMcpEndpointWith405()
    {
        // Sessions were removed; DELETE terminated a session in earlier revisions.
        var response = await _factory.CreateClient().DeleteAsync("/mcp");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task EndpointIsServedAtMcpNotRoot()
    {
        // MapMcp defaults to "/"; the plugin declares http://127.0.0.1:7823/mcp statically,
        // so the path must be pinned and must not silently move.
        var atRoot = await _factory.CreateClient().PostAsync("/",
            JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }));
        Assert.Equal(HttpStatusCode.NotFound, atRoot.StatusCode);
    }
}
