using System.Text.Json;
using Hades.Core;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

/// <summary>
/// F6-honesty's mandated ground-truth check, against the real Hades-Unity-Client corpus rather
/// than a synthetic fixture: GraphToolsTests' own TraceDependencies_ReportsADanglingDependency...
/// tests already prove the mechanism against a controlled fixture; this proves it holds for a
/// REAL asset with a REAL unindexed dependency, not just one built to order. Same
/// directory-exists-guard pattern as every other RealProject*SmokeTest — a local sanity check,
/// skipped rather than failing on a machine that does not have this checkout. Isolated per-test
/// AppPaths (never sharing the real machine's default HADES_HOME) via the same DI-substitution
/// convention every other RealProject*SmokeTest already uses, and strictly read-only against
/// RealProject itself — every tool called here (inspect_asset, trace_dependencies) is
/// ReadOnly = true, and nothing in this file ever writes into RealProject.
///
/// The corpus's one material, Assets/Demo/Materials/M_Enemy.mat, has no texture assigned in any
/// slot (every m_TexEnvs entry is {fileID: 0}), but does reference a shader
/// (933532a4fcc9baf4fa0491de14d08ed7 — URP's builtin Lit.shader, confirmed via
/// Library/PackageCache/.../Lit.shader.meta, which is outside every scan root ProjectWalker
/// resolves, so unresolvable by either the graph or ReadThrough.ResolveGuidsToPaths). A
/// material→shader case, not the bug's own material→texture phrasing — and, since binary/imported
/// assets became graph nodes (textures, models, audio, fonts, shaders, and animation clips are all
/// indexed now — see Hades.Core.Unity.ImportedAssetKind), the REAL reason this specific shader
/// stays dangling: it is a builtin bundled with the URP package and resolved into
/// Library/PackageCache, a root ProjectWalker deliberately never scans (registry packages are
/// third-party code — see ProjectWalker's own class doc comment), not because "shader" is an
/// unindexed kind. This is exactly the concrete, real-world case the dangling boundary's wording
/// now names explicitly. A separate, fully-resolved (inspect_asset returns resolved: true, a real
/// on-disk path) material→texture case is covered by a synthetic fixture in GraphToolsTests
/// instead, precisely because no such asset exists in this corpus to check against.
/// </summary>
public class RealProjectDanglingDependencySmokeTest : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    const string RealProject = "/Users/mike/Projects/Hades-Unity-Client";
    const string Material = "Assets/Demo/Materials/M_Enemy.mat";

    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public RealProjectDanglingDependencySmokeTest(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppPaths>();
                services.AddSingleton(new AppPaths(_appRoot));
            }));
    }

    static JsonElement Structured(JsonElement envelope) =>
        envelope.GetProperty("result").GetProperty("structuredContent");

    [Fact]
    public async Task TraceDependencies_ReportsTheSameShaderGuidInspectAssetAlreadySurfaces_InsteadOfABareEmpty()
    {
        if (!Directory.Exists(RealProject)) return;
        if (!File.Exists(Path.Combine(RealProject, Material))) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        // Ground truth #1: inspect_asset already surfaces the shader guid link directly from the
        // material's own YAML, on-disk-scan resolved (or not) independent of the graph — the
        // "inspect_asset resolves those exact GUID links" half of F6-honesty's complaint.
        var inspected = Structured(await McpTestClient.CallTool(_factory, "inspect_asset", new { path = Material }));
        var shader = inspected.GetProperty("material").GetProperty("shader");
        var shaderGuid = shader.GetProperty("guid").GetString();

        Console.WriteLine($"[inspect_asset] {Material} shader.guid={shaderGuid} "
            + $"shader.resolved={shader.GetProperty("resolved").GetBoolean()}");

        Assert.False(string.IsNullOrEmpty(shaderGuid));

        // Ground truth #2 (F6-honesty itself): trace_dependencies on the SAME file, before this
        // fix, was {results: [], truncated: false} — an authoritative-looking "depends on
        // nothing". It must now report the same guid as a dangling dependency instead of a bare
        // empty, with the boundary note naming shaders explicitly.
        var traced = Structured(await McpTestClient.CallTool(_factory, "trace_dependencies", new { assetPath = Material }));
        var danglingCount = traced.GetProperty("danglingCount").GetInt32();

        Console.WriteLine($"[trace_dependencies] {Material} results={traced.GetProperty("results").GetArrayLength()} "
            + $"danglingCount={danglingCount}");

        Assert.True(danglingCount > 0,
            "trace_dependencies reported a bare empty for a material inspect_asset shows a real shader dependency for");

        var dangling = traced.GetProperty("dangling").EnumerateArray().ToList();
        Assert.Contains(dangling, d => d.GetProperty("toGuid").GetString() == shaderGuid);

        var note = traced.GetProperty("danglingNote").GetString();
        Assert.Contains("Library/PackageCache", note);
    }

    public void Dispose()
    {
        // See EditorToolTestBase.Dispose's own comment: _factory is a fresh per-test
        // WebApplicationFactory whose own background services can still be touching _appRoot
        // until the host itself is disposed - which must happen before the delete below.
        _factory.Dispose();

        if (Directory.Exists(_appRoot)) Directory.Delete(_appRoot, recursive: true);
    }
}
