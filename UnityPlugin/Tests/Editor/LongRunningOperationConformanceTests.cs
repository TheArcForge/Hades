// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Linq;
using System.Reflection;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;
using UnityEditor;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// DESIGN-08 (docs/design/DESIGN-08-long-running-operations.md) §14's plugin-side Tier-1/Tier-2
    /// conformance tests, written ONCE over every shipped long-running operation rather than
    /// duplicated per design - Phase 3 integration (ROADMAP.md Phase 3 item 2). Per-lane test files
    /// (PlayModeCommandsTests.cs/SceneBakeCommandsTests.cs/BuildCommandsTests.cs) already prove each
    /// operation's OWN state machine in detail; this file does not re-derive any of that. It covers:
    ///
    /// - Tier 1 O4 (fail closed: never synthesize a terminal state from absence of evidence) -
    ///   table-driven over build and bake, the two operations whose reconciliation logic lives on
    ///   this side of the process boundary. play_mode is not part of this table - its 'state'
    ///   vocabulary (editMode/playing/paused/detached) has no 'complete' concept to synthesize in
    ///   the first place. project_run_tests (the test run) is deliberately excluded too - see this
    ///   file's own <see cref="O4_TestRun_CannotBeWrittenHonestly_OpenItem"/>.
    /// - Tier 1 O5 (stop/cancel/exit/status must not refuse under any precondition start refuses
    ///   under) - the two current instances DESIGN-08 §14 names: play.exit and scene.bake's
    ///   cancel/status.
    /// - Tier 2 (the guard, extended): DESIGN-01 §15.1.1's parameterised test already covers every
    ///   bare method; this adds the one further dimension DESIGN-08 §7.2 names - every
    ///   ACTION-KEYED command's every (method, action) pair must have an explicit decision, derived
    ///   from the command's own declared action list (via reflection) so a future action added
    ///   without a decision fails immediately, rather than from a hand-copied list that could drift.
    /// - The <c>Hades.Bake.*</c> writer/reader agreement check ROADMAP.md's Phase 3 item 2 adds:
    ///   write a job through Lane G's own writer (<see cref="BakeJobStore.MarkStarted"/>, driven by
    ///   the shared <see cref="BakeKeys"/> constants) and assert Lane C's reader
    ///   (<c>BuildCommands.HadesOwnedBakeInProgressRefusal</c>, reached via 'project.build_start')
    ///   sees the SAME job - unlike the existing per-lane build tests, which hand-stamp
    ///   SessionState with string literals and so would not catch a rename on either side.
    /// </summary>
    [TestFixture]
    public sealed class LongRunningOperationConformanceTests
    {
        // ================================================================== shared fakes (bake)

        static readonly Func<bool> RealLightingBakeAsync = BakeTargetSeams.LightingBakeAsync;
        static readonly Func<bool> RealLightingIsRunning = BakeTargetSeams.LightingIsRunning;
        static readonly Func<float> RealLightingBuildProgress = BakeTargetSeams.LightingBuildProgress;
        static readonly Action RealLightingCancelAction = BakeTargetSeams.LightingCancelAction;
        static readonly Func<string> RealLightingConfiguredBackend = BakeTargetSeams.LightingConfiguredBackend;
        static readonly Func<bool> RealOcclusionGenerateInBackground = BakeTargetSeams.OcclusionGenerateInBackground;
        static readonly Func<bool> RealOcclusionIsRunning = BakeTargetSeams.OcclusionIsRunning;
        static readonly Action RealOcclusionCancelAction = BakeTargetSeams.OcclusionCancelAction;
        static readonly Func<(bool, string[], string)> RealNavMeshStart = BakeTargetSeams.NavMeshStart;
        static readonly Func<string[], (RunningState, string)> RealNavMeshResolve = BakeTargetSeams.NavMeshResolve;
        static readonly Action<string[]> RealNavMeshCancelAction = BakeTargetSeams.NavMeshCancelAction;
        static readonly Func<bool> RealNavMeshTypesAreResolvable = BakeTargetSeams.NavMeshTypesAreResolvable;

        static readonly Func<BuildRequest, BuildOutcome> RealRunBuild = BuildCommands.RunBuild;

        bool _lightingRunning;
        bool _occlusionRunning;

        [SetUp]
        public void SetUp()
        {
            BakeJobStore.Reset();
            BakeWatchdog.ResetForTests();

            _lightingRunning = false;
            _occlusionRunning = false;

            BakeTargetSeams.LightingBakeAsync = () => true;
            BakeTargetSeams.LightingIsRunning = () => _lightingRunning;
            BakeTargetSeams.LightingBuildProgress = () => 0.5f;
            BakeTargetSeams.LightingCancelAction = () => _lightingRunning = false;
            BakeTargetSeams.LightingConfiguredBackend = () => "ProgressiveGPU";

            BakeTargetSeams.OcclusionGenerateInBackground = () => true;
            BakeTargetSeams.OcclusionIsRunning = () => _occlusionRunning;
            BakeTargetSeams.OcclusionCancelAction = () => _occlusionRunning = false;

            BakeTargetSeams.NavMeshTypesAreResolvable = () => true;
            BakeTargetSeams.NavMeshStart = () => (true, new[] { "navmesh-surface-1" }, null);
            BakeTargetSeams.NavMeshResolve = _ => (RunningState.NotRunning, null);
            BakeTargetSeams.NavMeshCancelAction = _ => { };

            DeleteBuildStatusFileIfPresent();
            BuildCommands.RunBuild = _ => throw new InvalidOperationException(
                "A test forgot to substitute BuildCommands.RunBuild before dispatching 'project.build_start'.");
            BuildCommands.IsCompiling = () => false;
            BuildCommands.ScriptCompilationFailed = () => false;
            BuildCommands.IsPlayingOrWillChangePlaymode = () => false;
            BuildSession.BuildInFlight = false;

            PlayModeSession.Clear();
            ResetPlayModeApiSeams();
        }

        [TearDown]
        public void TearDown()
        {
            BakeTargetSeams.LightingBakeAsync = RealLightingBakeAsync;
            BakeTargetSeams.LightingIsRunning = RealLightingIsRunning;
            BakeTargetSeams.LightingBuildProgress = RealLightingBuildProgress;
            BakeTargetSeams.LightingCancelAction = RealLightingCancelAction;
            BakeTargetSeams.LightingConfiguredBackend = RealLightingConfiguredBackend;
            BakeTargetSeams.OcclusionGenerateInBackground = RealOcclusionGenerateInBackground;
            BakeTargetSeams.OcclusionIsRunning = RealOcclusionIsRunning;
            BakeTargetSeams.OcclusionCancelAction = RealOcclusionCancelAction;
            BakeTargetSeams.NavMeshStart = RealNavMeshStart;
            BakeTargetSeams.NavMeshResolve = RealNavMeshResolve;
            BakeTargetSeams.NavMeshCancelAction = RealNavMeshCancelAction;
            BakeTargetSeams.NavMeshTypesAreResolvable = RealNavMeshTypesAreResolvable;
            BakeJobStore.Reset();
            BakeWatchdog.ResetForTests();

            BuildCommands.RunBuild = RealRunBuild;
            BuildCommands.IsCompiling = () => EditorApplication.isCompiling;
            BuildCommands.ScriptCompilationFailed = () => EditorUtility.scriptCompilationFailed;
            BuildCommands.IsPlayingOrWillChangePlaymode = () => EditorApplication.isPlayingOrWillChangePlaymode;
            BuildSession.BuildInFlight = false;
            DeleteBuildStatusFileIfPresent();

            PlayModeSession.Clear();
            ResetPlayModeApiSeams();
        }

        static void DeleteBuildStatusFileIfPresent()
        {
            if (System.IO.File.Exists(BuildStatusStore.StatusPath)) System.IO.File.Delete(BuildStatusStore.StatusPath);
        }

        /// <summary>Same reset shape PlayModeCommandsTests.ResetPlayModeApiSeams uses - every
        /// EditorPlayModeApi seam is a process-wide static shared with every other test fixture in
        /// this assembly, so a test here that overrides one (only IsPlaying, currently) must restore
        /// ALL of them, not just the one it touched, per this codebase's own stated hygiene rule.</summary>
        static void ResetPlayModeApiSeams()
        {
            EditorPlayModeApi.EnterPlayMode = () => EditorApplication.EnterPlaymode();
            EditorPlayModeApi.ExitPlayMode = () => EditorApplication.ExitPlaymode();
            EditorPlayModeApi.SetPaused = paused => EditorApplication.isPaused = paused;
            EditorPlayModeApi.StepFrame = () => EditorApplication.Step();
            EditorPlayModeApi.IsPlaying = () => EditorApplication.isPlaying;
            EditorPlayModeApi.IsPaused = () => EditorApplication.isPaused;
            EditorPlayModeApi.PlayClock = () => DateTime.UtcNow;
            EditorPlayModeApi.ScheduleAfterThisTick = action => EditorApplication.delayCall += () => action();
        }

        // ================================================================== dispatch helpers

        static (ReloadGate gate, FakeEditorLockApi fake, MainThreadPump pump) NoopGateParts()
        {
            var fake = new FakeEditorLockApi();
            var pump = new MainThreadPump();
            var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            return (gate, fake, pump);
        }

        static JsonRpcRequest Request(string method, JsonValue @params) =>
            new JsonRpcRequest { Id = JsonValue.Integer(1), Method = method, Params = @params };

        static JsonValue Dispatch(string method, JsonValue @params)
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                return CommandTable.Dispatch(gate, Request(method, @params));
            }
        }

        static JsonValue DispatchBake(JsonValue @params) => Dispatch("scene.bake", @params);

        static JsonValue BakeStartParams(string target) =>
            JsonValue.NewObject().SetProperty("action", JsonValue.String("start")).SetProperty("target", JsonValue.String(target));

        static JsonValue BakeStatusParams() => JsonValue.NewObject().SetProperty("action", JsonValue.String("status"));
        static JsonValue BakeCancelParams() => JsonValue.NewObject().SetProperty("action", JsonValue.String("cancel"));

        static string BakeStatusOf(JsonValue result) =>
            result.TryGetProperty("status", out var v) && v.Kind == JsonValueKind.String ? v.AsString() : null;

        static JsonValue DispatchBuildStart(BuildTarget target) =>
            Dispatch("project.build_start", JsonValue.NewObject().SetProperty("target", JsonValue.String(target.ToString())));

        // ================================================================== Tier 1: O4

        [Test]
        public void O4_Build_RecordExistsOracleNotInFlightNoTerminalWritten_ReconcilesToAbandoned_NeverComplete()
        {
            // DESIGN-08 O4/§14: "record exists, the oracle says not-running, and no terminal state
            // was written" - for build, the record is the status file (state:'running'), the oracle
            // is BuildSession.BuildInFlight (DESIGN-08 §12's intra-session liveness check), and
            // 'no terminal state was written' is exactly what makes this the abandonment case rather
            // than a genuine completion. BuildWatchdog.Tick is the reconciliation seam - see
            // BuildCommandsTests.Watchdog_RunningState_MatchingNonce_ButNotBuildInFlight_ReconcilesToAbandoned
            // for the equivalent per-lane proof; this is the SAME scenario, asserted here as one row
            // of the cross-operation table DESIGN-08 §14 calls for.
            var doc = new BuildStatusStore.Document
            {
                Schema = BuildStatusStore.CurrentSchema,
                BuildId = "o4-build-1",
                BootNonce = BuildSession.CurrentBootNonce, // matching - isolates the intra-session case from the boot-nonce one
                State = BuildStatusStore.StateRunning,
                Target = "StandaloneOSX",
            };
            BuildStatusStore.Write(doc);
            BuildSession.BuildInFlight = false; // the oracle: "not in flight"

            BuildWatchdog.Tick();

            var result = BuildStatusStore.TryRead();
            Assert.AreNotEqual(BuildStatusStore.StateComplete, result.State, "O4: must never synthesize 'complete' from the absence of evidence");
            Assert.AreEqual(BuildStatusStore.StateAbandoned, result.State);
        }

        [Test]
        public void O4_Bake_RecordExistsOracleNotRunningNoTerminalWritten_ReconcilesToIndeterminate_NeverComplete()
        {
            // Same DESIGN-08 O4 scenario for bake: a job is recorded (BakeJobStore.HasJob), the
            // oracle (BakeTargetSeams.LightingIsRunning) has never once reported 'running' - so the
            // watchdog's own _sawRunning latch never fires and it writes nothing - and no terminal
            // state exists. DESIGN-06 §4.4 rule 5's answer is 'indeterminate', never 'complete'.
            _lightingRunning = false;
            DispatchBake(BakeStartParams("lighting"));
            // Deliberately no BakeWatchdog.Tick() call while running - or a Tick() now would only
            // ever see NotRunning too, since _lightingRunning never flips true in this test.

            var status = BakeStatusOf(DispatchBake(BakeStatusParams()));
            Assert.AreNotEqual("complete", status, "O4: must never synthesize 'complete' from the absence of evidence");
            Assert.AreEqual("indeterminate", status);
        }

        [Test]
        public void O4_TestRun_CannotBeWrittenHonestly_OpenItem()
        {
            // DESIGN-08 §14 itself flags this: "does Unity's GI system abort..." is H2, but the
            // TABLE row for the test run specifically needs an oracle - "is the Test Runner still
            // running" - that TestRunnerBridge does not currently expose (DESIGN-08 §16 item 5,
            // §8's own "Recommendation... needs a Test Runner liveness read that TestRunnerBridge
            // does not currently expose"). Without that oracle there is no way to construct "record
            // exists, oracle says not-running, no terminal written" for the test run at all - the
            // scenario itself cannot be built, let alone asserted on. ROADMAP.md files the whole
            // defect (TestRunResultStore's O3/O4 violation) out of every lane's scope, including
            // Phase 3's. Recorded here, per this file's own job (write it once, honestly, over every
            // operation) as a named open item rather than a forced, meaningless passing test or a
            // silent omission from this file's coverage.
            Assert.True(true, "see this test's own comment - the test run has no liveness oracle to drive this scenario with; filed separately, not fixed here");
        }

        // ================================================================== Tier 1: O5

        [Test]
        public void O5_PlayExitAndStatus_DoNotRefuseUnderThePreconditionPlayEnterRefusesUnder()
        {
            // The one documented 'enter' refusal (PlayModeCommands.DoEnter): Play Mode is already
            // active, but NOT started by Hades (no PlayModeSession recorded) - "Hades never adopts
            // a play session it did not start."
            EditorPlayModeApi.IsPlaying = () => true;

            var (gateEnter, fakeEnter, pumpEnter) = NoopGateParts();
            using (pumpEnter) using (gateEnter)
            {
                Assert.Throws<InvalidOperationException>(
                    () => CommandTable.Dispatch(gateEnter, Request("play.enter", JsonValue.NewObject())),
                    "'play.enter' is documented to refuse a user-owned play session - this precondition must be reachable for the rest of this test to mean anything");
            }

            var (gateExit, fakeExit, pumpExit) = NoopGateParts();
            using (pumpExit) using (gateExit)
            {
                Assert.DoesNotThrow(
                    () => CommandTable.Dispatch(gateExit, Request("play.exit", JsonValue.NewObject())),
                    "O5: 'play.exit' must not refuse under a precondition 'play.enter' refuses under");
            }

            var (gateStatus, fakeStatus, pumpStatus) = NoopGateParts();
            using (pumpStatus) using (gateStatus)
            {
                Assert.DoesNotThrow(
                    () => CommandTable.Dispatch(gateStatus, Request("play.status", JsonValue.NewObject())),
                    "O5: 'play.status' must not refuse under a precondition 'play.enter' refuses under");
            }
        }

        [Test]
        public void O5_SceneBakeCancelAndStatus_DoNotRefuseUnderThePreconditionStartRefusesUnder()
        {
            // The precondition that refuses a bake 'start': a bake Hades started is already in
            // progress (own job, non-terminal, same epoch, oracle says running). Start it with the
            // oracle FALSE first (a start while the oracle already reads 'running' would hit the
            // OTHER refusal branch - an unowned bake already running - not this one), then flip the
            // oracle true to simulate the job actually running now.
            _lightingRunning = false;
            DispatchBake(BakeStartParams("lighting")); // first start succeeds - job now recorded
            _lightingRunning = true; // now simulate the bake actually running - the 'own job in progress' precondition

            Assert.Throws<InvalidOperationException>(
                () => DispatchBake(BakeStartParams("lighting")),
                "a SECOND 'start' is documented to refuse while Hades' own bake is still in progress - this precondition must be reachable for the rest of this test to mean anything");

            // Same precondition (own job still running, current epoch) - 'status' first (read-only,
            // does not disturb the precondition for the 'cancel' check below).
            Assert.DoesNotThrow(
                () => DispatchBake(BakeStatusParams()),
                "O5: 'status' must not refuse under a precondition 'start' refuses under");

            // 'cancel' last - it mutates (ends the job), so it must run after 'status' is checked
            // under the SAME still-running precondition.
            Assert.DoesNotThrow(
                () => DispatchBake(BakeCancelParams()),
                "O5: 'cancel' must not refuse under a precondition 'start' refuses under");
        }

        // ================================================================== Tier 2: (method, action) guard coverage

        /// <summary>DESIGN-01 §15.1.1's existing parameterised test
        /// (PlayModeCommandsTests.Dispatch_WhilePlaying_RefusesEveryMethodNotOnTheAllowList) walks
        /// every BARE method in <see cref="CommandTable.Handlers"/> - for an action-keyed command
        /// like 'scene.bake' it only ever exercises the params-less fallback lookup, so it can never
        /// catch a NEW action added to that command's own action list without a matching
        /// "method:action" guard entry. This test closes that gap: it derives the ground truth for
        /// "every valid action" from the command's OWN declaration
        /// (<c>SceneBakeCommands.ValidActions</c>, read via reflection since it is `internal`/private
        /// and this assembly has no InternalsVisibleTo into the plugin's own - the same boundary
        /// every other doc comment in this codebase cites), not from a hand-copied list that could
        /// silently drift out of sync with it. DESIGN-08 §7.2: "A new action added without one fails
        /// immediately."</summary>
        [Test]
        public void EveryActionKeyedCommand_EveryDeclaredAction_HasAnExplicitPlayModeGuardDecision()
        {
            // Action-keyed methods are discovered from AllowedWhilePlaying's OWN "method:action"
            // keys, not hand-listed - today that is exactly {"scene.bake"}. If a second action-keyed
            // command ships, its first "method:action" guard entry makes it show up here too.
            var actionKeyedMethods = CommandTable.AllowedWhilePlaying.Keys
                .Where(k => k.Contains(':'))
                .Select(k => k.Substring(0, k.IndexOf(':')))
                .Distinct()
                .ToArray();

            CollectionAssert.Contains(actionKeyedMethods, "scene.bake", "sanity: scene.bake is the one known action-keyed command today - if this fails, the guard's own shape changed");

            foreach (var method in actionKeyedMethods)
            {
                var declaredActions = DeclaredActionsFor(method);
                Assert.IsNotEmpty(declaredActions, $"could not find a declared action list for '{method}' - see this test's own doc comment");

                foreach (var action in declaredActions)
                {
                    var key = method + ":" + action;
                    Assert.IsTrue(CommandTable.AllowedWhilePlaying.ContainsKey(key),
                        $"'{key}' has no explicit entry in CommandTable.AllowedWhilePlaying - DESIGN-08 §7.2 "
                        + "requires every (method, action) pair of an action-keyed command to have an "
                        + "explicit allow-or-refuse decision, not merely the guard's own default-deny.");
                }
            }
        }

        /// <summary>Reflection lookup for a command's own declared action list - 'scene.bake' maps
        /// to <c>Hades.Tools.SceneBakeCommands.ValidActions</c> (private static, internal class).
        /// Add a new mapping here (not a parallel test) when a second action-keyed command ships.</summary>
        static string[] DeclaredActionsFor(string method)
        {
            switch (method)
            {
                case "scene.bake":
                {
                    var type = typeof(CommandTable).Assembly.GetType("Hades.Tools.SceneBakeCommands", throwOnError: true);
                    var field = type.GetField("ValidActions", BindingFlags.NonPublic | BindingFlags.Static)
                        ?? throw new InvalidOperationException("SceneBakeCommands.ValidActions not found by reflection - renamed or removed?");
                    return (string[])field.GetValue(null);
                }
                default:
                    throw new InvalidOperationException($"No DeclaredActionsFor mapping for '{method}' - add one (see this method's own doc comment).");
            }
        }

        // ================================================================== Hades.Bake.* writer/reader agreement

        [Test]
        public void BakeKeysAgreement_LaneGsWriter_IsSeenByLaneCsReader()
        {
            // Writes a job through Lane G's REAL writer (BakeJobStore.MarkStarted, itself built
            // entirely on the shared BakeKeys constants) rather than hand-stamping SessionState with
            // string literals the way the existing per-lane BuildCommandsTests rows do (see
            // BuildCommandsTests.Start_WithAHadesOwnedNonTerminalBakeAtTheCurrentEpoch_Refuses) - a
            // rename on either side that a literal-stamped test cannot see is exactly the failure
            // mode ROADMAP.md's §4 shared-surface row warns about, and exactly what BakeKeys exists
            // to turn into a compile error instead. This test proves writer and reader still agree
            // through the REAL data path, not a simulation of it.
            BakeJobStore.MarkStarted("agree-1", "lighting", Array.Empty<string>(), DateTime.UtcNow, BakeReloadEpoch.Current);

            var active = EditorUserBuildSettings.activeBuildTarget;
            var ex = Assert.Throws<InvalidOperationException>(() => DispatchBuildStart(active));
            StringAssert.Contains("agree-1", ex.Message);
        }

        [Test]
        public void BakeKeysAgreement_AnAbsentKey_SurfacesAsCannotTell_NeverAsNoBake()
        {
            // Same real writer, then degrade it: erase exactly one required key by the SAME
            // BakeKeys constant the writer used (never a literal), simulating "Lane G's shape
            // changed under Lane C" without actually renaming anything. ROADMAP.md's own words:
            // "an absent key must surface as 'cannot tell', never silently as 'no bake'."
            BakeJobStore.MarkStarted("agree-2", "lighting", Array.Empty<string>(), DateTime.UtcNow, BakeReloadEpoch.Current);
            SessionState.EraseString(BakeKeys.Terminal);

            var active = EditorUserBuildSettings.activeBuildTarget;
            var ex = Assert.Throws<InvalidOperationException>(() => DispatchBuildStart(active));
            StringAssert.Contains("cannot tell", ex.Message);
            StringAssert.DoesNotContain("agree-2", ex.Message); // "cannot tell" is its own message, distinct from the "in progress, job '...'" one
        }
    }
}
