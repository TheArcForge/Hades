using System.Text.Json;
using Hades.Contract.Wire;
using Hades.Core;
using Hades.Server.Control;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Hades.Server.Tests;

/// <summary>
/// DESIGN-03 (docs/design/DESIGN-03-build-pipeline.md) §11.1's core-server suite for
/// <c>project_build</c>. Unlike every other Editor-tool test file, 'status' tests never script a
/// fake Editor response for the action itself - the whole point of the status-file design is that
/// 'status' makes NO wire call, so these tests prove that by writing the status file directly and
/// showing the tool answers from it even when a connected fake Editor never receives (or would
/// refuse) anything.
///
/// Uses the shortened <see cref="ProjectService.CharonProbeTimeout"/> (5s, matching
/// <c>PlayModeToolTests</c>' own choice) so a test that (incorrectly) triggered a wire round trip
/// against a non-responding fake Editor would fail fast rather than waiting out the 30s default.
/// </summary>
public sealed class ProjectBuildTests(WebApplicationFactory<Program> factory)
    : EditorToolTestBase(factory, TimeSpan.FromSeconds(5))
{
    static JsonValue Obj(params (string Key, JsonValue Value)[] members)
    {
        var o = JsonValue.NewObject();
        foreach (var (key, value) in members) o.SetProperty(key, value);
        return o;
    }

    string ProjectRoot => Factory.Services.GetRequiredService<ProjectService>().Get(ProjectGuid)!.Path;

    string StatusFilePath => Path.Combine(ProjectRoot, "Library", "Hades", "build-status.json");

    void WriteStatusFile(object document)
    {
        var path = StatusFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(document));
    }

    // ---------------------------------------------------------------- start

    [Fact]
    public async Task Start_SendsWireMethod_MapsResult_RegistersAnOperation()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("buildId", JsonValue.String("build-1")), ("state", JsonValue.String("queued")),
            ("target", JsonValue.String("StandaloneOSX")), ("outputPath", JsonValue.String("Build/StandaloneOSX/Demo.app")),
            ("dirtyScenes", JsonValue.NewArray().Add(JsonValue.String("Assets/Scenes/Main.unity")))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "project_build",
            new { action = "start", target = "StandaloneOSX" }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("project.build_start", request.Method);
        Assert.Equal("StandaloneOSX", request.Params!.TryGetProperty("target", out var t) ? t!.AsString() : null);

        Assert.Equal("build-1", structured.GetProperty("buildId").GetString());
        Assert.Equal("queued", structured.GetProperty("state").GetString());
        Assert.Equal("StandaloneOSX", structured.GetProperty("target").GetString());
        Assert.Equal("Build/StandaloneOSX/Demo.app", structured.GetProperty("outputPath").GetString());
        Assert.Single(structured.GetProperty("dirtyScenes").EnumerateArray());

        var operationId = structured.GetProperty("operationId").GetString();
        Assert.False(string.IsNullOrEmpty(operationId));

        var operations = Factory.Services.GetRequiredService<OperationRegistry>();
        var record = operations.Get(operationId!);
        Assert.NotNull(record);
        Assert.Equal("build", record!.Kind);
    }

    [Fact]
    public async Task Start_NeitherTargetNorProfile_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "project_build", new { action = "start" });
        Assert.True(envelope.GetProperty("result").GetProperty("isError").GetBoolean());
        var text = envelope.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("target", text);
        Assert.Contains("profile", text);
    }

    [Fact]
    public async Task Start_TargetAndProfileTogether_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "project_build",
            new { action = "start", target = "StandaloneOSX", profile = "Assets/Build.asset" });
        Assert.True(envelope.GetProperty("result").GetProperty("isError").GetBoolean());
        var text = envelope.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("mutually exclusive", text);
    }

    [Fact]
    public async Task Start_DevelopmentWithProfile_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "project_build",
            new { action = "start", profile = "Assets/Build.asset", development = true });
        Assert.True(envelope.GetProperty("result").GetProperty("isError").GetBoolean());
        var text = envelope.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("mutually exclusive", text);
    }

    // ---------------------------------------------------------------- outputPath containment (refused before any wire call)

    [Theory]
    [InlineData("/tmp/somewhere-else")]
    [InlineData("../outside")]
    [InlineData("Assets/Build")]
    [InlineData("Library/Sneaky")]
    [InlineData("Packages/Sneaky")]
    [InlineData("ProjectSettings/Sneaky")]
    public async Task Start_BadOutputPath_RefusedBeforeAnyWireCall_NoEditorAttachedAtAll(string badPath)
    {
        // Deliberately no ConnectAsFakeUnityAsync() call anywhere in this test - if the containment
        // check did not run before EditorProxy.SendCommandAsync, the very first thing that call does
        // is GetCharonStatus, which (no Editor attached) throws a DIFFERENT, "not attached" error -
        // so a test asserting on the actual outputPath wording, with no Editor connected at all, is
        // proof the wire path was never reached.
        var envelope = await McpTestClient.CallTool(Factory, "project_build",
            new { action = "start", target = "StandaloneOSX", outputPath = badPath });
        Assert.True(envelope.GetProperty("result").GetProperty("isError").GetBoolean());
        var text = envelope.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("outputPath", text);
        Assert.DoesNotContain("Editor", text);
    }

    // ---------------------------------------------------------------- status (the key property: no wire call)

    [Fact]
    public async Task Status_AnswersFromDisk_NoEditorAttachedAtAll()
    {
        WriteStatusFile(new
        {
            schema = 1,
            buildId = "build-2",
            bootNonce = "nonce",
            state = "running",
            target = "StandaloneOSX",
            outputPath = "Build/StandaloneOSX/Demo.app",
            queuedAtUtcMs = 1000,
            startedAtUtcMs = 1500,
            editorLogPath = "/tmp/editor.log",
            errors = Array.Empty<object>(),
            errorsTruncated = false,
        });

        var structured = Structured(await McpTestClient.CallTool(Factory, "project_build", new { action = "status" }));

        Assert.Equal("build-2", structured.GetProperty("buildId").GetString());
        Assert.Equal("running", structured.GetProperty("state").GetString());
        Assert.Equal("StandaloneOSX", structured.GetProperty("target").GetString());
        Assert.Equal("/tmp/editor.log", structured.GetProperty("editorLogPath").GetString());
    }

    [Fact]
    public async Task Status_AnswersFromDisk_EvenWhileTheEditorNeverAnswersAnything()
    {
        // An Editor IS attached - and never answers a single request in this test - proving the
        // wire path was genuinely never reached, not merely never NEEDED (the previous test's "no
        // Editor at all" case leaves open the theoretical question of what would happen if one were
        // attached and busy; this closes it directly). No responder is ever wired up.
        await ConnectAsFakeUnityAsync();

        WriteStatusFile(new
        {
            schema = 1,
            buildId = "build-3",
            bootNonce = "nonce",
            state = "complete",
            target = "StandaloneOSX",
            outputPath = "Build/StandaloneOSX/Demo.app",
            totalErrors = 0,
            totalWarnings = 2,
            outputSizeBytes = 123456,
            errors = Array.Empty<object>(),
            errorsTruncated = false,
        });

        var structured = Structured(await McpTestClient.CallTool(Factory, "project_build", new { action = "status" }));

        Assert.Equal("complete", structured.GetProperty("state").GetString());
        Assert.Equal(0, structured.GetProperty("totalErrors").GetInt32());
        Assert.Equal(2, structured.GetProperty("totalWarnings").GetInt32());
        Assert.Equal(123456, structured.GetProperty("outputSizeBytes").GetInt64());
    }

    [Fact]
    public async Task Status_NoBuildEverStarted_ReportsNone_NotAnEmptyResult()
    {
        var structured = Structured(await McpTestClient.CallTool(Factory, "project_build", new { action = "status" }));
        Assert.Equal("none", structured.GetProperty("state").GetString());
        Assert.False(string.IsNullOrEmpty(structured.GetProperty("note").GetString()));
    }

    [Fact]
    public async Task Status_UnknownBuildId_ReportsUnknown_NamesTheCurrentBuildId()
    {
        WriteStatusFile(new { schema = 1, buildId = "current-build", state = "running", target = "StandaloneOSX", outputPath = "Build/StandaloneOSX/Demo.app" });

        var structured = Structured(await McpTestClient.CallTool(Factory, "project_build",
            new { action = "status", buildId = "some-other-id" }));

        Assert.Equal("unknown", structured.GetProperty("state").GetString());
        var note = structured.GetProperty("note").GetString()!;
        Assert.Contains("current-build", note);
    }

    [Fact]
    public async Task Status_MatchingBuildId_ReportsTheRealState()
    {
        WriteStatusFile(new { schema = 1, buildId = "the-build", state = "failed", target = "StandaloneOSX", outputPath = "Build/StandaloneOSX/Demo.app" });

        var structured = Structured(await McpTestClient.CallTool(Factory, "project_build",
            new { action = "status", buildId = "the-build" }));

        Assert.Equal("failed", structured.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Status_FutureSchema_FailsWithAnActionableMessage_NeverAnUnhandledException()
    {
        WriteStatusFile(new { schema = 99, buildId = "b", state = "running", target = "StandaloneOSX", outputPath = "x" });

        var envelope = await McpTestClient.CallTool(Factory, "project_build", new { action = "status" });

        Assert.True(envelope.GetProperty("result").GetProperty("isError").GetBoolean());
        var text = envelope.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("schema", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_MalformedJson_FailsWithAnActionableMessage_NeverAnUnhandledException()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatusFilePath)!);
        File.WriteAllText(StatusFilePath, "{ this is not valid json");

        var envelope = await McpTestClient.CallTool(Factory, "project_build", new { action = "status" });

        Assert.True(envelope.GetProperty("result").GetProperty("isError").GetBoolean());
        var text = envelope.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("could not be parsed", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_ErrorMessagesAreMappedThrough()
    {
        WriteStatusFile(new
        {
            schema = 1,
            buildId = "b",
            state = "failed",
            target = "StandaloneOSX",
            outputPath = "x",
            errors = new[] { new { step = "Compiling", type = "Error", content = "CS1234: boom" } },
            errorsTruncated = false,
        });

        var structured = Structured(await McpTestClient.CallTool(Factory, "project_build", new { action = "status" }));

        var errors = structured.GetProperty("errors");
        Assert.Equal(1, errors.GetArrayLength());
        Assert.Equal("Compiling", errors[0].GetProperty("step").GetString());
        Assert.Equal("CS1234: boom", errors[0].GetProperty("content").GetString());
    }

    // ---------------------------------------------------------------- targets

    [Fact]
    public async Task Targets_SendsWireMethod_MapsHostPlatformAndTargets()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var targetsJson = JsonValue.NewArray()
            .Add(Obj(("target", JsonValue.String("StandaloneOSX")), ("group", JsonValue.String("Standalone")), ("supported", JsonValue.Bool(true))))
            .Add(Obj(("target", JsonValue.String("Android")), ("group", JsonValue.String("Android")), ("supported", JsonValue.Bool(false))));
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("hostPlatform", JsonValue.String("OSXEditor")), ("targets", targetsJson)));

        var structured = Structured(await McpTestClient.CallTool(Factory, "project_build", new { action = "targets" }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("project.build_targets", request.Method);
        Assert.Equal("OSXEditor", structured.GetProperty("hostPlatform").GetString());
        var targets = structured.GetProperty("targets");
        Assert.Equal(2, targets.GetArrayLength());
        Assert.Equal("StandaloneOSX", targets[0].GetProperty("target").GetString());
        Assert.True(targets[0].GetProperty("supported").GetBoolean());
        Assert.False(targets[1].GetProperty("supported").GetBoolean());
    }

    // ---------------------------------------------------------------- action validation

    [Fact]
    public async Task UnknownAction_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "project_build", new { action = "bogus" });
        Assert.True(envelope.GetProperty("result").GetProperty("isError").GetBoolean());
        var text = envelope.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("'start'", text);
        Assert.Contains("'status'", text);
        Assert.Contains("'targets'", text);
    }
}
