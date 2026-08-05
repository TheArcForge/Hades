using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hades.Core;
using Hades.Core.Editors;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WireJson = Hades.Contract.Wire.JsonValue;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Mcp;

public sealed record RecompileScriptsResult
{
    [JsonPropertyName("requested")] public required bool Requested { get; init; }
}

public sealed record RunTestsResult
{
    [JsonPropertyName("runId")] public required string RunId { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("testMode")] public required string TestMode { get; init; }
    [JsonPropertyName("filter")] public string? Filter { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

public sealed record RegressionReplayEntryResult
{
    [JsonPropertyName("method")] public string? Method { get; init; }
    [JsonPropertyName("passed")] public required bool Passed { get; init; }
    [JsonPropertyName("actual")] public object? Actual { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>One entry of hades_regression's 'replay' action's 'calls' parameter: a Plugin~ wire
/// method name (e.g. "scene.create_gameobject", "component.set_property" - CommandTable's own
/// dispatch name, NOT this server's snake_case MCP tool name - see EditorComponentTools' own doc
/// comment for why the two differ), its params, and an optional expected result to diff against.</summary>
public sealed record RegressionCallSpec
{
    [JsonPropertyName("method")] public required string Method { get; init; }
    [JsonPropertyName("params")] public IReadOnlyDictionary<string, JsonElement>? Params { get; init; }
    [JsonPropertyName("expected")] public JsonElement? Expected { get; init; }
}

/// <summary>One entry hades_regression's 'stop' action returns - the SAME {method, params,
/// expected} JSON shape as <see cref="RegressionCallSpec"/> (hades_regression's OWN 'replay' input
/// shape), so a caller can hand this result's 'calls' straight into a later 'replay' action's
/// 'calls' parameter with no translation step. A separate type rather than literally
/// RegressionCallSpec because that type's fields are JsonElement-typed for MCP's own INPUT
/// binding - this is an OUTPUT, built from a plugin response, so Params/Expected follow the same
/// plain-CLR-object convention every other read-through result in this codebase already uses (see
/// WireJsonBridge.ToClr and e.g. RegressionReplayEntryResult.Actual above) rather than
/// constructing a JsonElement by hand.</summary>
public sealed record RegressionRecordedCallResult
{
    [JsonPropertyName("method")] public required string Method { get; init; }
    [JsonPropertyName("params")] public object? Params { get; init; }
    [JsonPropertyName("expected")] public object? Expected { get; init; }
}

/// <summary>script_editing_session's result - shaped by 'action': 'leaseId'/'expiresAtUtc'
/// populated (others null) for action='begin'; 'released'/'requested' populated (others null) for
/// action='end'.</summary>
public sealed record ScriptEditingSessionResult
{
    [JsonPropertyName("leaseId")] public string? LeaseId { get; init; }
    [JsonPropertyName("expiresAtUtc")] public DateTimeOffset? ExpiresAtUtc { get; init; }
    [JsonPropertyName("released")] public bool? Released { get; init; }
    [JsonPropertyName("requested")] public bool? Requested { get; init; }
}

/// <summary>hades_regression's result - shaped by 'action': 'recording' populated for
/// action='start'; 'calls'/'count' populated for action='stop' (reusing
/// <see cref="RegressionRecordedCallResult"/> verbatim); 'results'/'total'/'passed'/'failed'
/// populated for action='replay' (reusing <see cref="RegressionReplayEntryResult"/> verbatim) -
/// never inventing a parallel shape for either half.</summary>
public sealed record HadesRegressionResult
{
    [JsonPropertyName("recording")] public bool? Recording { get; init; }
    [JsonPropertyName("calls")] public IReadOnlyList<RegressionRecordedCallResult>? Calls { get; init; }
    [JsonPropertyName("count")] public int? Count { get; init; }
    [JsonPropertyName("results")] public IReadOnlyList<RegressionReplayEntryResult>? Results { get; init; }
    [JsonPropertyName("total")] public int? Total { get; init; }
    [JsonPropertyName("passed")] public int? Passed { get; init; }
    [JsonPropertyName("failed")] public int? Failed { get; init; }
}

public sealed record ConsoleLogEntryResult
{
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("stackTrace")] public required string StackTrace { get; init; }
}

public sealed record ConsoleLogResult
{
    [JsonPropertyName("entries")] public required IReadOnlyList<ConsoleLogEntryResult> Entries { get; init; }
    [JsonPropertyName("count")] public required int Count { get; init; }
    [JsonPropertyName("totalBuffered")] public required int TotalBuffered { get; init; }
}

/// <summary>One failed test case from a completed project_run_tests run.</summary>
public sealed record TestFailureResult
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
}

/// <summary>project_get_test_results' result, shaped by <see cref="Status"/>: "none" (no run ever
/// started - only Note is populated), "running" or "unknown" (RunId and Note populated, nothing
/// else), or "complete" (every count field, Duration, and Failures populated).</summary>
public sealed record TestResultsResult
{
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("runId")] public string? RunId { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonPropertyName("total")] public int? Total { get; init; }
    [JsonPropertyName("passed")] public int? Passed { get; init; }
    [JsonPropertyName("failed")] public int? Failed { get; init; }
    [JsonPropertyName("skipped")] public int? Skipped { get; init; }
    [JsonPropertyName("inconclusive")] public int? Inconclusive { get; init; }
    [JsonPropertyName("duration")] public string? Duration { get; init; }
    [JsonPropertyName("failures")] public IReadOnlyList<TestFailureResult>? Failures { get; init; }
}

/// <summary>
/// Project-level tools surviving Plan 10 Task 6's hard cutover: force script recompilation, start a
/// test run and poll its result, read the live console log, and one merged multi-call session
/// (script_editing_session) plus one merged record/replay tool (hades_regression). See
/// EditorPrefabTools' own former doc comment (now gone with that file) for the shared
/// not-attached/busy/timeout/plugin-error contract every one of these inherits from
/// <see cref="EditorProxy"/>.
///
/// project_recompile_scripts and project_run_tests both return as soon as the attached Editor has
/// ACCEPTED the request, not once it has finished: both trigger a domain reload on the Unity side
/// (recompiling scripts; EditMode test runs reload before executing), which can take far longer
/// than a single tool call should block for - see Plugin~'s ProjectCommands.cs for the
/// release-before-trigger ordering that makes this safe.
///
/// project_get_console_log and project_get_test_results are class 4 (live-state reads - no lease at
/// all - "52 Editor tools" plan, Task 5) - see ToolSupport.LiveStateClause (each tool's own
/// description) and Plugin~'s ProjectCommands.cs (ConsoleLogBuffer/TestRunResultStore) for why
/// neither can be answered from disk. project_get_test_results is the poll side of
/// project_run_tests, reconciled by the runId that tool hands back - an unstarted or unknown run
/// answers plainly rather than with an empty result (see TestResultsResult's own doc comment for
/// the exact status vocabulary).
///
/// <para><b>script_editing_session</b> (action='begin'|'end') is the ONLY place in this whole
/// server that calls <see cref="LeaseRegistry.RecordHeld"/>/<see cref="LeaseRegistry.Clear"/> - this
/// is what makes hades_charon_status's leaseHeld reflect a session actually started here (see those
/// two methods' own doc comments for why: without this, LeaseRegistry had a Get/ReconcileAsync
/// reader but no writer for the FIRST hold, so leaseHeld read false even while a lease was genuinely
/// held). Recorded/cleared against the SAME resolved productGuid every other tool in this class
/// sends its command against - never the raw, possibly-null 'project' handle - so a lease belief is
/// never filed under an alias instead of the canonical id LeaseRegistry.Get (via hades_charon_status)
/// actually looks up. Plan 10 Task 6 removed this class's two former standalone tools,
/// BeginScriptEditing and EndScriptEditing (PascalCase, matching the old package's own naming - the
/// "52 Editor tools" plan's own Class-3 listing) - script_editing_session sends the EXACT SAME wire
/// methods (project.begin_script_editing/project.end_script_editing) and performs the EXACT SAME
/// LeaseRegistry.RecordHeld/Clear calls those two used to, so the lease semantics Plan 8 proved
/// (an exception between 'begin' and 'end' leaves the lease held; TTL/disconnect are the nets; 'end'
/// without 'begin' is idempotent and calls Unlock zero times) are unchanged, re-proven under this
/// name in EditorProjectToolsTests.cs.</para>
///
/// <para><b>hades_regression</b> (action='start'|'stop'|'replay') replaces this class's three
/// former standalone tools, hades_regression_record (action='start'|'stop') and
/// hades_regression_replay (a 'calls' array) - sending the EXACT SAME three wire methods
/// (hades.regression_record_start/_stop, hades.regression_replay) those used to. Record's output and
/// replay's input keep agreeing BY CONSTRUCTION: action='stop' returns
/// <see cref="RegressionRecordedCallResult"/> entries - the SAME {method, params, expected} shape
/// <see cref="RegressionCallSpec"/> (action='replay's own 'calls' element type) accepts - so a
/// caller can hand THIS tool's own 'stop' result straight into a later 'replay' call on this SAME
/// tool with no translation step. There is deliberately no dataset store on either side of that
/// hand-off.</para>
/// </summary>
[McpServerToolType]
public sealed class EditorProjectTools(EditorProxy editor, ProjectService projects, LeaseRegistry leases)
{
    [McpServerTool(Name = "project_recompile_scripts", Title = "Recompile Scripts", ReadOnly = false, UseStructuredContent = true)]
    [Description("Forces the attached Unity Editor to recompile scripts. Triggers a domain reload "
               + "once this call returns - the connection will briefly drop and reconnect. Needs a "
               + "live Editor - call hades_charon_status first if unsure.")]
    public async Task<RecompileScriptsResult> ProjectRecompileScripts(
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null)
    {
        var result = await editor.SendCommandAsync(project, "project.recompile_scripts").ConfigureAwait(false);
        return new RecompileScriptsResult
        {
            Requested = result.TryGetProperty("requested", out var v) && v!.Kind == WireKind.Boolean && v.AsBoolean(),
        };
    }

    [McpServerTool(Name = "project_run_tests", Title = "Run Tests", ReadOnly = false, UseStructuredContent = true)]
    [Description("Starts a Unity Test Runner run on the attached Editor and returns immediately "
               + "with a runId and status='started' - it does NOT wait for the run to finish "
               + "(EditMode runs trigger a domain reload, which can take far longer than a single "
               + "tool call should block for). Needs a live Editor - call hades_charon_status "
               + "first if unsure.")]
    public async Task<RunTestsResult> ProjectRunTests(
        [Description("Regex filter matched against full test names - a class or namespace name selects everything beneath it. Omit to run all tests.")] string? filter = null,
        [Description("EditMode, PlayMode, or All (default EditMode)")] string? testMode = null,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null)
    {
        var @params = WireJson.NewObject();
        if (!string.IsNullOrEmpty(filter)) @params.SetProperty("filter", WireJson.String(filter));
        if (!string.IsNullOrEmpty(testMode)) @params.SetProperty("testMode", WireJson.String(testMode));

        var result = await editor.SendCommandAsync(project, "project.run_tests", @params).ConfigureAwait(false);
        return new RunTestsResult
        {
            RunId = EditorComponentTools.Str(result, "runId"),
            Status = EditorComponentTools.Str(result, "status"),
            TestMode = EditorComponentTools.Str(result, "testMode"),
            Filter = result.TryGetProperty("filter", out var f) && f!.Kind == WireKind.String ? f.AsString() : null,
            Error = result.TryGetProperty("error", out var e) && e!.Kind == WireKind.String ? e.AsString() : null,
        };
    }

    // ---------------------------------------------------------------- project_get_console_log

    [McpServerTool(Name = "project_get_console_log", Title = "Get Console Log", ReadOnly = true, UseStructuredContent = true)]
    [Description("Recent entries from the attached Unity Editor's own Console - captured live as "
               + "Unity logs them, since the plugin loaded. Optionally filtered to one severity "
               + "('Error', 'Warning', or 'Log') and capped at 'count' most recent matching "
               + "entries (default 50, max 200). Needs a live Editor - call hades_charon_status "
               + "first if unsure." + ToolSupport.LiveStateClause)]
    public async Task<ConsoleLogResult> ProjectGetConsoleLog(
        [Description("Max entries to return (default 50, max 200)")] int? count = null,
        [Description("Filter to one severity: 'Error', 'Warning', or 'Log'. Omit for every severity.")] string? type = null,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null)
    {
        var @params = WireJson.NewObject();
        if (count is { } c) @params.SetProperty("count", WireJson.Integer(c));
        if (!string.IsNullOrEmpty(type)) @params.SetProperty("type", WireJson.String(type));

        var result = await editor.SendCommandAsync(project, "project.get_console_log", @params).ConfigureAwait(false);

        var entries = new List<ConsoleLogEntryResult>();
        if (result.TryGetProperty("entries", out var entriesJson) && entriesJson!.Kind == WireKind.Array)
        {
            foreach (var entry in entriesJson.Items)
            {
                entries.Add(new ConsoleLogEntryResult
                {
                    Type = EditorComponentTools.Str(entry, "type"),
                    Message = EditorComponentTools.Str(entry, "message"),
                    StackTrace = EditorComponentTools.Str(entry, "stackTrace"),
                });
            }
        }

        return new ConsoleLogResult
        {
            Entries = entries,
            Count = (int)EditorComponentTools.Int(result, "count"),
            TotalBuffered = (int)EditorComponentTools.Int(result, "totalBuffered"),
        };
    }

    // ---------------------------------------------------------------- project_get_test_results

    [McpServerTool(Name = "project_get_test_results", Title = "Get Test Results", ReadOnly = true, UseStructuredContent = true)]
    [Description("Polls for the outcome of a project_run_tests run, reconciled by 'runId': "
               + "'running' while the run (and any domain reload EditMode triggers) is still in "
               + "progress, 'complete' with total/passed/failed/skipped/inconclusive counts and "
               + "failure details once finished, 'none' if no run has been started this session, "
               + "or 'unknown' if 'runId' does not match the most recently started run. Omit "
               + "'runId' to poll whichever run started most recently. Needs a live Editor - call "
               + "hades_charon_status first if unsure." + ToolSupport.LiveStateClause)]
    public async Task<TestResultsResult> ProjectGetTestResults(
        [Description("The runId project_run_tests returned. Omit to poll the most recently started run.")] string? runId = null,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null)
    {
        var @params = WireJson.NewObject();
        if (!string.IsNullOrEmpty(runId)) @params.SetProperty("runId", WireJson.String(runId));

        var result = await editor.SendCommandAsync(project, "project.get_test_results", @params).ConfigureAwait(false);

        List<TestFailureResult>? failures = null;
        if (result.TryGetProperty("failures", out var failuresJson) && failuresJson!.Kind == WireKind.Array)
        {
            failures = new List<TestFailureResult>();
            foreach (var failure in failuresJson.Items)
            {
                failures.Add(new TestFailureResult
                {
                    Name = failure.TryGetProperty("name", out var n) && n!.Kind == WireKind.String ? n.AsString() : null,
                    Message = failure.TryGetProperty("message", out var m) && m!.Kind == WireKind.String ? m.AsString() : null,
                });
            }
        }

        return new TestResultsResult
        {
            Status = EditorComponentTools.Str(result, "status"),
            RunId = result.TryGetProperty("runId", out var r) && r!.Kind == WireKind.String ? r.AsString() : null,
            Note = result.TryGetProperty("note", out var note) && note!.Kind == WireKind.String ? note.AsString() : null,
            Total = TryIntProperty(result, "total"),
            Passed = TryIntProperty(result, "passed"),
            Failed = TryIntProperty(result, "failed"),
            Skipped = TryIntProperty(result, "skipped"),
            Inconclusive = TryIntProperty(result, "inconclusive"),
            Duration = result.TryGetProperty("duration", out var d) && d!.Kind == WireKind.String ? d.AsString() : null,
            Failures = failures,
        };
    }

    static int? TryIntProperty(WireJson value, string key) =>
        value.TryGetProperty(key, out var v) && v!.Kind == WireKind.Integer ? (int)v.AsInteger() : null;

    // ---------------------------------------------------------------- script_editing_session

    [McpServerTool(Name = "script_editing_session", Title = "Script Editing Session", ReadOnly = false, UseStructuredContent = true)]
    [Description("Begins or ends a script-editing session that holds Unity's reload lock, so the "
               + "Editor does not recompile out from under a multi-file change in progress. "
               + "action='begin' acquires the lock - call action='end' when done, or Unity resumes "
               + "recompiling on its own once the lease's TTL expires (see expiresAtUtc); calling "
               + "'begin' again before 'end' renews it. action='end' releases the lock (if held) "
               + "and triggers recompilation - release then trigger, never fighting its own lock - "
               + "and is safe to call even if 'begin' was never called or already expired (released "
               + "comes back false; recompilation is still requested). Needs a live Editor - call "
               + "hades_charon_status first if unsure.")]
    public async Task<ScriptEditingSessionResult> ScriptEditingSession(
        [Description("'begin' or 'end'")] string action,
        [Description("begin only: how long the lease may be held before it expires if never renewed, in seconds. Omit for the plugin's default (30s).")] double? ttlSeconds = null,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null)
    {
        EditorComponentTools.RequireNonBlank(action, nameof(action), "script_editing_session");

        switch (action.Trim().ToLowerInvariant())
        {
            case "begin":
            {
                var @params = WireJson.NewObject();
                if (ttlSeconds is { } t) @params.SetProperty("ttlSeconds", WireJson.Float(t));

                var productGuid = ToolSupport.ResolveProject(projects, project);
                var result = await editor.SendCommandAsync(productGuid, "project.begin_script_editing", @params).ConfigureAwait(false);

                var leaseId = EditorComponentTools.Str(result, "leaseId");
                var expiresAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(EditorComponentTools.Int(result, "expiresAtUtcMs"));

                // The plugin's own answer, read back off its response - never the requested
                // ttlSeconds above, which the plugin may not have honoured verbatim (e.g. a
                // renewal keeps the ORIGINAL lease's TTL - see ReloadGate.Acquire) - is what seeds
                // LeaseRegistry's very first belief that a lease is held. See this class's own doc
                // comment: this action and 'end' below are the only writers LeaseRegistry has ever
                // had.
                leases.RecordHeld(productGuid, leaseId, expiresAtUtc);

                return new ScriptEditingSessionResult { LeaseId = leaseId, ExpiresAtUtc = expiresAtUtc };
            }

            case "end":
            {
                var productGuid = ToolSupport.ResolveProject(projects, project);
                var result = await editor.SendCommandAsync(productGuid, "project.end_script_editing").ConfigureAwait(false);

                // Cleared unconditionally - matching this action's own idempotent contract (safe to
                // call with no matching 'begin', or after the plugin's TTL already released it):
                // whatever this app believed about a held lease for this project stops being
                // trustworthy the moment 'end' has been requested, regardless of the plugin's own
                // 'released' value.
                leases.Clear(productGuid);

                return new ScriptEditingSessionResult
                {
                    Released = result.TryGetProperty("released", out var r) && r!.Kind == WireKind.Boolean && r.AsBoolean(),
                    Requested = result.TryGetProperty("requested", out var q) && q!.Kind == WireKind.Boolean && q.AsBoolean(),
                };
            }

            default:
                throw new McpException($"script_editing_session's 'action' must be 'begin' or 'end', got '{action}'.");
        }
    }

    // ---------------------------------------------------------------- hades_regression

    [McpServerTool(Name = "hades_regression", Title = "Regression Record/Replay", ReadOnly = false, UseStructuredContent = true)]
    [Description("Records or replays tool calls made against the attached Unity Editor. "
               + "action='start' begins an empty recording; every subsequent tool call the attached "
               + "Editor executes is captured until action='stop', which returns the captured "
               + "entries as 'calls': {method, params, expected} - EXACTLY the shape action='replay' "
               + "accepts in its own 'calls' parameter, so this tool's own 'stop' result can be "
               + "passed straight into a later 'replay' call with no translation step. Stopping "
               + "with nothing recorded (or never started) is a no-op, not an error. "
               + "action='replay' replays a batch of calls - each identified by its Plugin~ wire "
               + "method name (e.g. 'scene.create_gameobject', 'component.set_property'), not an "
               + "MCP tool name - and, where an 'expected' value is supplied, compares the actual "
               + "result against it; a per-call failure does not stop the rest of the batch. Needs "
               + "a live Editor - call hades_charon_status first if unsure.")]
    public async Task<HadesRegressionResult> HadesRegression(
        [Description("'start', 'stop', or 'replay'")] string action,
        [Description("replay only: calls to replay, each { method, params, expected? }")] IReadOnlyList<RegressionCallSpec>? calls = null,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null)
    {
        EditorComponentTools.RequireNonBlank(action, nameof(action), "hades_regression");

        switch (action.Trim().ToLowerInvariant())
        {
            case "start":
            {
                var result = await editor.SendCommandAsync(project, "hades.regression_record_start").ConfigureAwait(false);
                return new HadesRegressionResult
                {
                    Recording = result.TryGetProperty("recording", out var r) && r!.Kind == WireKind.Boolean && r.AsBoolean(),
                };
            }

            case "stop":
            {
                var result = await editor.SendCommandAsync(project, "hades.regression_record_stop").ConfigureAwait(false);

                var stoppedCalls = new List<RegressionRecordedCallResult>();
                if (result.TryGetProperty("calls", out var callsJson) && callsJson!.Kind == WireKind.Array)
                {
                    foreach (var entry in callsJson.Items)
                    {
                        stoppedCalls.Add(new RegressionRecordedCallResult
                        {
                            Method = EditorComponentTools.Str(entry, "method"),
                            Params = entry.TryGetProperty("params", out var p) ? WireJsonBridge.ToClr(p!) : null,
                            Expected = entry.TryGetProperty("expected", out var e) ? WireJsonBridge.ToClr(e!) : null,
                        });
                    }
                }

                return new HadesRegressionResult { Calls = stoppedCalls, Count = (int)EditorComponentTools.Int(result, "count") };
            }

            case "replay":
            {
                if (calls is null || calls.Count == 0)
                    throw new McpException("hades_regression's 'replay' action needs a non-empty 'calls' array.");

                var callsJson = WireJson.NewArray();
                foreach (var call in calls)
                {
                    EditorComponentTools.RequireNonBlank(call.Method, nameof(call.Method), "hades_regression");

                    var callJson = WireJson.NewObject().SetProperty("method", WireJson.String(call.Method));
                    if (call.Params is { Count: > 0 })
                    {
                        var paramsJson = WireJson.NewObject();
                        foreach (var (key, value) in call.Params) paramsJson.SetProperty(key, WireJsonBridge.ToWire(value));
                        callJson.SetProperty("params", paramsJson);
                    }
                    if (call.Expected is { } expected) callJson.SetProperty("expected", WireJsonBridge.ToWire(expected));

                    callsJson.Add(callJson);
                }

                var @params = WireJson.NewObject().SetProperty("calls", callsJson);
                var result = await editor.SendCommandAsync(project, "hades.regression_replay", @params).ConfigureAwait(false);

                var results = new List<RegressionReplayEntryResult>();
                if (result.TryGetProperty("results", out var resultsJson) && resultsJson!.Kind == WireKind.Array)
                {
                    foreach (var entry in resultsJson.Items)
                    {
                        results.Add(new RegressionReplayEntryResult
                        {
                            Method = entry.TryGetProperty("method", out var m) && m!.Kind == WireKind.String ? m.AsString() : null,
                            Passed = entry.TryGetProperty("passed", out var p) && p!.Kind == WireKind.Boolean && p.AsBoolean(),
                            Actual = entry.TryGetProperty("actual", out var a) ? WireJsonBridge.ToClr(a!) : null,
                            Error = entry.TryGetProperty("error", out var er) && er!.Kind == WireKind.String ? er.AsString() : null,
                        });
                    }
                }

                return new HadesRegressionResult
                {
                    Results = results,
                    Total = (int)EditorComponentTools.Int(result, "total"),
                    Passed = (int)EditorComponentTools.Int(result, "passed"),
                    Failed = (int)EditorComponentTools.Int(result, "failed"),
                };
            }

            default:
                throw new McpException($"hades_regression's 'action' must be 'start', 'stop', or 'replay', got '{action}'.");
        }
    }
}
