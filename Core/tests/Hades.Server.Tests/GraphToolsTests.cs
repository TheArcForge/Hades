using System.Text.Json;
using Hades.Core;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

/// <summary>
/// End-to-end, over HTTP, for GraphTools.cs's surviving tool, trace_dependencies. The underlying
/// SQL is already covered at the GraphDatabase level by RelationshipQueryTests — this file exists
/// to close the gap those tests do not: that the tool is actually wired up, advertised, and
/// reachable over MCP with the argument names and error behaviour a real caller depends on.
///
/// Plan 10 Task 6 removed this file's other five tools' tests (find_prefabs_with_component,
/// find_components_using_pattern, find_orphan_scripts, component_find, event_find_all) along with
/// the tools themselves - all five folded into graph_query/find_unset_references, see
/// QueryToolsTests.cs's "Enumerated" tests and InspectToolTests.cs's ProjectScope_* tests for the
/// equivalent coverage through the new tools. This fixture's own two-distinct-target UnityEvent
/// case (proving listenerCount aggregates to 2, not 1, when two listeners on one field target
/// different objects) is preserved verbatim as
/// InspectToolTests.cs::ProjectScope_TwoListenersOnOneFieldTargetingDifferentObjects_AggregatesListenerCountToTwo,
/// since find_unset_references' project scope is byte-for-byte the same
/// ProjectService.FindUnityEvents call event_find_all used.
/// </summary>
public class GraphToolsTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";
    const string ScriptGuid = "aaaa1111aaaa1111aaaa1111aaaa1111";
    const string OrphanGuid = "cccc3333cccc3333cccc3333cccc3333";
    const string PrefabGuid = "bbbb2222bbbb2222bbbb2222bbbb2222";
    const string MaterialGuid = "dddd4444dddd4444dddd4444dddd4444";

    /// <summary>Never resolves — no node in this fixture (or any real project) owns it, by
    /// construction: no file anywhere this fixture writes carries this guid. Shaders ARE indexed
    /// as nodes now (see Hades.Core.Unity.ImportedAssetKind), but that only helps a shader file
    /// that actually exists inside a scanned root; this guid names no file at all, standing in
    /// for the real-world case of a guid resolved outside every scanned root entirely (e.g. a
    /// package asset under Library/PackageCache) — dangling for the same observable reason,
    /// without needing a package fixture to prove it.</summary>
    const string UnindexedShaderGuid = "eeee5555eeee5555eeee5555eeee5555";

    void Write(string relative, string body, string? guid = null)
    {
        var full = Path.Combine(_projectRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
        if (guid is not null) File.WriteAllText(full + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
    }

    public GraphToolsTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            "  productGUID: aaaabbbbccccddddeeeeffff00001111\n");

        Write("Assets/PlayerController.cs", "using UnityEngine;\npublic class PlayerController : MonoBehaviour { }", ScriptGuid);
        Write("Assets/OrphanScript.cs", "using UnityEngine;\npublic class OrphanScript : MonoBehaviour { }", OrphanGuid);
        Write("Assets/Player.prefab",
            Header + "--- !u!1 &1\nGameObject:\n  m_Name: Player\n"
            + $"--- !u!114 &2\nMonoBehaviour:\n  m_Script: {{fileID: 11500000, guid: {ScriptGuid}, type: 3}}\n",
            PrefabGuid);
        Write("Assets/Enemy.prefab",
            Header + "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  m_Name: Enemy\n"
            + "--- !u!54 &2\nRigidbody:\n  m_GameObject: {fileID: 1}\n");

        // F6-honesty fixture: a material (indexed, gets a node) whose only dependency is a
        // shader guid nothing in the graph owns — this fixture never writes a file with that
        // guid at all, the same observable shape as a real project's shader/texture reference
        // resolving outside every scanned root (see UnindexedShaderGuid's own doc comment).
        Write("Assets/Enemy.mat",
            Header + "--- !u!21 &2100000\nMaterial:\n  m_Name: Enemy\n"
            + $"  m_Shader: {{fileID: 4800000, guid: {UnindexedShaderGuid}, type: 3}}\n",
            MaterialGuid);

        // A "Menu" GameObject with a MonoBehaviour carrying two wired UnityEvent fields: m_OnClick
        // with two listeners (targeting two DIFFERENT objects - fileId 1 and fileId 2 - since two
        // calls to the SAME target on the SAME event field would collide under the edges table's
        // own (from_path, from_file_id, to_guid, to_file_id, property_path) uniqueness and collapse
        // into one edge, undercounting; see FindUnityEvents' doc comment for why that collapsing is
        // a real, disclosed limitation rather than a bug this fixture should paper over), m_OnHover
        // with one.
        Write("Assets/Menu.prefab", Header
            + "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  m_Name: Menu\n"
            + "--- !u!114 &2\nMonoBehaviour:\n  m_GameObject: {fileID: 1}\n"
            + "  m_OnClick:\n    m_PersistentCalls:\n      m_Calls:\n"
            + "      - m_Target: {fileID: 1}\n        m_MethodName: DoOne\n        m_Mode: 1\n        m_CallState: 2\n"
            + "      - m_Target: {fileID: 2}\n        m_MethodName: DoTwo\n        m_Mode: 1\n        m_CallState: 2\n"
            + "  m_OnHover:\n    m_PersistentCalls:\n      m_Calls:\n"
            + "      - m_Target: {fileID: 1}\n        m_MethodName: DoThree\n        m_Mode: 1\n        m_CallState: 2\n");

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

    // ---------------------------------------------------------------- trace_dependencies

    [Fact]
    public async Task TraceDependencies_WalksReferencesOutwardFromAKnownAsset()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "trace_dependencies",
            new { assetPath = "Assets/Player.prefab" }));

        Assert.Equal("Assets/Player.prefab", structured.GetProperty("root").GetString());
        var hit = Assert.Single(structured.GetProperty("results").EnumerateArray());
        Assert.Equal("Assets/PlayerController.cs", hit.GetProperty("path").GetString());
        Assert.Equal(1, hit.GetProperty("depth").GetInt32());
    }

    [Fact]
    public async Task TraceDependencies_BlankPathGivesActionableGuidance()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "trace_dependencies",
            new { assetPath = "  " }));

        Assert.Contains("assetPath", text);
    }

    // ------------------------------------------------------------ trace_dependencies (F6-honesty)

    [Fact]
    public async Task TraceDependencies_ReportsADanglingDependencyForAMaterialsUnindexedShader()
    {
        // Before F6-honesty this was {results: [], truncated: false} — an authoritative-looking
        // "depends on nothing" for a material whose only dependency (a shader) genuinely exists
        // as an edge, it just points at an asset kind this graph never indexes as a node.
        var structured = Structured(await McpTestClient.CallTool(_factory, "trace_dependencies",
            new { assetPath = "Assets/Enemy.mat" }));

        Assert.Empty(structured.GetProperty("results").EnumerateArray());
        Assert.Equal(0, structured.GetProperty("totalReturned").GetInt32());
        Assert.False(structured.GetProperty("truncated").GetBoolean());

        Assert.Equal(1, structured.GetProperty("danglingCount").GetInt32());
        Assert.False(structured.GetProperty("danglingTruncated").GetBoolean());

        var dangling = Assert.Single(structured.GetProperty("dangling").EnumerateArray());
        Assert.Equal("Assets/Enemy.mat", dangling.GetProperty("fromPath").GetString());
        Assert.Equal(1, dangling.GetProperty("depth").GetInt32());
        Assert.Equal(UnindexedShaderGuid, dangling.GetProperty("toGuid").GetString());
        Assert.Equal("m_Shader", dangling.GetProperty("propertyPath").GetString());

        // Authored server-side, rendered verbatim — explains both reasons a target can go
        // unresolved (outside every scanned root, e.g. Library/PackageCache; or a still-unindexed
        // kind) and reassures that dangling is not "missing or unsafe".
        var note = structured.GetProperty("danglingNote").GetString();
        Assert.Contains("Library/PackageCache", note);
        Assert.Contains("does not mean the dependency is missing", note);
    }

    [Fact]
    public async Task TraceDependencies_DanglingFieldsAreEmptyAndTheNoteIsOmittedWhenNothingIsDangling()
    {
        // A measured report, not a static disclaimer: the boundary note must not appear on a
        // query with nothing dangling to explain.
        var structured = Structured(await McpTestClient.CallTool(_factory, "trace_dependencies",
            new { assetPath = "Assets/Player.prefab" }));

        Assert.Equal(0, structured.GetProperty("danglingCount").GetInt32());
        Assert.Empty(structured.GetProperty("dangling").EnumerateArray());
        Assert.False(structured.GetProperty("danglingTruncated").GetBoolean());

        // The MCP SDK's serializer omits null properties entirely (JsonIgnoreCondition.
        // WhenWritingNull) rather than sending an explicit null — same convention
        // RealProjectInspectionSmokeTest already documents for InspectAssetResult's own optional
        // fields — so absence, not a null read, is the correct assertion here.
        Assert.False(structured.TryGetProperty("danglingNote", out _));
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
