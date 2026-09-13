using Hades.Contract.Wire;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hades.Server.Tests;

/// <summary>
/// DESIGN-06 (docs/design/DESIGN-06-baking-and-subsystems.md) §9's core-server suite for
/// <c>scene_bake</c>. This class is a thin wire proxy over the plugin's own <c>scene.bake</c>
/// command - every state-model rule (the epoch ownership token, the sticky Terminal, the
/// three-valued <c>Resolve</c> predicate) lives entirely in the plugin
/// (UnityPlugin/Tests/Editor/SceneBakeCommandsTests.cs is where those are proven); this suite only
/// proves the wire shape - what gets sent, and how the plugin's response maps onto
/// <see cref="SceneBakeResult"/>.
/// </summary>
public sealed class SceneBakeTests(WebApplicationFactory<Program> factory)
    : EditorToolTestBase(factory, TimeSpan.FromSeconds(5))
{
    static JsonValue Obj(params (string Key, JsonValue Value)[] members)
    {
        var o = JsonValue.NewObject();
        foreach (var (key, value) in members) o.SetProperty(key, value);
        return o;
    }

    // ---------------------------------------------------------------- action validation (no Editor needed)

    [Fact]
    public async Task UnknownAction_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "scene_bake", new { action = "bogus" });
        Assert.True(envelope.GetProperty("result").GetProperty("isError").GetBoolean());
        var text = envelope.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("action", text);
    }

    [Fact]
    public async Task Start_WithNoTarget_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "scene_bake", new { action = "start" });
        Assert.True(envelope.GetProperty("result").GetProperty("isError").GetBoolean());
        var text = envelope.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("target", text);
    }

    // ---------------------------------------------------------------- start

    [Fact]
    public async Task Start_SendsWireMethodAndTarget_MapsResult()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("jobId", JsonValue.String("job-1")),
            ("target", JsonValue.String("lighting")),
            ("status", JsonValue.String("running")),
            ("progress", JsonValue.Float(0.25)),
            ("startedAtUtcMs", JsonValue.Integer(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),
            ("backend", JsonValue.String("ProgressiveGPU")),
            ("detail", JsonValue.String("A 'lighting' bake is currently running."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "scene_bake",
            new { action = "start", target = "lighting" }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("scene.bake", request.Method);
        Assert.Equal("start", request.Params!.TryGetProperty("action", out var a) ? a!.AsString() : null);
        Assert.Equal("lighting", request.Params!.TryGetProperty("target", out var t) ? t!.AsString() : null);

        Assert.Equal("job-1", structured.GetProperty("jobId").GetString());
        Assert.Equal("lighting", structured.GetProperty("target").GetString());
        Assert.Equal("running", structured.GetProperty("status").GetString());
        Assert.Equal(0.25, structured.GetProperty("progress").GetDouble());
        Assert.Equal("ProgressiveGPU", structured.GetProperty("backend").GetString());
    }

    [Fact]
    public async Task Start_Unavailable_MapsResult_NotAnError()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("target", JsonValue.String("navmesh")),
            ("status", JsonValue.String("unavailable")),
            ("detail", JsonValue.String("com.unity.ai.navigation is not installed..."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "scene_bake",
            new { action = "start", target = "navmesh" }));
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("unavailable", structured.GetProperty("status").GetString());
        Assert.Equal("navmesh", structured.GetProperty("target").GetString());
    }

    // ---------------------------------------------------------------- status

    [Fact]
    public async Task Status_OmitsJobId_SendsNoJobIdParam()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("status", JsonValue.String("none")),
            ("detail", JsonValue.String("No bake has been started this session."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "scene_bake", new { action = "status" }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(request.Params!.TryGetProperty("jobId", out _));
        Assert.Equal("none", structured.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Status_WithJobId_ForwardsIt()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("status", JsonValue.String("unknown")),
            ("jobId", JsonValue.String("stale-job")),
            ("detail", JsonValue.String("No bake with jobId 'stale-job' is known."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "scene_bake",
            new { action = "status", jobId = "stale-job" }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("stale-job", request.Params!.TryGetProperty("jobId", out var j) ? j!.AsString() : null);
        Assert.Equal("unknown", structured.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Status_Complete_MapsDurationAndBackend()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("jobId", JsonValue.String("job-2")),
            ("target", JsonValue.String("lighting")),
            ("status", JsonValue.String("complete")),
            ("startedAtUtcMs", JsonValue.Integer(DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds())),
            ("durationSeconds", JsonValue.Float(300.0)),
            ("backend", JsonValue.String("ProgressiveGPU")),
            ("detail", JsonValue.String("The 'lighting' bake ran to completion without this plugin observing an abort."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "scene_bake",
            new { action = "status" }));
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("complete", structured.GetProperty("status").GetString());
        Assert.Equal(300.0, structured.GetProperty("durationSeconds").GetDouble());
        Assert.Equal("ProgressiveGPU", structured.GetProperty("backend").GetString());
    }

    [Fact]
    public async Task Status_Indeterminate_NeverReportsCompleteFieldsWhenAbsent()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("jobId", JsonValue.String("job-3")),
            ("target", JsonValue.String("navmesh")),
            ("status", JsonValue.String("indeterminate")),
            ("detail", JsonValue.String("A recorded surface for this navmesh bake no longer resolves."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "scene_bake",
            new { action = "status" }));
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("indeterminate", structured.GetProperty("status").GetString());
        Assert.False(structured.TryGetProperty("durationSeconds", out _));
    }

    // ---------------------------------------------------------------- cancel

    [Fact]
    public async Task Cancel_SendsWireMethod_MapsResult()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("jobId", JsonValue.String("job-4")),
            ("target", JsonValue.String("occlusion")),
            ("status", JsonValue.String("cancelled")),
            ("detail", JsonValue.String("This bake was cancelled by an explicit scene_bake action='cancel' call."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "scene_bake", new { action = "cancel" }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("scene.bake", request.Method);
        Assert.Equal("cancel", request.Params!.TryGetProperty("action", out var a) ? a!.AsString() : null);
        Assert.Equal("cancelled", structured.GetProperty("status").GetString());
    }

    // ---------------------------------------------------------------- plugin-level failure propagates

    [Fact]
    public async Task Start_PluginLevelError_PropagatesAsToolError()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenFailAsync(reads, writes,
            "A bake Hades started (job 'abc', target 'lighting') is already in progress in this Editor.");

        var envelope = await McpTestClient.CallTool(Factory, "scene_bake", new { action = "start", target = "lighting" });
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(envelope.GetProperty("result").GetProperty("isError").GetBoolean());
        var text = envelope.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("already in progress", text);
    }
}
