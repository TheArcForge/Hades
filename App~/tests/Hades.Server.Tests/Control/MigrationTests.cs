using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hades.Core;
using Hades.Core.Migration;
using Hades.Core.Storage;
using Hades.Server.Control;

namespace Hades.Server.Tests.Control;

/// <summary>
/// <c>/control/migration/*</c> - the missing caller Plan 14 Task 10 adds: detection (read-only),
/// memory/traces import (non-destructive), and the four independently-authorised cleanup steps,
/// over real HTTP against a directly-constructed <see cref="ControlListener"/>. Same style as every
/// other Control *EndpointHttpTests class in this directory (see <see cref="MemoryEndpointHttpTests"/>).
///
/// Every fixture here is a synthetic scratch project under <see cref="Path.GetTempPath"/> - never
/// the user's real Hades-Unity-Client checkout - and every claude_desktop_config.json fixture is a
/// scratch file threaded through <see cref="ControlListener"/>'s <c>claudeDesktopConfigPath</c>
/// constructor override, never the real machine path (see <see cref="MigrationEndpoint.CleanClaudeDesktopConfig"/>'s
/// own doc comment for why that override exists at all).
/// </summary>
public sealed class MigrationEndpointHttpTests : IDisposable
{
    readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _claudeDesktopScratchPath;
    string ConnectionFilePath => Path.Combine(_tempDir, "control.token");

    readonly ProjectService _projects;
    readonly string _productGuid;

    static readonly string Start = V12Detector.StartMarker;
    static readonly string End = V12Detector.EndMarker;

    public MigrationEndpointHttpTests()
    {
        const string guid = "aaaabbbbccccddddeeeeffff11112222";
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"), $"  productGUID: {guid}\n");

        _claudeDesktopScratchPath = Path.Combine(_tempDir, "claude_desktop_config.json");

        _projects = new ProjectService(new AppPaths(Path.Combine(_tempDir, "app")));
        var project = _projects.Adopt(_projectRoot);
        _productGuid = project!.ProductGuid;
    }

    // ---- fixture helpers, mirroring V12Detector/V12Cleanup/V12Importer's own test fixtures ----

    void WriteManifest(string content)
    {
        var dir = Path.Combine(_projectRoot, "Packages");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), content);
    }

    void WriteV12ManifestEntry() => WriteManifest("""{ "dependencies": { "com.arcforge.hades": "file:/Users/mike/Projects/Hades" } }""");

    string ClaudeMdPath => Path.Combine(_projectRoot, "CLAUDE.md");
    void WriteClaudeMd(string content) => File.WriteAllText(ClaudeMdPath, content);

    void WriteMemoryFile(string relativePath, string content = "# doc\n")
    {
        var path = Path.Combine(_projectRoot, ".arcforge", "memory", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    void WriteTracesDb(string content = "sqlite-traces-bytes")
    {
        var dir = Path.Combine(_projectRoot, ".arcforge");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "traces.db"), content);
    }

    string McpJsonPath => Path.Combine(_projectRoot, ".mcp.json");
    void WriteMcpJson() => File.WriteAllText(McpJsonPath, """{ "mcpServers": { "hades": { "command": "node" } } }""");

    void WriteClaudeDesktopConfig(string content) => File.WriteAllText(_claudeDesktopScratchPath, content);

    static (byte[] Content, DateTime WriteTimeUtc) Snapshot(string path) =>
        (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));

    static void AssertUnchanged(string path, (byte[] Content, DateTime WriteTimeUtc) before)
    {
        var after = Snapshot(path);
        Assert.True(before.Content.AsSpan().SequenceEqual(after.Content), $"'{path}' content changed");
        Assert.Equal(before.WriteTimeUtc, after.WriteTimeUtc);
    }

    static HttpRequestMessage Request(HttpMethod method, string path, string? token, object? jsonBody = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (jsonBody is not null) request.Content = JsonContent.Create(jsonBody);
        return request;
    }

    static HttpClient ClientFor(ControlListener listener) => new() { BaseAddress = new Uri($"http://127.0.0.1:{listener.Port}") };

    ControlListener StartListener() =>
        new(ConnectionFilePath, projects: _projects, claudeDesktopConfigPath: _claudeDesktopScratchPath);

    // ==================================================================
    // Detection - read-only, safe by construction
    // ==================================================================

    [Fact]
    public async Task Detect_V12Project_ReportsIsV12ProjectTrueAndEveryItemPresent()
    {
        WriteV12ManifestEntry();
        WriteMemoryFile("conventions.md");
        WriteMemoryFile("proposals/idea.md");
        WriteTracesDb();
        WriteMcpJson();
        WriteClaudeMd($"before\n{Start}\nblock\n{End}\nafter\n");

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/migration/{_productGuid}/detect", listener.Token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.True(root.GetProperty("isV12Project").GetBoolean());
        Assert.True(root.GetProperty("manifestEntry").GetProperty("present").GetBoolean());
        Assert.True(root.GetProperty("hasMemory").GetBoolean());
        Assert.Equal(2, root.GetProperty("memoryDocumentCount").GetInt32());
        Assert.True(root.GetProperty("hasTraces").GetBoolean());
        Assert.False(root.GetProperty("hasGraph").GetBoolean());
        Assert.True(root.GetProperty("hasGeneratedMcpConfig").GetBoolean());
        Assert.Equal("marked", root.GetProperty("claudeMd").GetProperty("shape").GetString());
        Assert.False(root.GetProperty("hasUnityPlugin").GetBoolean());
    }

    [Fact]
    public async Task Detect_NonV12Project_ReportsIsV12ProjectFalse()
    {
        WriteManifest("""{ "dependencies": { "com.unity.textmeshpro": "3.0.6" } }""");

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/migration/{_productGuid}/detect", listener.Token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("isV12Project").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("manifestEntry").GetProperty("present").GetBoolean());
    }

    [Fact]
    public async Task Detect_UnknownProductGuid_Returns404()
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/control/migration/does-not-exist/detect", listener.Token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Detect_NeverWritesAnythingToTheProject()
    {
        WriteV12ManifestEntry();
        WriteMemoryFile("conventions.md");
        WriteClaudeMd($"before\n{Start}\nblock\n{End}\nafter\n");

        var (filesBefore, _) = Snapshot2(_projectRoot);

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);
        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/migration/{_productGuid}/detect", listener.Token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var (filesAfter, _) = Snapshot2(_projectRoot);
        Assert.Equal(filesBefore.Keys.OrderBy(k => k, StringComparer.Ordinal), filesAfter.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (path, before) in filesBefore)
        {
            Assert.True(before.AsSpan().SequenceEqual(filesAfter[path]), $"'{path}' changed after detection");
        }
    }

    static (Dictionary<string, byte[]> Files, HashSet<string> Dirs) Snapshot2(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllBytes);
        var dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(root, d)).ToHashSet();
        return (files, dirs);
    }

    // ---- auth spot check ----

    [Fact]
    public async Task Detect_NoToken_IsRefused()
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/control/migration/{_productGuid}/detect", token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ==================================================================
    // Import - memory mandatory, traces optional, both non-destructive
    // ==================================================================

    [Fact]
    public async Task ImportMemory_CopiesDocuments_ReturnsImportedNames_SourceUntouched()
    {
        WriteMemoryFile("conventions.md", "# Conventions\n");
        var sourcePath = Path.Combine(_projectRoot, ".arcforge", "memory", "conventions.md");
        var before = Snapshot(sourcePath);

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/control/migration/{_productGuid}/importMemory", listener.Token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var imported = doc.RootElement.GetProperty("imported").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("conventions.md", imported);
        Assert.Empty(doc.RootElement.GetProperty("skipped").EnumerateArray());

        AssertUnchanged(sourcePath, before);
    }

    [Fact]
    public async Task ImportMemory_Collision_ReportsSkippedWithReason_NeverOverwrites()
    {
        WriteMemoryFile("conventions.md", "FROM ARCFORGE\n");

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        // First import claims the name; a second import (e.g. a re-offered migration) must report
        // a collision, never silently duplicate or overwrite.
        await client.SendAsync(Request(HttpMethod.Post, $"/control/migration/{_productGuid}/importMemory", listener.Token));
        var response = await client.SendAsync(Request(HttpMethod.Post, $"/control/migration/{_productGuid}/importMemory", listener.Token));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(doc.RootElement.GetProperty("imported").EnumerateArray());
        var skipped = Assert.Single(doc.RootElement.GetProperty("skipped").EnumerateArray());
        Assert.Equal("conventions.md", skipped.GetProperty("source").GetString());
        Assert.False(string.IsNullOrWhiteSpace(skipped.GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task ImportTraces_CopiesTracesDb_ReportsImportedTrue()
    {
        WriteTracesDb("sqlite-traces-bytes");

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/control/migration/{_productGuid}/importTraces", listener.Token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("imported").GetBoolean());
    }

    [Fact]
    public async Task ImportTraces_NoSourceFile_ReportsImportedFalseWithReason_WritesNothing()
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/control/migration/{_productGuid}/importTraces", listener.Token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("imported").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("skippedReason").GetString()));
    }

    [Fact]
    public async Task ImportMemory_UnknownProductGuid_Returns404()
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/control/migration/does-not-exist/importMemory", listener.Token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ==================================================================
    // Cleanup - the destructive, individually-authorised part
    // ==================================================================

    [Fact]
    public async Task CleanClaudeMd_ProceedTrue_RemovesOnlyTheBlock()
    {
        WriteClaudeMd("before\n" + Start + "\nblock\n" + End + "\nafter\n");

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/control/migration/{_productGuid}/cleanClaudeMd", listener.Token,
            jsonBody: new { proceed = true }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("removed").GetBoolean());
        // Only content[Start..End) is removed - the "\n" that sits between "after\n" and the
        // marker's own End is NOT part of the block, so it survives (matching V12Cleanup's own
        // "removes exactly the marked block and nothing else" contract).
        Assert.Equal("before\n\nafter\n", File.ReadAllText(ClaudeMdPath));
    }

    [Fact]
    public async Task CleanClaudeMd_UnmarkedPrefixThenMarkedBlock_NoGoAhead_ReportsRemainingContentOutsideBlockBeforeActing()
    {
        // The pre-action twin of the test directly below: a caller building a confirmation prompt
        // needs remainingContentOutsideBlock to be accurate BEFORE agreeing (proceed:false), not
        // only after. Proves the fix surfaces over real HTTP, not just inside V12Cleanup itself.
        const string unmarkedPrefix = "# Hades — Agent Guidelines\n\nOld template revision, no markers.\n";
        const string markedBlock = "Newer template revision, with markers.\n";
        WriteClaudeMd(unmarkedPrefix + Start + "\n" + markedBlock + End + "\n");
        var before = Snapshot(ClaudeMdPath);

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/control/migration/{_productGuid}/cleanClaudeMd", listener.Token,
            jsonBody: new { proceed = false }));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("removed").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("remainingContentOutsideBlock").GetBoolean());
        AssertUnchanged(ClaudeMdPath, before);
    }

    [Fact]
    public async Task CleanClaudeMd_UnmarkedPrefixThenMarkedBlock_RemovesBlockButReportsRemainingContentOutsideBlock()
    {
        // Mirrors the real Hades-Unity-Client shape task 2 found: unmarked Hades-authored content
        // from an older template revision, followed by a marked block with a newer revision. This
        // is the exact guarantee the brief calls out: "cleanup succeeded" and "the file is now
        // clean" are different claims, and RemainingContentOutsideBlock is how the API tells them
        // apart rather than letting the shell imply the file is now fully clean.
        const string unmarkedPrefix =
            "# Hades — Agent Guidelines\n\nOld template revision, no markers.\n";
        const string markedBlock = "Newer template revision, with markers.\n";
        WriteClaudeMd(unmarkedPrefix + Start + "\n" + markedBlock + End + "\n");

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/control/migration/{_productGuid}/cleanClaudeMd", listener.Token,
            jsonBody: new { proceed = true }));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("removed").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("remainingContentOutsideBlock").GetBoolean());
        Assert.Equal(unmarkedPrefix + "\n", File.ReadAllText(ClaudeMdPath));
    }

    [Fact]
    public async Task CleanClaudeMd_BlockIsEntireFile_RemainingContentOutsideBlockIsFalse()
    {
        WriteClaudeMd(Start + "\nblock\n" + End);

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/control/migration/{_productGuid}/cleanClaudeMd", listener.Token,
            jsonBody: new { proceed = true }));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("removed").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("remainingContentOutsideBlock").GetBoolean());
    }

    [Fact]
    public async Task CleanClaudeMd_Unmarked_NeverDeletes_RegardlessOfProceed()
    {
        WriteClaudeMd("# Hand-written, nothing to do with Hades.\n");
        var before = Snapshot(ClaudeMdPath);

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/control/migration/{_productGuid}/cleanClaudeMd", listener.Token,
            jsonBody: new { proceed = true }));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("removed").GetBoolean());
        AssertUnchanged(ClaudeMdPath, before);
    }

    [Fact]
    public async Task CleanManifest_ProceedTrue_RemovesEntry_RestOfFileByteIdentical()
    {
        WriteManifest(
            "{\n" +
            "  \"dependencies\": {\n" +
            "    \"com.arcforge.hades\": \"file:/Users/mike/Projects/Hades\",\n" +
            "    \"com.unity.collab-proxy\": \"2.10.2\"\n" +
            "  }\n" +
            "}\n");

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/control/migration/{_productGuid}/cleanManifest", listener.Token,
            jsonBody: new { proceed = true }));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("removed").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("occurrencesFound").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("portConflictWarning").GetString()));
        Assert.Equal(
            "{\n" +
            "  \"dependencies\": {\n" +
            "    \"com.unity.collab-proxy\": \"2.10.2\"\n" +
            "  }\n" +
            "}\n",
            File.ReadAllText(Path.Combine(_projectRoot, "Packages", "manifest.json")));
    }

    [Fact]
    public async Task CleanMcpConfig_ProceedTrue_DeletesTheFile()
    {
        WriteMcpJson();

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/control/migration/{_productGuid}/cleanMcpConfig", listener.Token,
            jsonBody: new { proceed = true }));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("removed").GetBoolean());
        Assert.False(File.Exists(McpJsonPath));
    }

    [Fact]
    public async Task CleanClaudeMd_UnknownProductGuid_Returns404()
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/control/migration/does-not-exist/cleanClaudeMd", listener.Token,
            jsonBody: new { proceed = true }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- no-go-ahead: nothing on disk changes, across every cleanup target ----

    [Fact]
    public async Task NoGoAhead_AcrossEveryCleanupRoute_NothingOnDiskChangesAtAll()
    {
        WriteClaudeMd("before\n" + Start + "\nblock\n" + End + "\nafter\n");
        WriteManifest("""{ "dependencies": { "com.arcforge.hades": "file:/Users/mike/Projects/Hades", "com.unity.collab-proxy": "2.10.2" } }""");
        WriteMcpJson();
        WriteClaudeDesktopConfig("""{ "mcpServers": { "hades": { "command": "node" }, "other": { "command": "x" } } }""");

        var claudeMdBefore = Snapshot(ClaudeMdPath);
        var manifestBefore = Snapshot(Path.Combine(_projectRoot, "Packages", "manifest.json"));
        var mcpJsonBefore = Snapshot(McpJsonPath);
        var desktopConfigBefore = Snapshot(_claudeDesktopScratchPath);

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var claudeMdResponse = await client.SendAsync(Request(HttpMethod.Post, $"/control/migration/{_productGuid}/cleanClaudeMd", listener.Token, jsonBody: new { proceed = false }));
        var manifestResponse = await client.SendAsync(Request(HttpMethod.Post, $"/control/migration/{_productGuid}/cleanManifest", listener.Token, jsonBody: new { proceed = false }));
        var mcpConfigResponse = await client.SendAsync(Request(HttpMethod.Post, $"/control/migration/{_productGuid}/cleanMcpConfig", listener.Token, jsonBody: new { proceed = false }));
        var desktopConfigResponse = await client.SendAsync(Request(HttpMethod.Post, "/control/migration/claudeDesktopConfig/clean", listener.Token, jsonBody: new { proceed = false }));

        foreach (var response in new[] { claudeMdResponse, manifestResponse, mcpConfigResponse, desktopConfigResponse })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("removed").GetBoolean());
        }

        AssertUnchanged(ClaudeMdPath, claudeMdBefore);
        AssertUnchanged(Path.Combine(_projectRoot, "Packages", "manifest.json"), manifestBefore);
        AssertUnchanged(McpJsonPath, mcpJsonBefore);
        AssertUnchanged(_claudeDesktopScratchPath, desktopConfigBefore);
    }

    // ---- independence: one step refusing never blocks another from succeeding ----

    [Fact]
    public async Task EachCleanupStep_IsIndependentlyAuthorised_OneRefusingDoesNotBlockOthersSucceeding()
    {
        // CLAUDE.md is hand-written (Unmarked -> always refused, regardless of proceed); the other
        // three targets are all cleanly removable. Spec #10: every destructive step is
        // individually optional - one step's outcome must never depend on another's.
        WriteClaudeMd("# Hand-written, nothing to do with Hades.\n");
        WriteManifest("""{ "dependencies": { "com.arcforge.hades": "1.2.3" } }""");
        WriteMcpJson();
        WriteClaudeDesktopConfig("""{ "mcpServers": { "hades": { "command": "node" } } }""");

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var claudeMdResponse = await client.SendAsync(Request(HttpMethod.Post, $"/control/migration/{_productGuid}/cleanClaudeMd", listener.Token, jsonBody: new { proceed = true }));
        var manifestResponse = await client.SendAsync(Request(HttpMethod.Post, $"/control/migration/{_productGuid}/cleanManifest", listener.Token, jsonBody: new { proceed = true }));
        var mcpConfigResponse = await client.SendAsync(Request(HttpMethod.Post, $"/control/migration/{_productGuid}/cleanMcpConfig", listener.Token, jsonBody: new { proceed = true }));
        var desktopConfigResponse = await client.SendAsync(Request(HttpMethod.Post, "/control/migration/claudeDesktopConfig/clean", listener.Token, jsonBody: new { proceed = true }));

        using var claudeMdDoc = JsonDocument.Parse(await claudeMdResponse.Content.ReadAsStringAsync());
        Assert.False(claudeMdDoc.RootElement.GetProperty("removed").GetBoolean());
        Assert.True(File.Exists(ClaudeMdPath));

        using var manifestDoc = JsonDocument.Parse(await manifestResponse.Content.ReadAsStringAsync());
        Assert.True(manifestDoc.RootElement.GetProperty("removed").GetBoolean());

        using var mcpConfigDoc = JsonDocument.Parse(await mcpConfigResponse.Content.ReadAsStringAsync());
        Assert.True(mcpConfigDoc.RootElement.GetProperty("removed").GetBoolean());

        using var desktopConfigDoc = JsonDocument.Parse(await desktopConfigResponse.Content.ReadAsStringAsync());
        Assert.True(desktopConfigDoc.RootElement.GetProperty("removed").GetBoolean());
    }

    // ==================================================================
    // claude_desktop_config.json - global, per-user, NOT per-project
    // ==================================================================

    [Fact]
    public async Task CleanClaudeDesktopConfig_RouteCarriesNoProductGuid_WorksWithNoProjectInvolvedAtAll()
    {
        // A separate, completely empty ProjectService (no project ever adopted) proves this route
        // has no dependency on any known project whatsoever - it is reached purely by its own
        // literal path, never through a {productGuid} segment.
        WriteClaudeDesktopConfig("""{ "mcpServers": { "hades": { "command": "node" }, "other-server": { "command": "npx" } } }""");

        var projects = new ProjectService(new AppPaths(Path.Combine(_tempDir, "empty-app")));
        using var listener = new ControlListener(Path.Combine(_tempDir, "empty.token"), projects: projects, claudeDesktopConfigPath: _claudeDesktopScratchPath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/control/migration/claudeDesktopConfig/clean", listener.Token, jsonBody: new { proceed = true }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("removed").GetBoolean());
        Assert.Contains("other-server", File.ReadAllText(_claudeDesktopScratchPath));
        Assert.DoesNotContain("\"hades\"", File.ReadAllText(_claudeDesktopScratchPath));
    }

    [Fact]
    public async Task CleanClaudeDesktopConfig_NoGoAhead_ReportsOccurrencesFoundWithoutRemoving()
    {
        // This route has no companion per-project detect endpoint (it is global - see this class's
        // own doc comment), so occurrencesFound on a proceed:false dry run is the only way a caller
        // can tell "there is a hades entry to offer cleaning up" from "there is nothing here at
        // all". Proves the field survives the .NET -> wire -> HTTP round trip, not just inside
        // V12Cleanup itself.
        WriteClaudeDesktopConfig("""{ "mcpServers": { "hades": { "command": "node" } } }""");

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/control/migration/claudeDesktopConfig/clean", listener.Token,
            jsonBody: new { proceed = false }));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("removed").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("occurrencesFound").GetInt32());
    }

    [Fact]
    public async Task CleanClaudeDesktopConfig_NoHadesEntry_OccurrencesFoundIsZero()
    {
        WriteClaudeDesktopConfig("""{ "mcpServers": { "other-server": { "command": "npx" } } }""");

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/control/migration/claudeDesktopConfig/clean", listener.Token,
            jsonBody: new { proceed = true }));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("removed").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("occurrencesFound").GetInt32());
    }

    [Fact]
    public async Task CleanClaudeDesktopConfig_AlwaysReportsAGlobalScopeWarning()
    {
        WriteClaudeDesktopConfig("""{ "mcpServers": { "hades": { "command": "node" } } }""");

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/control/migration/claudeDesktopConfig/clean", listener.Token, jsonBody: new { proceed = true }));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var scopeWarning = doc.RootElement.GetProperty("scopeWarning").GetString();
        Assert.False(string.IsNullOrWhiteSpace(scopeWarning));
        Assert.Contains("global", scopeWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CleanClaudeDesktopConfig_OnlyHadesEntryRemoved_OtherServersAndKeysSurvive()
    {
        WriteClaudeDesktopConfig(
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"hades\": {\n" +
            "      \"command\": \"node\"\n" +
            "    },\n" +
            "    \"other-server\": {\n" +
            "      \"command\": \"npx\"\n" +
            "    }\n" +
            "  },\n" +
            "  \"preferences\": {\n" +
            "    \"keepAwakeEnabled\": true\n" +
            "  }\n" +
            "}\n");

        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/control/migration/claudeDesktopConfig/clean", listener.Token, jsonBody: new { proceed = true }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"other-server\": {\n" +
            "      \"command\": \"npx\"\n" +
            "    }\n" +
            "  },\n" +
            "  \"preferences\": {\n" +
            "    \"keepAwakeEnabled\": true\n" +
            "  }\n" +
            "}\n",
            File.ReadAllText(_claudeDesktopScratchPath));
    }

    [Fact]
    public async Task CleanClaudeDesktopConfig_NoToken_IsRefused()
    {
        using var listener = StartListener();
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/control/migration/claudeDesktopConfig/clean", token: null, jsonBody: new { proceed = true }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _tempDir, _projectRoot })
        {
            if (!Directory.Exists(dir)) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }
}
