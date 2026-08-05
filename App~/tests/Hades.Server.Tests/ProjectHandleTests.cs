using System.Text.Json;
using Hades.Core;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

/// <summary>
/// Multi-project behaviour, in its own fixture because it seeds a second project into the
/// shared ProjectService singleton and would otherwise leak into the single-project tests.
/// </summary>
public class ProjectHandleTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly List<string> _projectRoots = [];

    string MakeProject(string guid, string typeName)
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _projectRoots.Add(root);
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(root, "ProjectSettings", "ProjectSettings.asset"),
            $"  productGUID: {guid}\n");
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        File.WriteAllText(Path.Combine(root, "Assets", $"{typeName}.cs"), $"public class {typeName} {{ }}");
        return root;
    }

    public ProjectHandleTests(WebApplicationFactory<Program> factory)
    {
        var first = MakeProject("aaaabbbbccccddddeeeeffff00001111", "AlphaType");
        var second = MakeProject("bbbbccccddddeeeeffff000011112222", "BetaType");

        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppPaths>();
                services.AddSingleton(new AppPaths(_appRoot));
            }));

        var service = _factory.Services.GetRequiredService<ProjectService>();
        service.AdoptAndIndex(first);
        service.AdoptAndIndex(second);
    }

    static JsonElement Structured(JsonElement envelope) =>
        envelope.GetProperty("result").GetProperty("structuredContent");

    [Fact]
    public async Task StatusListsEveryProjectAndOffersNoDefault()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "hades_status"));

        Assert.Equal(2, structured.GetProperty("knownProjects").GetArrayLength());

        // With more than one project there is no safe default — the caller must choose.
        // The serializer omits null properties entirely, so absent and null both mean "none".
        var hasDefault = structured.TryGetProperty("defaultProject", out var d)
            && d.ValueKind is not JsonValueKind.Null;
        Assert.False(hasDefault, structured.GetRawText());
    }

    [Fact]
    public async Task OmittingTheHandleFailsWithTheCandidateList()
    {
        // The v1.2 hub guessed here. Guessing routes work to the wrong project silently, which
        // is worse than failing, so this must name the alternatives instead.
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "search_by_name",
            new { namePattern = "Type" }));

        Assert.Contains("needs a 'project' argument", text);
        Assert.Contains("aaaabbbbccccddddeeeeffff00001111", text);
        Assert.Contains("bbbbccccddddeeeeffff000011112222", text);
    }

    [Fact]
    public async Task TheHandleSelectsTheRightProject()
    {
        var alpha = Structured(await McpTestClient.CallTool(_factory, "search_by_name",
            new { namePattern = "Type", project = "aaaabbbbccccddddeeeeffff00001111" }));
        var beta = Structured(await McpTestClient.CallTool(_factory, "search_by_name",
            new { namePattern = "Type", project = "bbbbccccddddeeeeffff000011112222" }));

        Assert.Equal("AlphaType", alpha.GetProperty("results")[0].GetProperty("name").GetString());
        Assert.Equal("BetaType", beta.GetProperty("results")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task SummaryAlsoHonoursTheHandle()
    {
        var summary = Structured(await McpTestClient.CallTool(_factory, "get_project_summary",
            new { project = "bbbbccccddddeeeeffff000011112222" }));

        Assert.Equal("bbbbccccddddeeeeffff000011112222", summary.GetProperty("productGuid").GetString());
        Assert.Equal(1, summary.GetProperty("totalNodes").GetInt32());
    }

    public void Dispose()
    {
        foreach (var dir in _projectRoots.Append(_appRoot))
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
