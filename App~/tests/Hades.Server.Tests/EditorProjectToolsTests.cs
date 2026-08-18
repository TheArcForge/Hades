using Hades.Contract.Wire;
using Hades.Core.Editors;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Tests;

/// <summary>
/// EditorProjectTools' surviving tools over the full MCP/HTTP path - same scope and conventions the
/// deleted EditorSceneToolsTests/EditorMaterialToolsTests used: a fake Unity Editor plays canned
/// wire responses, so these prove the params sent and the result mapped, NOT the reload-lease
/// mechanics or the release-before-trigger ordering (both plugin-side only - see
/// ProjectCommandsTests).
///
/// Plan 10 Task 6 removed this file's tests for scene_open (folded into scene_manage's "open" op -
/// see SceneManageTests.cs), project_refresh_assets (folded into asset_manage's "refresh" op - see
/// AssetManageTests.cs), and the standalone BeginScriptEditing/EndScriptEditing/
/// hades_regression_record/hades_regression_replay (folded into script_editing_session/
/// hades_regression, both still tested below under their new names) - along with the tools
/// themselves.
/// </summary>
public sealed class EditorProjectToolsTests(WebApplicationFactory<Program> factory) : EditorToolTestBase(factory)
{
    static JsonValue Obj(params (string Key, JsonValue Value)[] members)
    {
        var o = JsonValue.NewObject();
        foreach (var (key, value) in members) o.SetProperty(key, value);
        return o;
    }

    // ---------------------------------------------------------------- project_recompile_scripts

    [Fact]
    public async Task ProjectRecompileScripts_SendsWireMethod_MapsResult()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(("requested", JsonValue.Bool(true))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "project_recompile_scripts", new { }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("project.recompile_scripts", request.Method);
        Assert.True(structured.GetProperty("requested").GetBoolean());
    }

    // ---------------------------------------------------------------- project_run_tests

    [Fact]
    public async Task ProjectRunTests_SendsFilterAndTestMode_MapsHandleWithoutWaitingForCompletion()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("runId", JsonValue.String("abc123")), ("status", JsonValue.String("started")),
            ("testMode", JsonValue.String("EditMode")), ("filter", JsonValue.String("MyTests"))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "project_run_tests", new { filter = "MyTests", testMode = "EditMode" }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("project.run_tests", request.Method);
        Assert.True(request.Params!.TryGetProperty("filter", out var f) && f!.AsString() == "MyTests");
        Assert.Equal("started", structured.GetProperty("status").GetString());
        Assert.Equal("abc123", structured.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task ProjectRunTests_NoFilterOrMode_OmitsBothParams()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("runId", JsonValue.String("xyz")), ("status", JsonValue.String("started")), ("testMode", JsonValue.String("EditMode"))));

        await McpTestClient.CallTool(Factory, "project_run_tests", new { });
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(request.Params!.TryGetProperty("filter", out _));
        Assert.False(request.Params!.TryGetProperty("testMode", out _));
    }

    [Fact]
    public async Task ProjectRunTests_StartFailure_MapsFailedStatusAndError()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("runId", JsonValue.String("abc")), ("status", JsonValue.String("failed")), ("testMode", JsonValue.String("EditMode")),
            ("error", JsonValue.String("Test Framework package not installed."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "project_run_tests", new { }));
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("failed", structured.GetProperty("status").GetString());
        Assert.Equal("Test Framework package not installed.", structured.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ProjectRunTests_DescriptionDisclosesEditModeReloadAndDeniesSceneSave()
    {
        // F12 was "PlayMode runs silently save the open scene to disk" - traced to Plugin~'s own
        // RunTests calling an explicit, UNCONDITIONAL scene-save before EVERY run (not just
        // PlayMode), never to any inherent Unity behavior: Unity itself never needs a disk write to
        // protect scene state across a domain reload or a PlayMode enter/exit (see
        // ProjectCommands.RunTests' own doc comment, Plugin~, for the full account). Fixed by
        // removing that call outright, so this now pins the description's new, TRUE shape: still
        // calls out EditMode's own domain reload (unchanged, real), and now explicitly DENIES ever
        // saving a scene, rather than warning that it does.
        var tools = (await McpTestClient.ListTools(Factory)).GetProperty("result").GetProperty("tools");
        var tool = Assert.Single(tools.EnumerateArray(), t => t.GetProperty("name").GetString() == "project_run_tests");
        var description = tool.GetProperty("description").GetString()!;

        Assert.Contains("domain reload", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never saves", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("open scene", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unity saves", description, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- project_get_console_log
    //
    // Class 4 (live-state read - Plan 9 Task 5): same "params sent, result mapped" scope as every
    // tool above - the ring buffer's bounding/filtering and the boot-time subscription are
    // plugin-side only (see ProjectCommandsTests in Plugin~/Tests/Editor).

    [Fact]
    public async Task ProjectGetConsoleLog_SendsCountAndType_MapsEntries()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        var pluginResult = Obj(
            ("entries", JsonValue.NewArray().Add(Obj(
                ("type", JsonValue.String("Error")),
                ("message", JsonValue.String("NullReferenceException")),
                ("stackTrace", JsonValue.String("at Foo.Bar()"))))),
            ("count", JsonValue.Integer(1)),
            ("totalBuffered", JsonValue.Integer(37)),
            ("totalMatching", JsonValue.Integer(9)));
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, pluginResult);

        var structured = Structured(await McpTestClient.CallTool(Factory, "project_get_console_log", new { count = 10, type = "Error" }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("project.get_console_log", request.Method);
        Assert.True(request.Params!.TryGetProperty("count", out var c) && c!.AsInteger() == 10);
        Assert.True(request.Params!.TryGetProperty("type", out var t) && t!.AsString() == "Error");

        Assert.Equal(1, structured.GetProperty("count").GetInt32());
        Assert.Equal(37, structured.GetProperty("totalBuffered").GetInt32());
        Assert.Equal(9, structured.GetProperty("totalMatching").GetInt32());
        var entry = structured.GetProperty("entries")[0];
        Assert.Equal("Error", entry.GetProperty("type").GetString());
        Assert.Equal("NullReferenceException", entry.GetProperty("message").GetString());
        Assert.Equal("at Foo.Bar()", entry.GetProperty("stackTrace").GetString());
    }

    [Fact]
    public async Task ProjectGetConsoleLog_NoArgs_OmitsCountAndTypeParams()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("entries", JsonValue.NewArray()), ("count", JsonValue.Integer(0)), ("totalBuffered", JsonValue.Integer(0))));

        await McpTestClient.CallTool(Factory, "project_get_console_log", new { });
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(request.Params is not null && request.Params.TryGetProperty("count", out _));
        Assert.False(request.Params is not null && request.Params.TryGetProperty("type", out _));
    }

    [Fact]
    public async Task ProjectGetConsoleLog_PluginError_PropagatesAsToolError()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenFailAsync(reads, writes, "'type' must be 'Error', 'Warning', or 'Log' (omit for every severity) - got 'Eror'.");

        var envelope = await McpTestClient.CallTool(Factory, "project_get_console_log", new { type = "Eror" });
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("'type' must be", McpTestClient.ErrorText(envelope));
    }

    [Fact]
    public async Task ProjectGetConsoleLog_OutputSchema_DeclaresTotalMatching()
    {
        // 'totalMatching' (the count of buffered entries matching 'type', before 'count' capped
        // the take) must be advertised in the tool's own outputSchema - the same "independent
        // match count" shape find_references_to's totalReferences uses - so a caller can tell
        // whether more matches exist beyond what 'entries' returned without cross-referencing
        // undocumented wire behaviour.
        var tools = (await McpTestClient.ListTools(Factory)).GetProperty("result").GetProperty("tools");
        var tool = Assert.Single(tools.EnumerateArray(), t => t.GetProperty("name").GetString() == "project_get_console_log");

        var properties = tool.GetProperty("outputSchema").GetProperty("properties");
        Assert.True(properties.TryGetProperty("totalMatching", out _),
            "project_get_console_log's outputSchema is missing 'totalMatching'");
    }

    // ---------------------------------------------------------------- project_get_test_results
    //
    // Class 4 (live-state read - Plan 9 Task 5): same "params sent, result mapped" scope - the
    // runId reconciliation (running/unknown/complete) and TestRunResultStore's own baseline logic
    // are plugin-side only (see ProjectCommandsTests).

    [Fact]
    public async Task ProjectGetTestResults_SendsRunId_MapsCompleteResultWithFailures()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        var pluginResult = Obj(
            ("status", JsonValue.String("complete")),
            ("runId", JsonValue.String("abc123")),
            ("total", JsonValue.Integer(3)),
            ("passed", JsonValue.Integer(2)),
            ("failed", JsonValue.Integer(1)),
            ("skipped", JsonValue.Integer(0)),
            ("inconclusive", JsonValue.Integer(0)),
            ("duration", JsonValue.String("1.234")),
            ("failures", JsonValue.NewArray().Add(Obj(
                ("name", JsonValue.String("MyTests.TestC")),
                ("message", JsonValue.String("Expected true but was false"))))));
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, pluginResult);

        var structured = Structured(await McpTestClient.CallTool(Factory, "project_get_test_results", new { runId = "abc123" }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("project.get_test_results", request.Method);
        Assert.True(request.Params!.TryGetProperty("runId", out var r) && r!.AsString() == "abc123");

        Assert.Equal("complete", structured.GetProperty("status").GetString());
        Assert.Equal("abc123", structured.GetProperty("runId").GetString());
        Assert.Equal(3, structured.GetProperty("total").GetInt32());
        Assert.Equal(2, structured.GetProperty("passed").GetInt32());
        Assert.Equal(1, structured.GetProperty("failed").GetInt32());
        Assert.Equal("1.234", structured.GetProperty("duration").GetString());
        Assert.Equal("MyTests.TestC", structured.GetProperty("failures")[0].GetProperty("name").GetString());
        Assert.Equal("Expected true but was false", structured.GetProperty("failures")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ProjectGetTestResults_OmittedRunId_OmitsParam_MapsNoneStatus()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("status", JsonValue.String("none")),
            ("note", JsonValue.String("No test run has been started this session. Call project_run_tests first."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "project_get_test_results", new { }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(request.Params is not null && request.Params.TryGetProperty("runId", out _));
        Assert.Equal("none", structured.GetProperty("status").GetString());
        Assert.False(structured.TryGetProperty("total", out _));
    }

    [Fact]
    public async Task ProjectGetTestResults_RunningStatus_MapsRunIdAndNote_ReportsPlainlyNotEmpty()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("status", JsonValue.String("running")), ("runId", JsonValue.String("run-1")),
            ("note", JsonValue.String("Test run in progress (EditMode runs include a domain reload). Poll again shortly."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "project_get_test_results", new { }));
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("running", structured.GetProperty("status").GetString());
        Assert.Equal("run-1", structured.GetProperty("runId").GetString());
        Assert.False(string.IsNullOrEmpty(structured.GetProperty("note").GetString()));
        Assert.False(structured.TryGetProperty("total", out _));
    }

    [Fact]
    public async Task ProjectGetTestResults_UnknownRunId_MapsUnknownStatusRunIdAndNote()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("status", JsonValue.String("unknown")), ("runId", JsonValue.String("stale-run")),
            ("note", JsonValue.String("No test run with runId 'stale-run' is known. The most recently started run's id is 'real-run'."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "project_get_test_results", new { runId = "stale-run" }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(request.Params!.TryGetProperty("runId", out var r) && r!.AsString() == "stale-run");
        Assert.Equal("unknown", structured.GetProperty("status").GetString());
        Assert.Equal("stale-run", structured.GetProperty("runId").GetString());
        Assert.Contains("real-run", structured.GetProperty("note").GetString());
    }

    [Fact]
    public async Task ProjectGetTestResults_PluginError_PropagatesAsToolError()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenFailAsync(reads, writes, "Test run 'x' looked complete, but its results at '...' could not be parsed: bad xml.");

        var envelope = await McpTestClient.CallTool(Factory, "project_get_test_results", new { runId = "x" });
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("could not be parsed", McpTestClient.ErrorText(envelope));
    }

    // ---------------------------------------------------------------- script_editing_session (Plan 10 Task 5)
    //
    // Plan 10 Task 6 removed this file's tests for the two standalone tools this merges
    // (BeginScriptEditing/EndScriptEditing, PascalCase, matching the old package's own naming)
    // along with the tools themselves - script_editing_session is naming/parameter merge only, so
    // these tests prove exactly what those used to: a fake Unity Editor plays canned wire
    // responses, proving only the params sent and the result mapped. The real lease mechanics this
    // merge must NOT change - an exception leaves the lease held, TTL/disconnect are the nets, 'end'
    // without 'begin' is idempotent and calls Unlock zero times, 'end' releases then triggers
    // recompile in that exact order - are plugin-side properties, proven against a REAL ReloadGate
    // in Plugin~/Tests/Editor/ScriptEditingSessionTests.cs, dispatched there by WIRE method name
    // (project.begin_script_editing/project.end_script_editing) - a name this app-level rename
    // never touches, so those tests remain valid completely unmodified.

    [Fact]
    public async Task ScriptEditingSession_Begin_NoTtl_OmitsTtlSecondsParam_MapsLeaseIdAndExpiry()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("leaseId", JsonValue.String("hades-script-editing")), ("expiresAtUtcMs", JsonValue.Integer(1_700_000_030_000))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "script_editing_session", new { action = "begin" }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("project.begin_script_editing", request.Method);
        Assert.False(request.Params is not null && request.Params.TryGetProperty("ttlSeconds", out _));
        Assert.Equal("hades-script-editing", structured.GetProperty("leaseId").GetString());
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_030_000), structured.GetProperty("expiresAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task ScriptEditingSession_Begin_WithTtlSeconds_SendsTtlSecondsParam()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("leaseId", JsonValue.String("hades-script-editing")), ("expiresAtUtcMs", JsonValue.Integer(1_700_000_120_000))));

        await McpTestClient.CallTool(Factory, "script_editing_session", new { action = "begin", ttlSeconds = 120.0 });
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(request.Params!.TryGetProperty("ttlSeconds", out var t) && t!.AsDouble() == 120.0);
    }

    // ---------------------------------------------------------------- script_editing_session out-of-range expiresAtUtcMs (B-F2)
    //
    // DateTimeOffset.FromUnixTimeMilliseconds throws ArgumentOutOfRangeException outside its
    // representable range. Thrown here, between the plugin taking Unity's reload lock (this
    // 'begin' call already told it to) and leases.RecordHeld below, that would leave the plugin
    // holding the lock with the app never having recorded it - a desync that only self-heals at
    // the plugin's own lease TTL - and would surface as a raw, non-McpException error instead of
    // a clean tool failure. Clamping instead of throwing means the call still succeeds and
    // RecordHeld still runs.

    [Fact]
    public async Task ScriptEditingSession_Begin_ExpiresAtUtcMsAboveValidRange_ClampsAndStillRecordsTheLease()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("leaseId", JsonValue.String("hades-script-editing")), ("expiresAtUtcMs", JsonValue.Integer(long.MaxValue))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "script_editing_session", new { action = "begin" }));
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        // No raw ArgumentOutOfRangeException surfaced - the call completed normally with a
        // clamped, still-usable expiry. Not literally DateTimeOffset.MaxValue: round-tripping
        // through Unix milliseconds (what the clamp actually computes with) loses MaxValue's
        // sub-millisecond ticks, so the true clamp boundary is a hair below it.
        var expectedClamp = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.MaxValue.ToUnixTimeMilliseconds());
        Assert.Equal(expectedClamp, structured.GetProperty("expiresAtUtc").GetDateTimeOffset());

        // And the lease WAS recorded - RecordHeld ran despite the garbage expiry, so the
        // plugin-holds-lock-app-unaware desync cannot occur here.
        var lease = Factory.Services.GetRequiredService<LeaseRegistry>().Get(ProjectGuid);
        Assert.NotNull(lease);
        Assert.Equal("hades-script-editing", lease!.LeaseId);
        Assert.Equal(expectedClamp, lease.ExpiresAtUtc);
    }

    [Fact]
    public async Task ScriptEditingSession_Begin_ExpiresAtUtcMsBelowValidRange_ClampsWithoutThrowing()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("leaseId", JsonValue.String("hades-script-editing")), ("expiresAtUtcMs", JsonValue.Integer(long.MinValue))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "script_editing_session", new { action = "begin" }));
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        // Clamped to the other end of the representable range - still no raw exception - even
        // though a this-far-in-the-past expiry self-expires immediately in LeaseRegistry (a
        // separate, correct concern - see LeaseRegistry.Get's own doc comment - so it is not
        // asserted as still-held here).
        Assert.Equal(DateTimeOffset.MinValue, structured.GetProperty("expiresAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task ScriptEditingSession_Begin_LeaseBusyElsewhere_PropagatesPluginErrorAsToolError()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenFailAsync(reads, writes,
            "BeginScriptEditing needs Unity's reload lock, but it is currently held by lease 'hades-script-editing'.");

        var envelope = await McpTestClient.CallTool(Factory, "script_editing_session", new { action = "begin" });
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("reload lock", McpTestClient.ErrorText(envelope));
    }

    [Fact]
    public async Task ScriptEditingSession_End_SendsWireMethod_MapsReleasedAndRequested()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("released", JsonValue.Bool(true)), ("requested", JsonValue.Bool(true))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "script_editing_session", new { action = "end" }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("project.end_script_editing", request.Method);
        Assert.True(structured.GetProperty("released").GetBoolean());
        Assert.True(structured.GetProperty("requested").GetBoolean());
    }

    [Fact]
    public async Task ScriptEditingSession_End_WithoutMatchingBegin_MapsReleasedFalse_StillRequested()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("released", JsonValue.Bool(false)), ("requested", JsonValue.Bool(true))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "script_editing_session", new { action = "end" }));
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(structured.GetProperty("released").GetBoolean());
        Assert.True(structured.GetProperty("requested").GetBoolean());
    }

    [Fact]
    public async Task ScriptEditingSession_UnknownAction_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "script_editing_session", new { action = "pause" });

        Assert.Contains("action", McpTestClient.ErrorText(envelope));
    }

    [Fact]
    public async Task ScriptEditingSession_BlankAction_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "script_editing_session", new { action = "" });

        Assert.Contains("action", McpTestClient.ErrorText(envelope));
    }

    // ---------------------------------------------------------------- script_editing_session wiring into LeaseRegistry
    //
    // Re-proves Plan 9's Defect 2 fix (the original fix's own wiring tests, against the standalone
    // BeginScriptEditing/EndScriptEditing tools, are gone along with those tools - Plan 10 Task 6)
    // under the NEW tool name - the fifth of the five lease properties this consolidation must not
    // regress. If script_editing_session's 'begin'/'end' stopped calling LeaseRegistry.RecordHeld/
    // Clear, one of these two tests would fail.

    [Fact]
    public async Task ScriptEditingSession_Begin_RecordsHeldLease_WithThePluginsActualExpiry_NotTheRequestedTtl()
    {
        // Deliberately NOT "5 seconds from now" (what a ttlSeconds=5.0 echo would produce) and
        // deliberately relative to UtcNow rather than a fixed literal - LeaseRegistry.Get now
        // self-expires against real wall-clock time (see its class doc comment), so a hardcoded
        // past-by-now literal would make this test's own later Get(ProjectGuid) call see nothing,
        // for reasons that have nothing to do with what this test is actually proving.
        var actualExpiryMs = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeMilliseconds();

        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("leaseId", JsonValue.String("hades-script-editing")), ("expiresAtUtcMs", JsonValue.Integer(actualExpiryMs))));

        // ttlSeconds=5 requested, but the plugin answers with a DIFFERENT actual expiry
        // (actualExpiryMs, ~30 minutes out) - LeaseRegistry must record THAT, never an echo of the
        // request.
        await McpTestClient.CallTool(Factory, "script_editing_session", new { action = "begin", ttlSeconds = 5.0 });
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        var lease = Factory.Services.GetRequiredService<LeaseRegistry>().Get(ProjectGuid);
        Assert.NotNull(lease);
        Assert.Equal("hades-script-editing", lease!.LeaseId);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(actualExpiryMs), lease.ExpiresAtUtc);
    }

    [Fact]
    public async Task ScriptEditingSession_End_ClearsWhateverThisAppBelievedWasHeld()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        var leases = Factory.Services.GetRequiredService<LeaseRegistry>();
        leases.RecordHeld(ProjectGuid, "hades-script-editing", DateTimeOffset.UtcNow.AddSeconds(30));

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("released", JsonValue.Bool(true)), ("requested", JsonValue.Bool(true))));

        await McpTestClient.CallTool(Factory, "script_editing_session", new { action = "end" });
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(leases.Get(ProjectGuid));
    }

    [Fact]
    public async Task ScriptEditingSession_End_WithoutMatchingBegin_StillClearsRegistry_IdempotentNoOp()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("released", JsonValue.Bool(false)), ("requested", JsonValue.Bool(true))));

        await McpTestClient.CallTool(Factory, "script_editing_session", new { action = "end" });
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(Factory.Services.GetRequiredService<LeaseRegistry>().Get(ProjectGuid));
    }

    // ---------------------------------------------------------------- hades_regression (Plan 10 Task 5)
    //
    // Naming/parameter merge of hades_regression_record (action='start'|'stop') and
    // hades_regression_replay ('calls') into one tool, action='start'|'stop'|'replay' - same scope
    // as the tests for the two originals above: params sent, result mapped, via a fake Unity Editor.
    //
    // F15: action='start'/'stop' no longer talk to the Editor at all (see EditorProjectTools'
    // own class doc comment and RegressionRecorder) - the two tests that used to prove a wire round
    // trip for them are replaced below by tests proving the new, editor-independent contract
    // instead. action='replay' is unchanged for entries with no 'format' field (see
    // HadesRegression_ActionReplay_EntryWithNoFormatField_... below) and gains a second path for
    // 'format':'tool' entries.

    [Fact]
    public async Task HadesRegression_ActionStart_NoEditorNeeded_BeginsEmptyRecording()
    {
        var structured = Structured(await McpTestClient.CallTool(Factory, "hades_regression", new { action = "start" }));
        Assert.True(structured.GetProperty("recording").GetBoolean());

        // Stopping with nothing recorded in between is the same safe no-op the pre-F15 Editor-side
        // session always was - see RegressionRecorder.Stop's own doc comment.
        var stopped = Structured(await McpTestClient.CallTool(Factory, "hades_regression", new { action = "stop" }));
        Assert.Equal(0, stopped.GetProperty("count").GetInt32());
        Assert.Empty(stopped.GetProperty("calls").EnumerateArray());
    }

    [Fact]
    public async Task HadesRegression_ActionStart_AlreadyRecording_RefusesWithActionableError_NoEditorNeeded()
    {
        await McpTestClient.CallTool(Factory, "hades_regression", new { action = "start" });

        var envelope = await McpTestClient.CallTool(Factory, "hades_regression", new { action = "start" });

        Assert.Contains("already active", McpTestClient.ErrorText(envelope));
    }

    // ---- F15: the graph/disk observability gap this plan closes ------------------------------
    //
    // Before this change, a recording session only ever saw tool calls the attached Unity Editor
    // itself executed (Plugin~'s CommandTable.Dispatch offering each one to
    // ProjectCommands.CaptureIfRecording) - graph- and disk-served tools such as search_by_name,
    // find_references_to, graph_query, and project_settings never reached the Editor at all, so a
    // session recording six mixed calls captured only the two Editor-routed ones. These two tests
    // are the failing-then-passing proof that capturing now happens one layer up, at Program.cs's
    // own CallToolFilters, which sees every tool dispatch regardless of how it answers.

    [Fact]
    public async Task HadesRegression_Start_CapturesAGraphServedToolCall_AndStopResultReplaysGreen()
    {
        await McpTestClient.CallTool(Factory, "hades_regression", new { action = "start" });

        // search_by_name is graph-served - answered entirely from the indexed project, never
        // touching the attached Editor - exactly the kind of call the pre-F15 recorder could not
        // see at all.
        await McpTestClient.CallTool(Factory, "search_by_name", new { namePattern = "NothingShouldMatchThisPattern" });

        var stopped = Structured(await McpTestClient.CallTool(Factory, "hades_regression", new { action = "stop" }));
        Assert.Equal(1, stopped.GetProperty("count").GetInt32());

        var call = stopped.GetProperty("calls")[0];
        Assert.Equal("search_by_name", call.GetProperty("method").GetString());
        Assert.Equal("tool", call.GetProperty("format").GetString());
        Assert.Equal("NothingShouldMatchThisPattern", call.GetProperty("params").GetProperty("namePattern").GetString());

        // 'stop's own 'calls' hands straight into 'replay' with no translation step - the same
        // invariant the record/replay shape has always kept, now proven for the new shape too.
        var replayed = Structured(await McpTestClient.CallTool(Factory, "hades_regression",
            new { action = "replay", calls = stopped.GetProperty("calls") }));

        Assert.Equal(1, replayed.GetProperty("total").GetInt32());
        Assert.Equal(1, replayed.GetProperty("passed").GetInt32());
        Assert.Equal(0, replayed.GetProperty("failed").GetInt32());
        var replayResult = replayed.GetProperty("results")[0];
        Assert.True(replayResult.GetProperty("passed").GetBoolean());
        Assert.Equal("search_by_name", replayResult.GetProperty("method").GetString());
    }

    [Fact]
    public async Task HadesRegression_MixedSession_CapturesBothEditorRoutedAndGraphServedCalls()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        await McpTestClient.CallTool(Factory, "hades_regression", new { action = "start" });

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(("requested", JsonValue.Bool(true))));
        await McpTestClient.CallTool(Factory, "project_recompile_scripts", new { });
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        await McpTestClient.CallTool(Factory, "search_by_name", new { namePattern = "StillNothingShouldMatch" });

        var stopped = Structured(await McpTestClient.CallTool(Factory, "hades_regression", new { action = "stop" }));

        Assert.Equal(2, stopped.GetProperty("count").GetInt32());
        var methods = stopped.GetProperty("calls").EnumerateArray()
            .Select(c => c.GetProperty("method").GetString())
            .ToList();
        Assert.Contains("project_recompile_scripts", methods);
        Assert.Contains("search_by_name", methods);

        // Both captured as the SAME new shape - format="tool" - regardless of the fact that one of
        // them happens to be Editor-routed internally. Capture no longer knows or cares.
        Assert.All(stopped.GetProperty("calls").EnumerateArray(),
            call => Assert.Equal("tool", call.GetProperty("format").GetString()));
    }

    // ---- F15: fixture-shape backward compatibility ----------------------------------------------
    //
    // scripts/regression/fixtures/editor-routed.json - and every fixture recorded before this
    // change - has no 'format' field on any entry. This is the proof that shape keeps replaying
    // exactly as it did before: dispatched by Plugin~ wire method name through the attached Editor,
    // in ONE batched hades.regression_replay wire call, never treated as an MCP tool name.

    [Fact]
    public async Task HadesRegression_ActionReplay_EntryWithNoFormatField_StillDispatchesByWireMethodThroughTheEditor()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("results", JsonValue.NewArray().Add(Obj(
                ("method", JsonValue.String("scene.create_gameobject")),
                ("passed", JsonValue.Bool(true)),
                ("actual", Obj(("name", JsonValue.String("RegSuiteHost"))))))),
            ("total", JsonValue.Integer(1)), ("passed", JsonValue.Integer(1)), ("failed", JsonValue.Integer(0))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "hades_regression", new
        {
            action = "replay",
            calls = new[]
            {
                new
                {
                    method = "scene.create_gameobject",
                    @params = new Dictionary<string, object?> { ["name"] = "RegSuiteHost" },
                    expected = (object?)null,
                },
            },
        }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("hades.regression_replay", request.Method);
        Assert.True(request.Params!.TryGetProperty("calls", out var wireCalls)
            && wireCalls!.Kind == WireKind.Array && wireCalls.Items.Count == 1);
        Assert.Equal(1, structured.GetProperty("total").GetInt32());
        Assert.Equal(1, structured.GetProperty("passed").GetInt32());
        Assert.True(structured.GetProperty("results")[0].GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task HadesRegression_ActionReplay_SendsCallsWithMethodParamsAndExpected_MapsPerEntryResults()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        var pluginResult = Obj(
            ("results", JsonValue.NewArray().Add(Obj(
                ("method", JsonValue.String("scene.create_gameobject")),
                ("passed", JsonValue.Bool(true)),
                ("actual", Obj(("name", JsonValue.String("Foo"))))))),
            ("total", JsonValue.Integer(1)), ("passed", JsonValue.Integer(1)), ("failed", JsonValue.Integer(0)));
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, pluginResult);

        var structured = Structured(await McpTestClient.CallTool(Factory, "hades_regression", new
        {
            action = "replay",
            calls = new[]
            {
                new
                {
                    method = "scene.create_gameobject",
                    @params = new Dictionary<string, object?> { ["name"] = "Foo" },
                    expected = (object?)null,
                },
            },
        }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("hades.regression_replay", request.Method);
        Assert.True(request.Params!.TryGetProperty("calls", out var calls) && calls!.Kind == WireKind.Array && calls.Items.Count == 1);
        Assert.True(calls.Items[0].TryGetProperty("method", out var methodValue) && methodValue!.AsString() == "scene.create_gameobject");
        Assert.Equal(1, structured.GetProperty("total").GetInt32());
        Assert.Equal(1, structured.GetProperty("passed").GetInt32());
        Assert.True(structured.GetProperty("results")[0].GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task HadesRegression_ActionReplay_ReportsMixedPassFail()
    {
        // Byte-for-byte the same scenario HadesRegressionReplay_ReportsMixedPassFail (above) proves
        // through the old hades_regression_replay tool - re-run here through the new consolidated
        // name/shape, since a batch replay's core value (one failing call does not stop the batch,
        // or hide behind an aggregate) is exactly the "partial failure reported per-item" property
        // the whole consolidation plan requires.
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        var pluginResult = Obj(
            ("results", JsonValue.NewArray()
                .Add(Obj(("method", JsonValue.String("scene.create_gameobject")), ("passed", JsonValue.Bool(true))))
                .Add(Obj(("method", JsonValue.String("not.a.method")), ("passed", JsonValue.Bool(false)), ("error", JsonValue.String("Method 'not.a.method' is not implemented yet."))))),
            ("total", JsonValue.Integer(2)), ("passed", JsonValue.Integer(1)), ("failed", JsonValue.Integer(1)));
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, pluginResult);

        var structured = Structured(await McpTestClient.CallTool(Factory, "hades_regression", new
        {
            action = "replay",
            calls = new[]
            {
                new { method = "scene.create_gameobject", @params = (Dictionary<string, object?>?)null, expected = (object?)null },
                new { method = "not.a.method", @params = (Dictionary<string, object?>?)null, expected = (object?)null },
            },
        }));
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, structured.GetProperty("total").GetInt32());
        Assert.Equal(1, structured.GetProperty("passed").GetInt32());
        Assert.Equal(1, structured.GetProperty("failed").GetInt32());
        Assert.True(structured.GetProperty("results")[0].GetProperty("passed").GetBoolean());
        Assert.False(structured.GetProperty("results")[1].GetProperty("passed").GetBoolean());
        Assert.Contains("not implemented", structured.GetProperty("results")[1].GetProperty("error").GetString());
    }

    [Fact]
    public async Task HadesRegression_ActionReplay_EmptyCalls_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "hades_regression", new { action = "replay", calls = Array.Empty<object>() });

        Assert.Contains("calls", McpTestClient.ErrorText(envelope));
    }

    [Fact]
    public async Task HadesRegression_ActionReplay_MissingCalls_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "hades_regression", new { action = "replay" });

        Assert.Contains("calls", McpTestClient.ErrorText(envelope));
    }

    [Fact]
    public async Task HadesRegression_UnknownAction_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "hades_regression", new { action = "pause" });

        Assert.Contains("action", McpTestClient.ErrorText(envelope));
    }

    [Fact]
    public async Task HadesRegression_BlankAction_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "hades_regression", new { action = "" });

        Assert.Contains("action", McpTestClient.ErrorText(envelope));
    }
}
