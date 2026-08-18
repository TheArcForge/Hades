// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Hades.Tools
{
    /// <summary>
    /// Class-2 (multi-tick, one call, lease bounded by the call - see the "52 Editor tools" plan's
    /// operation-class table) project-level operations: open a scene, force script recompilation,
    /// start a test run, and replay a batch of tool calls. See PrefabCommands' own doc comment for
    /// the shared <see cref="LeaseScope.Run"/> wrapper every handler here uses, and for why an
    /// exception here still leaves gate.IsHeld false (the opposite of class 3's BeginScriptEditing
    /// semantics, where an exception intentionally leaves the lease owned).
    ///
    /// This file ALSO carries class 3 (multi-call session - "52 Editor tools" plan Task 4):
    /// BeginScriptEditing/EndScriptEditing (see their own doc comments, just below
    /// RecompileScripts) and hades.regression_record's Start/Stop pair (just before this class's
    /// closing brace, beside RegressionReplay). Both are deliberately NOT run through
    /// <see cref="LeaseScope.Run"/> - that helper's whole purpose is "acquire, do bounded work,
    /// release before returning", which is exactly backwards for a lease meant to survive this
    /// handler's own return.
    ///
    /// project_refresh_assets (the MCP tool) has NO handler here: per the plan, it is the tool-face
    /// of the "assets.refresh" command CommandTable already registers (built in plan 8) - see
    /// CommandTable.HandleAssetsRefresh, now itself wrapped in LeaseScope.Run for the identical
    /// reason every handler in this file is (AssetDatabase.Refresh can trigger asset import/script
    /// compilation - real reload risk bounded by that one call).
    ///
    /// project.recompile_scripts is the one handler in this whole plan that WANTS a reload once it
    /// returns, so it deliberately does NOT hold the lease across the trigger: LeaseScope.Run's own
    /// acquire/work/release covers only this handler's bounded prep, and <see cref="RefreshAssets"/>
    /// then <see cref="TriggerRecompile"/> (AssetDatabase.Refresh() then
    /// CompilationPipeline.RequestScriptCompilation()) both run strictly AFTER that lease is
    /// already released - see ProjectCommandsTests for the ordering proof. Holding the lease across
    /// either call would make this handler fight its own lock: a held ReloadGate lease is exactly
    /// what blocks Unity's domain reload (and therefore recompilation) from happening at all.
    /// RefreshAssets runs first because TriggerRecompile alone only recompiles scripts Unity
    /// already knows about - a brand-new .cs file has no .meta yet and is not part of that set
    /// until Refresh imports it (mutation-tool-defects.md #1; see RefreshAssets' own doc comment).
    ///
    /// project.run_tests never links UnityEditor.TestTools.TestRunner.Api at compile time: Hades'
    /// own asmdef ships with an EMPTY "references" array (see PluginInstallerTests'
    /// Install_ZeroDependencyGuard test), and every consuming Unity project must keep compiling
    /// even without the Test Framework package installed. <see cref="TestRunnerBridge"/> resolves
    /// the Test Runner API entirely by reflection, exactly like ComponentTypes.Find already
    /// resolves MonoBehaviour types across assemblies with no compile-time reference to any of
    /// them - and degrades to an actionable error ("install the Test Framework package") when the
    /// package is absent, rather than a TypeLoadException. Same "wants a reload" reasoning as
    /// recompile_scripts applies here too: EditMode runs trigger their own domain reload, so
    /// starting the run happens after this handler's own lease is released, and the call returns
    /// immediately with a runId rather than waiting for the run to finish (which can take far
    /// longer than a single tool call should block for - see this class's own RunTests for the
    /// full reasoning).
    ///
    /// This file ALSO carries two of Plan 9 Task 5's class 4 (live-state reads - no lease at all,
    /// not even LeaseScope.Run's bounded acquire/release) handlers: project.get_console_log (with
    /// its own support class <see cref="ConsoleLogBuffer"/>, just below TestRunnerBridge) and
    /// project.get_test_results (with <see cref="TestRunResultStore"/>, the poll side of
    /// project.run_tests - see that class's own doc comment for how it reconciles a runId across
    /// the domain reload an EditMode run triggers). inspector.inspect, this plan's third class-4
    /// tool, lives in InspectorCommands.cs beside its own class-1 sibling inspector.select instead.
    /// </summary>
    public static class ProjectCommands
    {
        // ---------------------------------------------------------------- scene.open

        internal static JsonValue OpenScene(ReloadGate gate, JsonValue @params) =>
            LeaseScope.Run(gate, "scene.open", () => DoOpenScene(@params));

        /// <summary>Lease-free core - see AssetCommands.DoImportAsset's own doc comment ("Plan 10
        /// Task 5") for the identical split. Added so <see cref="SceneManageCommands"/>' own "open"
        /// op can call this directly inside its ONE whole-batch <see cref="LeaseScope.Run"/>, rather
        /// than nesting a second lease acquisition inside the batch's own.</summary>
        internal static JsonValue DoOpenScene(JsonValue @params)
        {
            var path = JsonParams.RequireString(@params, "path", "scene.open");
            var additive = JsonParams.OptionalBool(@params, "additive", false);

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
            {
                throw new ArgumentException(
                    "Scene not found at '" + path + "'. Use search_by_name to find the correct project-relative path.");
            }

            // Single (not the interactive "prompt to save" the Editor's own File > Open Scene
            // menu command shows): the scripting API never prompts - it simply discards
            // unsaved changes in the scene(s) being replaced - so a caller that cares about
            // those changes must scene_save first. Additive when requested, matching
            // EditorSceneManager's own two supported open modes.
            var mode = additive ? OpenSceneMode.Additive : OpenSceneMode.Single;
            var scene = EditorSceneManager.OpenScene(path, mode);

            if (!scene.IsValid())
                throw new ArgumentException("Failed to open scene at '" + path + "'.");

            return JsonValue.NewObject()
                .SetProperty("opened", JsonValue.String(path))
                .SetProperty("mode", JsonValue.String(mode.ToString()))
                .SetProperty("isLoaded", JsonValue.Bool(scene.isLoaded));
        }

        // ---------------------------------------------------------------- project.recompile_scripts

        /// <summary>Seam over CompilationPipeline.RequestScriptCompilation() - swapped out in
        /// ProjectCommandsTests to prove the release-then-trigger ORDER without ever touching real
        /// compilation. Public (not internal) for the same reason ReloadGate/IEditorLockApi are:
        /// there is no InternalsVisibleTo between this assembly and Hades.Tests.Editor, and every
        /// test in this plugin reaches its target through a PUBLIC seam rather than reflection or
        /// an assembly-visibility escape hatch - this is that seam for recompilation. Static,
        /// process-wide state: a test that reassigns this MUST restore
        /// <see cref="RealTriggerRecompile"/> in its own TearDown, same hygiene requirement as
        /// ReloadGate's SessionState key or this class's own <see cref="StartTestRun"/> seam
        /// below.</summary>
        public static Action TriggerRecompile = RealTriggerRecompile;

        static void RealTriggerRecompile() => UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();

        /// <summary>Seam over AssetDatabase.Refresh() - same swappable-static-field convention as
        /// <see cref="TriggerRecompile"/> and for the same testing reason (a test proving call
        /// ORDER must never touch the real asset database; restore in TearDown).
        ///
        /// This is mutation-tool-defects.md #1's fix. <see cref="TriggerRecompile"/>
        /// (CompilationPipeline.RequestScriptCompilation) only asks Unity to recompile scripts it
        /// ALREADY knows about - a brand-new .cs file has no .meta yet, so it is not part of that
        /// set at all until something imports it. Interactively, opening/focusing the Editor does
        /// that automatically; CommandTable.HandleAssetsRefresh's own doc comment already explains
        /// why a BACKGROUND Editor (batchmode above all - it never gets the focus event) does not:
        /// "without this nothing can provoke a recompile". RecompileScripts and EndScriptEditing
        /// both now call this FIRST, so a new file is imported - and therefore actually part of the
        /// compiled set - before the explicit recompile request that follows it. Called
        /// unconditionally, every time, exactly like <see cref="TriggerRecompile"/> already was:
        /// see EndScriptEditing's own doc comment for why "always, not only when a new file is
        /// detected" is the deliberate choice here.</summary>
        public static Action RefreshAssets = RealRefreshAssets;

        static void RealRefreshAssets() => UnityEditor.AssetDatabase.Refresh();

        internal static JsonValue RecompileScripts(ReloadGate gate, JsonValue @params)
        {
            var result = LeaseScope.Run(gate, "project.recompile_scripts", () => JsonValue.NewObject());

            // Deliberately AFTER LeaseScope.Run has already returned (and therefore already
            // released) - see this file's own class doc comment. RefreshAssets runs BEFORE
            // TriggerRecompile so a brand-new .cs file (no .meta yet) is actually imported, and
            // therefore part of the compiled set, before compilation of that set is requested -
            // see RefreshAssets' own doc comment (mutation-tool-defects.md #1).
            RefreshAssets();
            TriggerRecompile();

            return result.SetProperty("requested", JsonValue.Bool(true));
        }

        // ---------------------------------------------------------------- project.begin_script_editing / project.end_script_editing

        /// <summary>The single, well-known lease id every BeginScriptEditing session acquires -
        /// UNLIKE <see cref="LeaseScope"/>'s fresh GUID per call (each class-2 call is its own
        /// independent, complete operation), every script-editing session IS the same conceptual
        /// hold for as long as the gate keeps it, so a STABLE id is what makes calling
        /// BeginScriptEditing again, before EndScriptEditing, read to <see cref="ReloadGate.Acquire"/>
        /// as a RENEWAL of this same lease (re-acquiring your own lease counts as real activity -
        /// see that method's own doc comment) rather than a rejected, different-lease acquire. Only
        /// one Unity Editor connection exists per project, so there is no multi-tenant concern a
        /// caller-supplied id would need to guard against here, unlike class 2's per-call
        /// uniqueness requirement.</summary>
        public const string ScriptEditingLeaseId = "hades-script-editing";

        /// <summary>Acquires the gate under <see cref="ScriptEditingLeaseId"/> and, deliberately
        /// UNLIKE every class-2 handler in this plan, returns WITHOUT releasing it - the whole
        /// point of BeginScriptEditing is to leave the lease held across this call's own return,
        /// for EndScriptEditing (or the TTL, if nobody ever calls EndScriptEditing) to release
        /// later. Reusing <see cref="LeaseScope.Run"/> here would be a bug, not a convenience: that
        /// helper's finally releases before returning, unconditionally.
        ///
        /// Returns the lease's ACTUAL id/expiry read back off <see cref="ReloadGate.CurrentLease"/>
        /// after acquiring - never an echo of the request - because a renewal (this same session's
        /// second-or-later Begin call) keeps the TTL the lease was ORIGINALLY created with
        /// (<paramref name="@params"/>'s own ttlSeconds is ignored on that path - see
        /// ReloadGate.Acquire's own doc comment), so echoing the request would lie about what is
        /// actually still in effect.</summary>
        internal static JsonValue BeginScriptEditing(ReloadGate gate, JsonValue @params)
        {
            var ttl = OptionalTtlSeconds(@params);
            var acquired = gate.Acquire(ScriptEditingLeaseId, ttl);
            if (!acquired)
            {
                var holder = gate.CurrentLeaseId;
                throw new InvalidOperationException(
                    "BeginScriptEditing needs Unity's reload lock, but it is currently held by lease '" + holder
                    + "'. Call script_editing_session with action 'end', or wait for it to finish, then retry.");
            }

            var lease = gate.CurrentLease;
            return JsonValue.NewObject()
                .SetProperty("leaseId", JsonValue.String(lease.Id))
                .SetProperty("expiresAtUtcMs", JsonValue.Integer(ToUnixTimeMs(lease.ExpiresAtUtc)));
        }

        /// <summary>Releases <see cref="ScriptEditingLeaseId"/> if held, THEN refreshes the asset
        /// database and triggers recompilation via the SAME <see cref="RefreshAssets"/>/
        /// <see cref="TriggerRecompile"/> seams RecompileScripts uses - release-then-refresh-then-
        /// trigger, never fight its own lock, for the identical reason RecompileScripts itself does
        /// not hold the lease across either call (see this file's own class doc comment). Always
        /// runs both, even when nothing was actually held (the old package's EndScriptEditing always
        /// asked Unity to recompile too) - what changed is HOW the release happens:
        /// <see cref="ReloadGate.Release"/> already calls <see cref="IEditorLockApi.Unlock"/> ZERO
        /// times when nothing is held for this id, rather than the old implementation's
        /// unconditional force-unlock that could drive Unity's native counter to -1 (see
        /// DomainReloadTools.cs).
        ///
        /// RefreshAssets BEFORE TriggerRecompile is mutation-tool-defects.md #1's fix: a brand-new
        /// .cs file written during this session has no .meta yet, so
        /// CompilationPipeline.RequestScriptCompilation() alone had genuinely nothing new to
        /// compile - Unity never imported the file at all, which is why this call used to report
        /// success while silently leaving the new type out of Assembly-CSharp.dll. Refresh runs
        /// UNCONDITIONALLY, not only when this session is known to have written a new file: this
        /// plugin has no visibility into the agent's own out-of-band file writes between Begin and
        /// End (Begin/End are pure lease bookkeeping around a session Hades never sees the inside
        /// of), so any narrower trigger would mean re-deriving "did a new file appear" by walking
        /// Assets itself - more moving parts than the failure mode (a silent no-op recompile)
        /// tolerates. Matching what a human pressing Cmd+R does is simpler and cannot under-fire.
        /// The cost is acceptable because this handler is a class-3 SESSION BOUNDARY (the agent
        /// must come back to call it at all), never a hot per-edit path the way a class-1 mutation
        /// is.
        ///
        /// 'released' reports whether THIS call actually released a lease of ours - computed from
        /// whether <see cref="ScriptEditingLeaseId"/> was the current holder BEFORE releasing,
        /// since <see cref="ReloadGate.Release"/>'s own return value is true both when it just
        /// unlocked and when there was nothing to unlock (idempotent no-op) - those two cases are
        /// not distinguishable from Release's return alone.</summary>
        internal static JsonValue EndScriptEditing(ReloadGate gate, JsonValue @params)
        {
            var wasHeldByUs = gate.CurrentLeaseId == ScriptEditingLeaseId;
            var released = gate.Release(ScriptEditingLeaseId);

            // Deliberately AFTER release, and deliberately Refresh BEFORE TriggerRecompile - see
            // this method's own doc comment (mutation-tool-defects.md #1).
            RefreshAssets();
            TriggerRecompile();

            return JsonValue.NewObject()
                .SetProperty("released", JsonValue.Bool(wasHeldByUs && released))
                .SetProperty("requested", JsonValue.Bool(true));
        }

        /// <summary>Parses the optional 'ttlSeconds' parameter shared by BeginScriptEditing and
        /// lease.acquire (CommandTable's own OptionalTtl) - kept as a small, separate copy here
        /// rather than widening CommandTable's private helper's visibility, so this file's own
        /// change surface stays limited to itself.</summary>
        static TimeSpan? OptionalTtlSeconds(JsonValue @params)
        {
            var value = JsonParams.OptionalValue(@params, "ttlSeconds");
            if (value == null || value.Kind == JsonValueKind.Null) return null;
            if (value.Kind != JsonValueKind.Integer && value.Kind != JsonValueKind.Float)
                throw new ArgumentException("'ttlSeconds' must be a number.");
            return TimeSpan.FromSeconds(value.AsDouble());
        }

        static long ToUnixTimeMs(DateTime utc) =>
            new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        // ---------------------------------------------------------------- project.run_tests

        /// <summary>Seam over <see cref="TestRunnerBridge.TryStart"/> - public for the same
        /// InternalsVisibleTo reason <see cref="TriggerRecompile"/> is, and swappable for the same
        /// testing reason: tests must never provoke a REAL, recursive Test Runner invocation from
        /// inside an already-running EditMode test, so ProjectCommandsTests substitutes a fake
        /// here rather than ever letting the real bridge run. See TriggerRecompile's own doc
        /// comment for the hygiene requirement (restore in TearDown) that follows from
        /// that.</summary>
        public static Func<string, string, string, (bool Started, string Error)> StartTestRun = TestRunnerBridge.TryStart;

        internal static JsonValue RunTests(ReloadGate gate, JsonValue @params)
        {
            var filter = JsonParams.OptionalString(@params, "filter");
            var testMode = JsonParams.OptionalString(@params, "testMode") ?? "EditMode";
            var runId = Guid.NewGuid().ToString("N");

            // F12 fix: this used to call an explicit SaveDirtyScenesWithPath() here, UNCONDITIONALLY
            // for every testMode (not just PlayMode) - ported all the way back from the old
            // package's own RunTests, whose comment claimed "tests may trigger domain reload which
            // discards unsaved scene changes". That premise does not hold: Unity backs up in-memory
            // scene state across both a domain reload and a PlayMode enter/exit and restores it
            // afterward, without ever needing a disk write to protect it - the same guarantee that
            // lets you edit a scene and recompile scripts without losing scene work. So the save was
            // never protecting anything; it was only an unrequested, silent write to a tracked file
            // every time an agent asked to run tests. Fixed by removing the call outright rather
            // than disclosing it more loudly - see AssertRunTestsNeverSavesDirtyScene
            // (ProjectCommandsTests) for the pin, and EditorProjectTools' project_run_tests
            // description for the caller-facing side of this same fix.
            var result = LeaseScope.Run(gate, "project.run_tests", () => JsonValue.NewObject());

            // As with project.recompile_scripts, starting the run happens AFTER this handler's own
            // lease is released - EditMode runs trigger a domain reload, which this handler's own
            // held lease would otherwise block. StartTestRun returns immediately: it only asks
            // Unity's Test Runner to begin: it never waits for RunFinished (there is no ICallbacks
            // registration anywhere in this bridge) - project.get_test_results (Plan 9 Task 5,
            // class 4) is what polls for completion, mirroring the old package's own split between
            // "start" and "poll a results file".
            var (started, startError) = StartTestRun(runId, testMode, filter);

            // Only when StartTestRun actually confirms the run is beginning - marking "started"
            // for a run that never started (e.g. the Test Framework package is missing) would make
            // project_get_test_results report "running" forever for a runId that will never
            // resolve, since no results file is ever going to appear for it. See
            // TestRunResultStore's own doc comment for why this call is safe to make here, at the
            // RunTests level, rather than inside TestRunnerBridge.TryStart itself: Execute() only
            // QUEUES the run (EditMode/PlayMode both span multiple later Editor ticks), so it
            // cannot have already completed - and therefore cannot have already rewritten the
            // results file - by the time this line runs.
            if (started) TestRunResultStore.MarkStarted(runId);

            result.SetProperty("runId", JsonValue.String(runId));
            result.SetProperty("status", JsonValue.String(started ? "started" : "failed"));
            result.SetProperty("testMode", JsonValue.String(testMode));
            result.SetProperty("filter", filter != null ? JsonValue.String(filter) : JsonValue.Null);
            if (startError != null) result.SetProperty("error", JsonValue.String(startError));
            return result;
        }

        // ---------------------------------------------------------------- hades.regression_replay

        /// <summary>Replays a batch of (method, params, optional expected) entries supplied
        /// DIRECTLY in this call's own params, dispatching each straight through
        /// CommandTable.Dispatch. Takes the calls to replay as this call's own input rather than a
        /// 'datasetId' referencing a server-side store: hades.regression_record_stop (below) hands
        /// back exactly this same {method, params, expected} shape rather than persisting a
        /// dataset behind an id, so there is nothing for a datasetId to look up on this side
        /// either - see RegressionRecordStop's own doc comment for "record's storage and replay's
        /// input agree".
        ///
        /// Deliberately acquires NO lease of its own: this handler's only direct work is looping
        /// and comparing JSON, never a Unity API call. Every entry is dispatched back through
        /// CommandTable.Dispatch(gate, ...) - the SAME gate this handler itself received - so a
        /// replayed class-2 call acquires and releases its OWN lease exactly as it would if invoked
        /// directly over the wire, and a replayed class-1 call touches no lease at all. Wrapping
        /// the whole loop in a SECOND, outer lease would self-conflict: ReloadGate rejects a second
        /// id while a different one is already held (see ReloadGate.Acquire), so a class-2 entry
        /// replayed mid-loop would find the gate busy with this handler's own outer lease and fail
        /// every time - so this handler simply never takes one, and "no lease survives this call"
        /// holds trivially, inherited entirely from each nested handler's own correctness (proven
        /// independently, per handler, throughout this plan).
        ///
        /// A per-entry exception (bad method, nested tool's own thrown error) is caught and
        /// recorded as that entry's own failure - the same partial-failure shape as
        /// scene.setup/component.set_properties - rather than aborting the whole replay.</summary>
        internal static JsonValue RegressionReplay(ReloadGate gate, JsonValue @params)
        {
            var calls = JsonParams.OptionalValue(@params, "calls");
            if (calls == null || calls.Kind != JsonValueKind.Array || calls.Items.Count == 0)
                throw new ArgumentException("hades.regression_replay requires a non-empty 'calls' array parameter.");

            var results = JsonValue.NewArray();
            var passed = 0;
            var failed = 0;

            foreach (var call in calls.Items)
            {
                var method = call != null ? JsonParams.OptionalString(call, "method") : null;
                if (string.IsNullOrEmpty(method))
                {
                    failed++;
                    results.Add(ReplayEntry(null, false, null, "Each entry in 'calls' requires a non-empty 'method'."));
                    continue;
                }

                try
                {
                    var entryParams = call.TryGetProperty("params", out var p) ? p : null;
                    var expected = call.TryGetProperty("expected", out var e) ? e : null;

                    var request = new JsonRpcRequest { Id = JsonValue.Integer(1), Method = method, Params = entryParams };
                    var actual = CommandTable.Dispatch(gate, request);

                    var isMatch = expected == null || JsonValueEquals(actual, expected);
                    if (isMatch) passed++; else failed++;

                    results.Add(ReplayEntry(method, isMatch, actual, isMatch ? null : "Result did not match the recorded 'expected' value."));
                }
                catch (Exception ex)
                {
                    failed++;
                    results.Add(ReplayEntry(method, false, null, ex.Message));
                }
            }

            return JsonValue.NewObject()
                .SetProperty("results", results)
                .SetProperty("total", JsonValue.Integer(passed + failed))
                .SetProperty("passed", JsonValue.Integer(passed))
                .SetProperty("failed", JsonValue.Integer(failed));
        }

        static JsonValue ReplayEntry(string method, bool didPass, JsonValue actual, string error)
        {
            var entry = JsonValue.NewObject()
                .SetProperty("method", method != null ? JsonValue.String(method) : JsonValue.Null)
                .SetProperty("passed", JsonValue.Bool(didPass));
            if (actual != null) entry.SetProperty("actual", actual);
            if (error != null) entry.SetProperty("error", JsonValue.String(error));
            return entry;
        }

        /// <summary>Structural JSON equality - Kind must match, then value-by-value (Array in
        /// order; Object by key, order-independent). Floats compare with a small epsilon rather
        /// than bitwise, since a replayed value round-tripping through float math should not fail
        /// a byte-exact comparison it was never meant to satisfy.</summary>
        internal static bool JsonValueEquals(JsonValue a, JsonValue b)
        {
            if (a == null || b == null) return a == b;
            if (a.Kind != b.Kind) return false;

            switch (a.Kind)
            {
                case JsonValueKind.Null: return true;
                case JsonValueKind.Boolean: return a.AsBoolean() == b.AsBoolean();
                case JsonValueKind.Integer: return a.AsInteger() == b.AsInteger();
                case JsonValueKind.Float: return Math.Abs(a.AsDouble() - b.AsDouble()) < 0.0001;
                case JsonValueKind.String: return a.AsString() == b.AsString();
                case JsonValueKind.Array:
                    if (a.Items.Count != b.Items.Count) return false;
                    for (var i = 0; i < a.Items.Count; i++)
                        if (!JsonValueEquals(a.Items[i], b.Items[i])) return false;
                    return true;
                case JsonValueKind.Object:
                    if (a.Members.Count != b.Members.Count) return false;
                    foreach (var member in a.Members)
                    {
                        if (!b.TryGetProperty(member.Key, out var bValue)) return false;
                        if (!JsonValueEquals(member.Value, bValue)) return false;
                    }
                    return true;
                default: return false;
            }
        }

        // ---------------------------------------------------------------- hades.regression_record

        /// <summary>How long an open recording session may sit with no captured activity and no
        /// explicit stop before it is treated as abandoned and silently discarded - class 3's
        /// "follows the same session shape" (see the "52 Editor tools" plan's Task 4) applied to a
        /// MUCH lower-risk resource than <see cref="ReloadGate"/>'s reload lock: an abandoned
        /// recording only wastes memory (a growing list nothing will ever read); it never blocks
        /// Unity's own reload pipeline the way a leaked ReloadGate lease would. That difference in
        /// BLAST RADIUS is exactly why this is checked LAZILY (only the next time Start/Stop/a
        /// capture runs - see <see cref="IsRecordingActive"/>) rather than via a background watchdog
        /// Timer the way ReloadGate's own TTL is: nothing here needs to fire proactively while the
        /// project sits idle, because nothing here is stopping anything else from happening in the
        /// meantime. Deliberately NOT the same value as <see cref="ReloadGate.DefaultTtl"/>: a
        /// recording session is meant to span a whole batch of ordinary tool calls, which can
        /// easily take longer than the 30s a reload lock should ever reasonably be held for.</summary>
        static readonly TimeSpan RecordingTtl = TimeSpan.FromMinutes(10);

        /// <summary>Clock seam, same swappable-static-field convention as
        /// <see cref="TriggerRecompile"/>/<see cref="StartTestRun"/> above - tests inject a fake so
        /// TTL expiry is provable without a real 10-minute wait. MUST be restored in the test's own
        /// TearDown, same hygiene rule as those two seams.</summary>
        public static Func<DateTime> RecordingClock = () => DateTime.UtcNow;

        static List<JsonValue> _recordingCalls;
        static DateTime _recordingExpiresAtUtc;

        /// <summary>Starts an empty recording session. Deliberately holds NO <see cref="ReloadGate"/>
        /// lease of any kind - unlike BeginScriptEditing, recording has nothing to do with Unity's
        /// reload lock, and sharing the gate would be actively wrong: LeaseScope.Run-based class-2
        /// handlers (prefab_create, asset_import, ...) are exactly the "normal tool usage" a
        /// recording session exists to capture, and a recording session that held the gate would
        /// make every one of them fail with a busy error for as long as recording stayed
        /// open.</summary>
        internal static JsonValue RegressionRecordStart(ReloadGate gate, JsonValue @params)
        {
            if (IsRecordingActive())
            {
                throw new InvalidOperationException(
                    "A regression recording session is already active. Call hades_regression_record "
                    + "with action 'stop' first.");
            }

            _recordingCalls = new List<JsonValue>();
            _recordingExpiresAtUtc = RecordingClock() + RecordingTtl;

            return JsonValue.NewObject().SetProperty("recording", JsonValue.Bool(true));
        }

        /// <summary>Ends the active recording session (if any) and returns everything captured -
        /// idempotent when nothing was active (never began, already stopped, or silently expired):
        /// returns an empty 'calls' array rather than throwing, the same "closing something that
        /// was never opened is a safe no-op" contract EndScriptEditing and
        /// prefab.save_editing/PrefabCommands' own session both already establish.
        ///
        /// The returned shape - an array of {method, params, expected} - is DELIBERATELY IDENTICAL
        /// to hades.regression_replay's own 'calls' parameter (see RegressionReplay's doc comment
        /// above): this IS "record's storage and replay's input agree" - hand this result's 'calls'
        /// straight into hades.regression_replay with no translation step and no separate
        /// dataset-by-id store on either side of that hand-off.</summary>
        internal static JsonValue RegressionRecordStop(ReloadGate gate, JsonValue @params)
        {
            var callsJson = JsonValue.NewArray();
            if (IsRecordingActive())
                foreach (var call in _recordingCalls) callsJson.Add(call);

            _recordingCalls = null;

            return JsonValue.NewObject()
                .SetProperty("calls", callsJson)
                .SetProperty("count", JsonValue.Integer(callsJson.Items.Count));
        }

        /// <summary>True while a session is open AND has not gone silent past
        /// <see cref="RecordingTtl"/> - discards (and reports false for) a session found expired,
        /// so silence past the TTL expires it exactly like a class-3 lease's silence does, without
        /// needing a background watchdog to notice (see <see cref="RecordingTtl"/>'s own doc
        /// comment for why a lazy check is sufficient here).</summary>
        static bool IsRecordingActive()
        {
            if (_recordingCalls == null) return false;
            if (RecordingClock() < _recordingExpiresAtUtc) return true;

            _recordingCalls = null;
            return false;
        }

        /// <summary>Appends one dispatched call to the active recording session, if any - called
        /// from <see cref="CommandTable.Dispatch"/> after EVERY successful handler invocation
        /// (never for one that threw: Dispatch only reaches this call after <c>handler(...)</c>
        /// has already returned, so a thrown exception there never reaches here at all - a failed
        /// call is not a useful "expected" value to replay against later anyway). The two wire
        /// methods that open/close a session are excluded so a session never records its own
        /// start/stop; every other method - class 1, class 2, a concurrently-running
        /// BeginScriptEditing/EndScriptEditing, even hades.regression_replay itself - is captured,
        /// because all of those are exactly the "normal tool usage" this exists to capture. Each
        /// capture also renews the session's own TTL (see <see cref="RecordingTtl"/>): activity
        /// keeps a recording session alive, same "renewed by activity, not intent" spirit as
        /// class 3's leases, just checked lazily rather than via a watchdog.</summary>
        internal static void CaptureIfRecording(string method, JsonValue @params, JsonValue result)
        {
            if (method == "hades.regression_record_start" || method == "hades.regression_record_stop") return;
            if (!IsRecordingActive()) return;

            var entry = JsonValue.NewObject().SetProperty("method", JsonValue.String(method));
            if (@params != null) entry.SetProperty("params", @params);
            entry.SetProperty("expected", result ?? JsonValue.Null);
            _recordingCalls.Add(entry);

            _recordingExpiresAtUtc = RecordingClock() + RecordingTtl;
        }

        // ---------------------------------------------------------------- project.get_console_log

        /// <summary>Class 4 (live-state read - see this file's own class doc comment): recent
        /// entries from <see cref="ConsoleLogBuffer"/>, optionally filtered to one severity and
        /// capped at 'count' (default 50, max 200, min 1 - a caller-supplied value outside that
        /// range is clamped rather than rejected, since any positive intent is unambiguous). Also
        /// reports 'totalMatching' - how many buffered entries match 'type' before 'count' capped
        /// them - since 'totalBuffered' alone (every severity, ignoring 'type') cannot tell a
        /// caller whether the entries returned are all there are or just the newest slice. No
        /// lease: never references <paramref name="gate"/>, the same as inspector.select and every
        /// other handler in this file - there is nothing here a domain reload could interrupt.</summary>
        internal static JsonValue GetConsoleLog(ReloadGate gate, JsonValue @params)
        {
            var count = JsonParams.OptionalInt(@params, "count", 50);
            count = Math.Max(1, Math.Min(count, 200));

            LogType? filter = null;
            var typeName = JsonParams.OptionalString(@params, "type");
            if (!string.IsNullOrEmpty(typeName))
            {
                switch (typeName.ToLowerInvariant())
                {
                    case "error": filter = LogType.Error; break;
                    case "warning": filter = LogType.Warning; break;
                    case "log": filter = LogType.Log; break;
                    default:
                        throw new ArgumentException(
                            "'type' must be 'Error', 'Warning', or 'Log' (omit for every severity) - got '" + typeName + "'.");
                }
            }

            var (selected, totalBuffered, totalMatching) = ConsoleLogBuffer.GetRecent(count, filter);

            var entries = JsonValue.NewArray();
            foreach (var entry in selected)
            {
                entries.Add(JsonValue.NewObject()
                    .SetProperty("type", JsonValue.String(entry.Type.ToString()))
                    .SetProperty("message", JsonValue.String(entry.Message))
                    .SetProperty("stackTrace", JsonValue.String(entry.StackTrace)));
            }

            return JsonValue.NewObject()
                .SetProperty("entries", entries)
                .SetProperty("count", JsonValue.Integer(entries.Items.Count))
                .SetProperty("totalBuffered", JsonValue.Integer(totalBuffered))
                .SetProperty("totalMatching", JsonValue.Integer(totalMatching));
        }

        // ---------------------------------------------------------------- project.get_test_results

        /// <summary>Class 4 (live-state read): the poll side of project.run_tests, reconciled by
        /// runId - see <see cref="TestRunResultStore"/>'s own doc comment for the mechanism.
        /// 'runId' is optional: omitted, this reports on whichever run was started most recently
        /// (the common case, since only one run can be in flight at a time); supplied, a value
        /// that does not match that run answers 'unknown' rather than silently reporting on the
        /// wrong run or returning nothing. No lease, for the same reason as every handler in this
        /// file.</summary>
        internal static JsonValue GetTestResults(ReloadGate gate, JsonValue @params)
        {
            var requestedRunId = JsonParams.OptionalString(@params, "runId");

            if (!TestRunResultStore.HasStarted)
            {
                return JsonValue.NewObject()
                    .SetProperty("status", JsonValue.String("none"))
                    .SetProperty("note", JsonValue.String("No test run has been started this session. Call project_run_tests first."));
            }

            var currentRunId = TestRunResultStore.CurrentRunId;
            if (!string.IsNullOrEmpty(requestedRunId) && requestedRunId != currentRunId)
            {
                return JsonValue.NewObject()
                    .SetProperty("status", JsonValue.String("unknown"))
                    .SetProperty("runId", JsonValue.String(requestedRunId))
                    .SetProperty("note", JsonValue.String(
                        "No test run with runId '" + requestedRunId + "' is known. The most recently started run's id is '"
                        + currentRunId + "' - omit 'runId' to poll it, or call project_run_tests again to start a new run."));
            }

            if (!TestRunResultStore.IsComplete())
            {
                return JsonValue.NewObject()
                    .SetProperty("status", JsonValue.String("running"))
                    .SetProperty("runId", JsonValue.String(currentRunId))
                    .SetProperty("note", JsonValue.String("Test run in progress (EditMode runs include a domain reload). Poll again shortly."));
            }

            return ParseTestResults(currentRunId);
        }

        static JsonValue ParseTestResults(string runId)
        {
            XElement run;
            try
            {
                run = XDocument.Load(TestRunResultStore.ResultsPath).Root;
            }
            catch (Exception ex)
            {
                throw new ArgumentException(
                    "Test run '" + runId + "' looked complete, but its results at '" + TestRunResultStore.ResultsPath
                    + "' could not be parsed: " + ex.Message);
            }

            var passed = (int?)run.Attribute("passed") ?? 0;
            var failed = (int?)run.Attribute("failed") ?? 0;
            var skipped = (int?)run.Attribute("skipped") ?? 0;
            var inconclusive = (int?)run.Attribute("inconclusive") ?? 0;
            var total = (int?)run.Attribute("total") ?? (int?)run.Attribute("testcasecount") ?? (passed + failed + skipped + inconclusive);

            var failures = JsonValue.NewArray();
            foreach (var testCase in run.Descendants("test-case"))
            {
                if ((string)testCase.Attribute("result") != "Failed") continue;

                var name = (string)testCase.Attribute("fullname") ?? (string)testCase.Attribute("name") ?? "(unnamed)";
                var message = testCase.Element("failure")?.Element("message")?.Value?.Trim() ?? "";
                failures.Add(JsonValue.NewObject().SetProperty("name", JsonValue.String(name)).SetProperty("message", JsonValue.String(message)));
            }

            return JsonValue.NewObject()
                .SetProperty("status", JsonValue.String("complete"))
                .SetProperty("runId", JsonValue.String(runId))
                .SetProperty("total", JsonValue.Integer(total))
                .SetProperty("passed", JsonValue.Integer(passed))
                .SetProperty("failed", JsonValue.Integer(failed))
                .SetProperty("skipped", JsonValue.Integer(skipped))
                .SetProperty("inconclusive", JsonValue.Integer(inconclusive))
                .SetProperty("duration", JsonValue.String((string)run.Attribute("duration") ?? ""))
                .SetProperty("failures", failures);
        }
    }

    /// <summary>
    /// Reflection-only bridge to UnityEditor.TestTools.TestRunner.Api.TestRunnerApi - see
    /// ProjectCommands' own class doc comment for why no compile-time reference is possible (Hades'
    /// asmdef must keep an empty "references" array). Starts a run and returns immediately - there
    /// is no ICallbacks registration anywhere here; Unity itself writes the finished run to
    /// TestResults.xml, which <see cref="TestRunResultStore"/> (just below) tracks and
    /// project.get_test_results polls, mirroring the old package's own
    /// ProjectTools.RunTests/GetTestResults split.
    ///
    /// Public (not internal) so ProjectCommandsTests can call
    /// <see cref="TypesAreResolvable"/> directly - a safe, execution-free check that the
    /// reflection LOOKUP itself works in this environment - same InternalsVisibleTo reasoning as
    /// ProjectCommands.TriggerRecompile/StartTestRun.
    /// </summary>
    public static class TestRunnerBridge
    {
        const string ApiTypeName = "UnityEditor.TestTools.TestRunner.Api.TestRunnerApi";
        const string FilterTypeName = "UnityEditor.TestTools.TestRunner.Api.Filter";
        const string ExecutionSettingsTypeName = "UnityEditor.TestTools.TestRunner.Api.ExecutionSettings";
        const string TestModeTypeName = "UnityEditor.TestTools.TestRunner.Api.TestMode";

        /// <summary>True when every type this bridge needs is resolvable in the current AppDomain -
        /// i.e. the consuming project has the Test Framework package installed - without
        /// constructing or invoking anything. Exists so a test can prove the reflection LOOKUP
        /// itself works in this environment without ever calling Execute (which would start a
        /// real, and here recursive, test run - see TryStart's own doc comment). Public for the
        /// same InternalsVisibleTo reason the rest of this class is.</summary>
        public static bool TypesAreResolvable() =>
            FindType(ApiTypeName) != null && FindType(FilterTypeName) != null
            && FindType(ExecutionSettingsTypeName) != null && FindType(TestModeTypeName) != null;

        /// <summary>Starts a Unity Test Runner run via reflection and returns immediately -
        /// deliberately never called from this plugin's OWN test suite with a real testMode/filter
        /// that would actually execute (see ProjectCommands.StartTestRun's own doc comment): Unity
        /// Test Runner has no defined behaviour for "start a new run from code that is itself
        /// executing inside a currently-running run", and this bridge does not attempt to define
        /// one either.</summary>
        public static (bool Started, string Error) TryStart(string runId, string testMode, string filter)
        {
            try
            {
                var apiType = FindType(ApiTypeName);
                var filterType = FindType(FilterTypeName);
                var executionSettingsType = FindType(ExecutionSettingsTypeName);
                var testModeType = FindType(TestModeTypeName);

                if (apiType == null || filterType == null || executionSettingsType == null || testModeType == null)
                {
                    return (false,
                        "Unity Test Framework (com.unity.test-framework) is not available in this project - "
                        + "install the 'Test Framework' package via Package Manager to use project_run_tests.");
                }

                var modeValue = ResolveTestMode(testModeType, testMode);

                var filterInstance = Activator.CreateInstance(filterType);
                filterType.GetField("testMode").SetValue(filterInstance, modeValue);
                if (!string.IsNullOrEmpty(filter))
                {
                    var groupNames = Array.CreateInstance(typeof(string), 1);
                    groupNames.SetValue(filter, 0);
                    filterType.GetField("groupNames").SetValue(filterInstance, groupNames);
                }

                var filtersArray = Array.CreateInstance(filterType, 1);
                filtersArray.SetValue(filterInstance, 0);

                var executionSettings = Activator.CreateInstance(executionSettingsType, (object)filtersArray);

                var api = UnityEngine.ScriptableObject.CreateInstance(apiType);
                var executeMethod = apiType.GetMethod("Execute", new[] { executionSettingsType });
                executeMethod.Invoke(api, new[] { executionSettings });

                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, "Failed to start test run: " + ex.Message);
            }
        }

        static object ResolveTestMode(Type testModeType, string testMode)
        {
            switch ((testMode ?? "EditMode").ToLowerInvariant())
            {
                case "playmode":
                    return Enum.Parse(testModeType, "PlayMode");
                case "all":
                    var editMode = Convert.ToInt32(Enum.Parse(testModeType, "EditMode"));
                    var playMode = Convert.ToInt32(Enum.Parse(testModeType, "PlayMode"));
                    return Enum.ToObject(testModeType, editMode | playMode);
                default:
                    return Enum.Parse(testModeType, "EditMode");
            }
        }

        static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = assembly.GetType(fullName); }
                catch (Exception) { continue; }
                if (type != null) return type;
            }
            return null;
        }
    }

    /// <summary>
    /// Tracks Unity Test Runner runs across the domain reload an EditMode run triggers, so
    /// project.get_test_results (class 4 - live-state read) can reconcile a poll against the
    /// runId project.run_tests handed back. A run's outcome is only available after Unity's own
    /// RunFinished fires, which for EditMode happens on a LATER Editor tick, past a domain reload
    /// that wipes every plain static field in this assembly (see HadesBoot's own class doc
    /// comment) - so, like <see cref="ReloadGate"/>'s own leaked-lock detection, this MUST persist
    /// through <see cref="SessionState"/>, not plain fields, or a poll landing right after that
    /// reload would wrongly report "no run started" for a run genuinely still in flight.
    ///
    /// This is safe to read/write from <see cref="RunTests"/> and <see cref="GetTestResults"/>
    /// because both are command handlers, and HadesClient guarantees every command (this
    /// included) already runs on the main thread via MainThreadPump before either ever executes -
    /// the SAME guarantee <see cref="ReloadGate.Acquire"/>/<see cref="ReloadGate.Release"/> rely on
    /// for their own SessionState calls (see that class's own doc comment). Contrast
    /// <see cref="ConsoleLogBuffer"/> below, which deliberately does NOT use SessionState, because
    /// its own callback can arrive off the main thread, where that guarantee does not hold.
    ///
    /// The Unity Test Framework writes each completed run's NUnit3 results to
    /// <see cref="ResultsPath"/>; <see cref="MarkStarted"/> records that file's modification time
    /// as a baseline so a STALE file left over from a PRIOR run is never mistaken for this one's
    /// output - a run only reads as complete once the file is newer than that baseline.
    ///
    /// <see cref="ResultsPath"/> is a swappable public field, same convention as
    /// <see cref="ProjectCommands.TriggerRecompile"/>/<see cref="ProjectCommands.StartTestRun"/> -
    /// tests point it at a scratch file rather than racing whatever this SAME Editor process's own
    /// real Test Framework might write to that default location while these very tests run under
    /// -runTests.
    /// </summary>
    public static class TestRunResultStore
    {
        const string RunIdKey = "Hades.TestRun.RunId";
        const string BaselineKey = "Hades.TestRun.BaselineTicks";

        public static string ResultsPath = Path.Combine(Application.persistentDataPath, "TestResults.xml");

        public static bool HasStarted => !string.IsNullOrEmpty(SessionState.GetString(RunIdKey, ""));

        public static string CurrentRunId => SessionState.GetString(RunIdKey, "");

        /// <summary>Records the run that just started and <see cref="ResultsPath"/>'s current
        /// modification time - in <see cref="SessionState"/>, per this class's own doc comment, so
        /// both survive the domain reload an EditMode run triggers - as the baseline a completed
        /// run's rewrite must exceed. Call only once the caller is confident the run is actually
        /// starting - see <see cref="RunTests"/>, which calls this only after
        /// <see cref="ProjectCommands.StartTestRun"/> itself reports success.</summary>
        public static void MarkStarted(string runId)
        {
            var baselineTicks = File.Exists(ResultsPath) ? File.GetLastWriteTimeUtc(ResultsPath).Ticks : 0L;
            SessionState.SetString(BaselineKey, baselineTicks.ToString());
            SessionState.SetString(RunIdKey, runId);
        }

        /// <summary>True once <see cref="ResultsPath"/> has been (re)written since the matching
        /// <see cref="MarkStarted"/> call.</summary>
        public static bool IsComplete()
        {
            if (!HasStarted || !File.Exists(ResultsPath)) return false;
            var baselineTicks = long.TryParse(SessionState.GetString(BaselineKey, "0"), out var parsed) ? parsed : 0L;
            return File.GetLastWriteTimeUtc(ResultsPath).Ticks > baselineTicks;
        }

        /// <summary>Test-only reset - production code never needs to forget a started run; a new
        /// run's own MarkStarted simply supersedes it. Public for the same InternalsVisibleTo
        /// reason every other test seam in this file is.</summary>
        public static void Reset()
        {
            SessionState.EraseString(RunIdKey);
            SessionState.EraseString(BaselineKey);
        }
    }

    /// <summary>
    /// Thread-safe, bounded (drop-oldest) ring buffer of every message Unity's console receives,
    /// captured via <see cref="Application.logMessageReceivedThreaded"/> - what
    /// project.get_console_log (class 4 - live-state read) reads from.
    ///
    /// <see cref="Install"/> is called from <see cref="Hades.Runtime.HadesBoot"/>'s own static
    /// constructor, NOT lazily on this command's first call: a message logged before the first
    /// project_get_console_log call is exactly the one a caller is most likely asking about (a
    /// compile error just before recompiling, say), and a subscription installed only once a tool
    /// call happened to arrive would have already missed it. HadesBoot's static constructor
    /// re-runs after every domain reload (see its own class doc comment), and so does this -
    /// <see cref="Install"/> is idempotent (a reload wipes <c>_installed</c> along with everything
    /// else, so the next boot's call is a fresh, correct re-subscription, not a guarded-against
    /// double one).
    ///
    /// Deliberately NOT persisted via SessionState the way <see cref="TestRunResultStore"/> above
    /// is: <c>logMessageReceivedThreaded</c> fires on WHATEVER thread produced the message, which
    /// can be a background thread (see this event's own Unity documentation) - and SessionState's
    /// setters are main-thread-only (measured directly; see <see cref="ReloadGate"/>'s own doc
    /// comment for the identical constraint on its boot reconciliation), so touching SessionState
    /// from this callback would throw UnityException whenever a message arrived off the main
    /// thread. A plain, lock-protected static ring buffer is the only safe choice for a callback
    /// that can run anywhere. The trade-off: a domain reload (e.g. from project_recompile_scripts,
    /// or a project_run_tests run completing) clears this buffer's history along with every other
    /// managed static in this assembly. Recovering that history would need an explicit
    /// main-thread-only flush - mirroring the old package's own SessionState-backed
    /// ConsoleLogBuffer, which deferred every SessionState write to EditorApplication.update for
    /// exactly this reason - not implemented here, since it was not asked for and the added
    /// complexity (JSON round-tripping with zero third-party dependencies) is real.
    /// </summary>
    public static class ConsoleLogBuffer
    {
        public const int Capacity = 200;

        static readonly object Gate = new object();
        static readonly (LogType Type, string Message, string StackTrace)[] Entries = new (LogType, string, string)[Capacity];
        static int _head;
        static int _count;
        static bool _installed;

        /// <summary>Subscribes to the real log pipeline. Idempotent - see this class's own doc
        /// comment for why a guard is needed at all despite every call originating from a fresh
        /// boot.</summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            Application.logMessageReceivedThreaded += OnLogReceived;
        }

        static void OnLogReceived(string message, string stackTrace, LogType type) => Capture(type, message, stackTrace);

        /// <summary>Appends one entry, dropping the oldest once <see cref="Capacity"/> is
        /// exceeded. Public so tests can populate the buffer directly and deterministically,
        /// without a real Debug.Log call fighting Unity Test Runner's own LogAssert
        /// fail-on-unhandled-error machinery - same "public seam, no InternalsVisibleTo" convention
        /// as <see cref="ProjectCommands.TriggerRecompile"/>/<see cref="ProjectCommands.StartTestRun"/>.</summary>
        public static void Capture(LogType type, string message, string stackTrace)
        {
            lock (Gate)
            {
                Entries[_head] = (type, message ?? "", stackTrace ?? "");
                _head = (_head + 1) % Capacity;
                if (_count < Capacity) _count++;
            }
        }

        /// <summary>The <paramref name="count"/> most recent entries matching
        /// <paramref name="filter"/> (null = every severity), oldest-of-the-selected-window first,
        /// alongside how many entries the buffer currently holds in total (capped at
        /// <see cref="Capacity"/>, regardless of <paramref name="filter"/>) and how many of those
        /// buffered entries match <paramref name="filter"/> - the count <c>Selected</c> was taken
        /// from before <paramref name="count"/> capped it. <c>TotalBuffered</c> alone cannot answer
        /// "are there more matches than I got back?" once a filter is applied (it counts every
        /// severity, not just the one asked for); <c>TotalMatching</c> can. Filters across the
        /// WHOLE buffer before taking the most recent <paramref name="count"/> - deliberately NOT
        /// "the most recent count entries, then filtered" - so asking for the last 5 errors
        /// returns the last 5 errors even when non-matching entries sit between them, rather than
        /// however many happen to survive a raw positional slice taken first.</summary>
        public static ((LogType Type, string Message, string StackTrace)[] Selected, int TotalBuffered, int TotalMatching) GetRecent(int count, LogType? filter)
        {
            (LogType Type, string Message, string StackTrace)[] snapshot;
            int totalBuffered;

            lock (Gate)
            {
                totalBuffered = _count;
                snapshot = new (LogType, string, string)[_count];
                for (var i = 0; i < _count; i++)
                {
                    var idx = (_head - _count + i + Capacity) % Capacity;
                    snapshot[i] = Entries[idx];
                }
            }

            var matching = filter.HasValue ? Array.FindAll(snapshot, e => e.Type == filter.Value) : snapshot;
            var take = Math.Min(count, matching.Length);
            var result = new (LogType, string, string)[take];
            Array.Copy(matching, matching.Length - take, result, 0, take);
            return (result, totalBuffered, matching.Length);
        }

        /// <summary>Test-only reset. Public for the same InternalsVisibleTo reason every other
        /// test seam in this file is.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                Array.Clear(Entries, 0, Entries.Length);
                _head = 0;
                _count = 0;
            }
        }
    }
}
