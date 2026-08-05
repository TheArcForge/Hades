using System.Text.Json;
using Hades.Core;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

/// <summary>
/// The end-to-end reference query, over HTTP. Builds a miniature but realistic project: a script,
/// a prefab whose MonoBehaviour points at that script, and a scene instantiating the prefab.
/// </summary>
public class FindReferencesToTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";
    const string ScriptGuid = "aaaa1111aaaa1111aaaa1111aaaa1111";
    const string PrefabGuid = "bbbb2222bbbb2222bbbb2222bbbb2222";

    void Write(string relative, string body, string? guid = null)
    {
        var full = Path.Combine(_projectRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
        if (guid is not null) File.WriteAllText(full + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
    }

    public FindReferencesToTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            "  productGUID: ccccdddd0000111122223333444455556\n"[..("  productGUID: ".Length + 32)] + "\n");

        Write("Assets/PlayerController.cs", "using UnityEngine;\npublic class PlayerController : MonoBehaviour { }", ScriptGuid);
        Write("Assets/Player.prefab",
            Header + "--- !u!1 &1\nGameObject:\n  m_Name: Player\n"
            + $"--- !u!114 &2\nMonoBehaviour:\n  m_Script: {{fileID: 11500000, guid: {ScriptGuid}, type: 3}}\n",
            PrefabGuid);
        Write("Assets/Main.unity",
            Header + "--- !u!1001 &100\nPrefabInstance:\n  m_Modification:\n"
            + "    m_TransformParent: {fileID: 200}\n    m_Modifications: []\n    m_RemovedComponents: []\n"
            + $"  m_SourcePrefab: {{fileID: 100100000, guid: {PrefabGuid}, type: 3}}\n",
            "cccc3333cccc3333cccc3333cccc3333");

        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppPaths>();
                services.AddSingleton(new AppPaths(_appRoot));
            }));

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(_projectRoot);
    }

    static JsonElement Structured(JsonElement envelope) =>
        envelope.GetProperty("result").GetProperty("structuredContent");

    [Fact]
    public async Task FindsWhatUsesACSharpScript()
    {
        // The query a Unity developer actually asks. It only works because ScriptIndexer records
        // each .cs file's .meta GUID — without that, m_Script edges point at nothing resolvable.
        var structured = Structured(await McpTestClient.CallTool(_factory, "find_references_to",
            new { assetPath = "Assets/PlayerController.cs" }));

        Assert.Equal(1, structured.GetProperty("totalReferences").GetInt32());
        Assert.Equal(1, structured.GetProperty("referencingFiles").GetInt32());

        var file = structured.GetProperty("files")[0];
        Assert.Equal("Assets/Player.prefab", file.GetProperty("path").GetString());
        Assert.Equal(1, file.GetProperty("references").GetInt32());
        Assert.Equal("m_Script", file.GetProperty("sampleVia").GetString());
        Assert.Contains("references", file.GetProperty("relationships").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task ManyReferencesFromOneFileCollapseToOneRowWithACount()
    {
        // The point of grouping: a prefab used 144 times across two scenes should read as two
        // rows, not 144. Three MonoBehaviours in one prefab all pointing at the same script.
        Write("Assets/Triple.prefab",
            Header
            + $"--- !u!114 &1\nMonoBehaviour:\n  m_Script: {{fileID: 11500000, guid: {ScriptGuid}, type: 3}}\n"
            + $"--- !u!114 &2\nMonoBehaviour:\n  m_Script: {{fileID: 11500000, guid: {ScriptGuid}, type: 3}}\n"
            + $"--- !u!114 &3\nMonoBehaviour:\n  m_Script: {{fileID: 11500000, guid: {ScriptGuid}, type: 3}}\n",
            "dddd4444dddd4444dddd4444dddd4444");
        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(_projectRoot);

        var structured = Structured(await McpTestClient.CallTool(_factory, "find_references_to",
            new { assetPath = "Assets/PlayerController.cs" }));

        Assert.Equal(4, structured.GetProperty("totalReferences").GetInt32());   // 1 + 3
        Assert.Equal(2, structured.GetProperty("referencingFiles").GetInt32());  // two files

        // Ordered by weight, so the heaviest user is first — that is the one worth looking at.
        var heaviest = structured.GetProperty("files")[0];
        Assert.Equal("Assets/Triple.prefab", heaviest.GetProperty("path").GetString());
        Assert.Equal(3, heaviest.GetProperty("references").GetInt32());
    }

    [Fact]
    public async Task FindsWhichScenesInstantiateAPrefab()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "find_references_to",
            new { assetPath = "Assets/Player.prefab" }));

        var file = Assert.Single(structured.GetProperty("files").EnumerateArray(),
            f => f.GetProperty("relationships").EnumerateArray().Any(r => r.GetString() == "instance_of"));
        Assert.Equal("Assets/Main.unity", file.GetProperty("path").GetString());
    }

    [Fact]
    public async Task ReportsWhichRelationshipKindsAFileUses()
    {
        var file = Structured(await McpTestClient.CallTool(_factory, "find_references_to",
            new { assetPath = "Assets/PlayerController.cs" })).GetProperty("files")[0];

        var relationship = Assert.Single(file.GetProperty("relationships").EnumerateArray());
        Assert.Equal("references", relationship.GetString());
    }

    [Fact]
    public async Task AnUnreferencedAssetReportsZeroRatherThanFailing()
    {
        // "Known, zero references" and "unknown path" are very different answers to someone
        // asking what would break.
        var structured = Structured(await McpTestClient.CallTool(_factory, "find_references_to",
            new { assetPath = "Assets/Main.unity" }));

        Assert.Equal(0, structured.GetProperty("totalReferences").GetInt32());
        Assert.Equal(0, structured.GetProperty("referencingFiles").GetInt32());
        Assert.Empty(structured.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public async Task AnUnknownPathGivesActionableGuidance()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "find_references_to",
            new { assetPath = "Assets/DoesNotExist.prefab" }));

        Assert.Contains("not in the graph", text);
        Assert.Contains("search_by_name", text);
    }

    [Fact]
    public async Task ABlankPathGivesActionableGuidance()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "find_references_to",
            new { assetPath = "  " }));

        Assert.Contains("assetPath", text);
    }

    [Fact]
    public async Task TheToolIsAdvertisedWithSchemasAndReadOnly()
    {
        var tool = Assert.Single((await McpTestClient.ListTools(_factory))
            .GetProperty("result").GetProperty("tools").EnumerateArray(),
            t => t.GetProperty("name").GetString() == "find_references_to");

        Assert.True(tool.TryGetProperty("outputSchema", out _));
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.Contains("assetPath", tool.GetProperty("inputSchema").GetProperty("required").EnumerateArray()
            .Select(x => x.GetString()));
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
