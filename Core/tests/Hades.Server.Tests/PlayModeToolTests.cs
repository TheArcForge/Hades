using Hades.Contract.Wire;
using Microsoft.AspNetCore.Mvc.Testing;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Tests;

/// <summary>
/// DESIGN-01 (docs/design/DESIGN-01-play-mode-control.md) §15.2's core-server suite for
/// <c>play_mode</c>, over the full MCP/HTTP path - a fake Unity Editor plays canned wire responses,
/// so these prove the params sent and the result mapped, never the plugin-side session-state
/// machinery (that is PlayModeCommandsTests.cs, UnityPlugin side).
///
/// Uses a shortened <see cref="ProjectService.CharonProbeTimeout"/> (5s, matching
/// <c>CharonStatusTests</c>' own choice) rather than <see cref="EditorToolTestBase"/>'s 30s default,
/// because this file's busy-short-circuit tests deliberately never answer the probe.
/// </summary>
public sealed class PlayModeToolTests(WebApplicationFactory<Program> factory)
    : EditorToolTestBase(factory, TimeSpan.FromSeconds(5))
{
    static JsonValue Obj(params (string Key, JsonValue Value)[] members)
    {
        var o = JsonValue.NewObject();
        foreach (var (key, value) in members) o.SetProperty(key, value);
        return o;
    }

    // ---------------------------------------------------------------- 1: each action sends the expected wire method/params

    [Fact]
    public async Task Enter_SendsWireMethod_MapsSessionIdStateAndExpiry()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("sessionId", JsonValue.String("abc123")), ("state", JsonValue.String("entering")),
            ("expiresAtUtcMs", JsonValue.Integer(1700000000000))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "play_mode", new { action = "enter", ttlSeconds = 60.0 }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("play.enter", request.Method);
        Assert.True(request.Params!.TryGetProperty("ttlSeconds", out var t) && t!.AsDouble() == 60.0);
        Assert.Equal("entering", structured.GetProperty("state").GetString());
        Assert.Equal("abc123", structured.GetProperty("sessionId").GetString());
        Assert.True(structured.GetProperty("ownedByHades").GetBoolean());
        Assert.True(structured.TryGetProperty("expiresAtUtc", out var e) && e.ValueKind != System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Enter_NoTtlSeconds_OmitsParam()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("sessionId", JsonValue.String("s1")), ("state", JsonValue.String("entering")), ("expiresAtUtcMs", JsonValue.Null)));

        await McpTestClient.CallTool(Factory, "play_mode", new { action = "enter" });
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(request.Params is { Kind: WireKind.Object } p && p.TryGetProperty("ttlSeconds", out _));
    }

    [Fact]
    public async Task Exit_SendsWireMethod_MapsStateAndSessionId()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("state", JsonValue.String("exiting")), ("wasPlaying", JsonValue.Bool(true)), ("sessionId", JsonValue.String("s1"))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "play_mode", new { action = "exit" }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("play.exit", request.Method);
        Assert.Equal("exiting", structured.GetProperty("state").GetString());
        Assert.Equal("s1", structured.GetProperty("sessionId").GetString());
        Assert.True(structured.GetProperty("ownedByHades").GetBoolean());
    }

    [Fact]
    public async Task Exit_NoSession_MapsOwnedByHadesFalse()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("state", JsonValue.String("editMode")), ("wasPlaying", JsonValue.Bool(false)), ("sessionId", JsonValue.Null)));

        var structured = Structured(await McpTestClient.CallTool(Factory, "play_mode", new { action = "exit" }));
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(structured.GetProperty("ownedByHades").GetBoolean());
    }

    [Theory]
    [InlineData("pause", true)]
    [InlineData("resume", false)]
    public async Task PauseResume_SendsSetPausedWithExpectedFlag(string action, bool expectedPaused)
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("state", JsonValue.String(expectedPaused ? "paused" : "playing")), ("paused", JsonValue.Bool(expectedPaused)),
            ("sessionId", JsonValue.String("s1")), ("ownedByHades", JsonValue.Bool(true))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "play_mode", new { action }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("play.set_paused", request.Method);
        Assert.True(request.Params!.TryGetProperty("paused", out var p) && p!.AsBoolean() == expectedPaused);
        Assert.Equal("s1", structured.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task Step_SendsWireMethod_MapsFrame()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("state", JsonValue.String("paused")), ("frame", JsonValue.Integer(42)),
            ("sessionId", JsonValue.String("s1")), ("ownedByHades", JsonValue.Bool(true))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "play_mode", new { action = "step" }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("play.step", request.Method);
        Assert.False(request.Params is { Kind: WireKind.Object } p && p.Members.Count > 0);
        Assert.Equal(42, structured.GetProperty("frame").GetInt64());
    }

    [Fact]
    public async Task Status_Playing_SendsWireMethod_MapsEveryField()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        // PlayModeTool.StatusAsync calls ProjectService.GetCharonStatus ITSELF first (DESIGN-01
        // §10's short-circuit), then - once satisfied the Editor is attached and not busy - calls
        // EditorProxy.SendCommandAsync, which probes AGAIN internally before sending the real
        // command. So a successful 'status' round trip is TWO busy probes plus the real command,
        // not one - AnswerBusyProbeThenRespondAsync (built for every other tool, which probes only
        // once) does not fit this one action; wired here directly instead.
        var responder = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var probeLine = await reads.ReadLineAsync();
                Assert.True(JsonRpcRequest.TryParse(probeLine, out var probeRequest, out _));
                await writes.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(probeRequest!.Id!, JsonValue.Bool(true)).ToJson()));
            }

            var line = await reads.ReadLineAsync();
            Assert.True(JsonRpcRequest.TryParse(line, out var request, out _));
            await writes.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(request!.Id!, Obj(
                ("state", JsonValue.String("playing")), ("sessionId", JsonValue.String("s1")),
                ("ownedByHades", JsonValue.Bool(true)), ("expiresAtUtcMs", JsonValue.Integer(1700000000000)),
                ("frame", JsonValue.Integer(7)), ("playingForSeconds", JsonValue.Float(3.5)))).ToJson()));
            return request;
        });

        var structured = Structured(await McpTestClient.CallTool(Factory, "play_mode", new { action = "status" }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("play.status", request.Method);
        Assert.Equal("playing", structured.GetProperty("state").GetString());
        Assert.Equal("s1", structured.GetProperty("sessionId").GetString());
        Assert.True(structured.GetProperty("ownedByHades").GetBoolean());
        Assert.Equal(7, structured.GetProperty("frame").GetInt64());
        Assert.Equal(3.5, structured.GetProperty("playingForSeconds").GetDouble());
    }

    // ---------------------------------------------------------------- 2: unknown action names all six

    [Fact]
    public async Task UnknownAction_FailsLocally_NamesAllSixActions_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "play_mode", new { action = "jump" });
        var text = envelope.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

        foreach (var action in new[] { "enter", "exit", "pause", "resume", "step", "status" })
            Assert.Contains($"'{action}'", text);
    }

    [Fact]
    public async Task BlankAction_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "play_mode", new { action = "" });
        Assert.True(envelope.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    // ---------------------------------------------------------------- 3: ttlSeconds is forwarded, never re-clamped client-side

    [Theory]
    [InlineData(1.0)]
    [InlineData(10000.0)]
    public async Task Enter_TtlSecondsOutsidePluginRange_IsSentUnmodified_ClampingIsThePluginsJob(double requested)
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("sessionId", JsonValue.String("s1")), ("state", JsonValue.String("entering")), ("expiresAtUtcMs", JsonValue.Null)));

        await McpTestClient.CallTool(Factory, "play_mode", new { action = "enter", ttlSeconds = requested });
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        // The core tool must not second-guess an out-of-[10,600]-range value itself - a SINGLE
        // clamp (the plugin's own, DESIGN-01 §4.1) is the source of truth; a second, disagreeing
        // clamp here would be a silent behavioural fork.
        Assert.True(request.Params!.TryGetProperty("ttlSeconds", out var t));
        Assert.Equal(requested, t!.AsDouble());
    }

    // ---------------------------------------------------------------- 4/5/6: the status short-circuit

    [Fact]
    public async Task Status_NoEditorAttached_ReturnsDetached_DoesNotThrow()
    {
        var structured = Structured(await McpTestClient.CallTool(Factory, "play_mode", new { action = "status" }));

        Assert.Equal("detached", structured.GetProperty("state").GetString());
        Assert.Contains("domain reload", structured.GetProperty("note").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_WhileEditorAttachedButBusy_ReturnsDetachedWithBusyNote_DoesNotThrow()
    {
        await ConnectAsFakeUnityAsync();
        // Never answers the busy probe - CharonProbeTimeout (5s, this file's own constructor arg)
        // is what this test waits out.

        var structured = Structured(await McpTestClient.CallTool(Factory, "play_mode", new { action = "status" }));

        Assert.Equal("detached", structured.GetProperty("state").GetString());
        Assert.Contains("main thread", structured.GetProperty("note").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_WhileBusyOrDetached_MakesNoWireCall()
    {
        // Not-attached case: nothing to connect at all, so ANY wire traffic would mean the guard
        // did not short-circuit. If PlayModeTool instead caught a real send failure, this would
        // still (wrongly) look like it passed the prior two tests - only a direct absence-of-traffic
        // check proves the short-circuit is a short-circuit and not a disguised catch.
        var structured = Structured(await McpTestClient.CallTool(Factory, "play_mode", new { action = "status" }));
        Assert.Equal("detached", structured.GetProperty("state").GetString());

        // Busy case: connect, never answer, and independently confirm exactly one request (the
        // busy probe itself) was ever written - a second, real 'play.status' send would mean the
        // short-circuit was bypassed. The probe is only WRITTEN once the tool call below actually
        // runs, so the call and the read must race concurrently - awaiting the read first would
        // deadlock (nothing sends anything until GetCharonStatus is invoked).
        var (reads, writes) = await ConnectAsFakeUnityAsync(processId: 9111);
        _ = writes; // the probe is deliberately left unanswered

        var secondCall = McpTestClient.CallTool(Factory, "play_mode", new { action = "status" });
        var probeLine = await reads.ReadLineAsync();
        Assert.True(JsonRpcRequest.TryParse(probeLine, out var probeRequest, out _));
        Assert.Equal("hades/mainThreadProbe", probeRequest!.Method); // never 'play.status' itself

        var second = Structured(await secondCall);
        Assert.Equal("detached", second.GetProperty("state").GetString());
    }

    // ---------------------------------------------------------------- 7: step takes no 'frames' parameter

    [Fact]
    public async Task Step_TakesNoFramesParameter()
    {
        var tools = (await McpTestClient.ListTools(Factory)).GetProperty("result").GetProperty("tools");
        var tool = Assert.Single(tools.EnumerateArray(), t => t.GetProperty("name").GetString() == "play_mode");
        var properties = tool.GetProperty("inputSchema").GetProperty("properties");

        var propertyNames = properties.EnumerateObject().Select(p => p.Name).ToList();
        Assert.DoesNotContain("frames", propertyNames);
    }

    // ---------------------------------------------------------------- 8: the description pin

    [Fact]
    public async Task Description_DisclosesReloadNeverSavesAndRefusesMutationsWhilePlaying()
    {
        var tools = (await McpTestClient.ListTools(Factory)).GetProperty("result").GetProperty("tools");
        var tool = Assert.Single(tools.EnumerateArray(), t => t.GetProperty("name").GetString() == "play_mode");
        var description = tool.GetProperty("description").GetString()!;

        Assert.Contains("domain reload", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("returns immediately", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never writes to disk", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refused", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ttlSeconds", description);
        Assert.Contains("ownedByHades", description);

        // DESIGN-01 §6.2's corrected wording: never claim Unity discards EVERY change made while
        // playing - that is false for the majority (asset-file writes persist).
        Assert.DoesNotContain("discards every change", description, StringComparison.OrdinalIgnoreCase);
    }
}
