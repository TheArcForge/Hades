// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// DESIGN-06 (docs/design/DESIGN-06-baking-and-subsystems.md) §9's plugin EditMode suite.
    /// Reaches every handler through <see cref="CommandTable.Dispatch"/>, same convention as
    /// <see cref="BuildCommandsTests"/>/<see cref="PlayModeCommandsTests"/>. Every
    /// <see cref="BakeTargetSeams"/> field is substituted in <see cref="SetUp"/> and restored in
    /// <see cref="TearDown"/> - see that class's own doc comment: a test must NEVER trigger a real
    /// bake (DESIGN-06 §9's own stated requirement).
    ///
    /// The bolded row from §9's own test table - an epoch mismatch, isRunning true, then true-&gt;false
    /// (the user's own bake) - is what <see cref="Watchdog_EpochMismatch_NeverWritesCompleteRegardlessOfTransition"/>
    /// proves: the observer defers ENTIRELY to 'status' across a reload boundary, so this can never
    /// happen by construction, not merely by having been tested once.
    /// </summary>
    [TestFixture]
    public sealed class SceneBakeCommandsTests
    {
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

        // Test-controlled fakes, reassignable per test.
        bool _lightingRunning;
        bool _occlusionRunning;
        Func<string[], (RunningState, string)> _navMeshResolve = _ => (RunningState.NotRunning, null);

        [SetUp]
        public void SetUp()
        {
            BakeJobStore.Reset();
            BakeWatchdog.ResetForTests();

            _lightingRunning = false;
            _occlusionRunning = false;
            _navMeshResolve = _ => (RunningState.NotRunning, null);

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
            BakeTargetSeams.NavMeshResolve = scope => _navMeshResolve(scope);
            BakeTargetSeams.NavMeshCancelAction = _ => { };
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
        }

        static (ReloadGate gate, FakeEditorLockApi fake, MainThreadPump pump) NoopGateParts()
        {
            var fake = new FakeEditorLockApi();
            var pump = new MainThreadPump();
            var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            return (gate, fake, pump);
        }

        static JsonRpcRequest Request(string method, JsonValue @params) =>
            new JsonRpcRequest { Id = JsonValue.Integer(1), Method = method, Params = @params };

        static JsonValue Dispatch(JsonValue @params)
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                return CommandTable.Dispatch(gate, Request("scene.bake", @params));
            }
        }

        static JsonValue StartParams(string target) =>
            JsonValue.NewObject().SetProperty("action", JsonValue.String("start")).SetProperty("target", JsonValue.String(target));

        static JsonValue StatusParams(string jobId = null)
        {
            var p = JsonValue.NewObject().SetProperty("action", JsonValue.String("status"));
            if (jobId != null) p.SetProperty("jobId", JsonValue.String(jobId));
            return p;
        }

        static JsonValue CancelParams(string jobId = null)
        {
            var p = JsonValue.NewObject().SetProperty("action", JsonValue.String("cancel"));
            if (jobId != null) p.SetProperty("jobId", JsonValue.String(jobId));
            return p;
        }

        static string Status(JsonValue result) => result.TryGetProperty("status", out var v) && v.Kind == JsonValueKind.String ? v.AsString() : null;
        static string JobId(JsonValue result) => result.TryGetProperty("jobId", out var v) && v.Kind == JsonValueKind.String ? v.AsString() : null;

        // ============================================================== params / vocabulary

        [Test]
        public void UnknownAction_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => Dispatch(JsonValue.NewObject().SetProperty("action", JsonValue.String("bogus"))));
            StringAssert.Contains("action", ex.Message);
        }

        [Test]
        public void Start_UnknownTarget_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => Dispatch(StartParams("bogus-target")));
            StringAssert.Contains("target", ex.Message);
        }

        [Test]
        public void Status_NoJobStartedThisSession_ReturnsNone()
        {
            var result = Dispatch(StatusParams());
            Assert.AreEqual("none", Status(result));
        }

        [Test]
        public void Status_UnknownJobId_ReturnsUnknown_NamesTheCurrentJobId()
        {
            var started = Dispatch(StartParams("lighting"));
            var currentJobId = JobId(started);

            var result = Dispatch(StatusParams("not-a-real-job-id"));
            Assert.AreEqual("unknown", Status(result));
            StringAssert.Contains(currentJobId, result.TryGetProperty("detail", out var d) ? d.AsString() : "");
        }

        [Test]
        public void Cancel_NoJobStartedThisSession_ReturnsNone()
        {
            var result = Dispatch(CancelParams());
            Assert.AreEqual("none", Status(result));
        }

        // ============================================================== unavailable (navmesh capability gate)

        [Test]
        public void Start_Navmesh_Unavailable_ReturnsUnavailableStatus_DoesNotThrow_NeverCallsNavMeshStart()
        {
            BakeTargetSeams.NavMeshTypesAreResolvable = () => false;
            BakeTargetSeams.NavMeshStart = () => throw new InvalidOperationException("must not be called when unavailable");

            var result = Dispatch(StartParams("navmesh"));
            Assert.AreEqual("unavailable", Status(result));
        }

        // ============================================================== ownership / refusal table

        [Test]
        public void Start_WhileOurOwnJobStillRunning_Refused_NamesTheRunningJobId()
        {
            var started = Dispatch(StartParams("lighting"));
            var jobId = JobId(started);
            _lightingRunning = true;

            var ex = Assert.Throws<InvalidOperationException>(() => Dispatch(StartParams("lighting")));
            StringAssert.Contains(jobId, ex.Message);
        }

        [Test]
        public void Start_WhileAnUnownedLightingBakeRuns_Refused_NoIdInvented()
        {
            _lightingRunning = true; // nobody started a Hades job; the global boolean is just true

            var ex = Assert.Throws<InvalidOperationException>(() => Dispatch(StartParams("lighting")));
            StringAssert.Contains("did not start", ex.Message);
        }

        [Test]
        public void Start_NotRunning_Proceeds_OverwritesTheSlot()
        {
            var first = Dispatch(StartParams("lighting"));
            Assert.NotNull(JobId(first));

            _lightingRunning = false; // the first bake already finished, unobserved
            var second = Dispatch(StartParams("occlusion"));
            Assert.AreNotEqual(JobId(first), JobId(second));
            Assert.AreEqual("occlusion", second.TryGetProperty("target", out var t) ? t.AsString() : null);
        }

        // ============================================================== the three false-'complete' rows (§9)

        [Test]
        public void Status_JustStarted_BeforeIsRunningFlipsTrue_ReturnsIndeterminate_NeverComplete()
        {
            // _lightingRunning starts false and is never set true - simulates the real race where
            // BakeAsync() has been called but isRunning has not ticked true yet.
            var started = Dispatch(StartParams("lighting"));
            Assert.AreNotEqual("complete", Status(started));

            var status = Dispatch(StatusParams());
            Assert.AreEqual("indeterminate", Status(status));
        }

        [Test]
        public void Watchdog_ObservesTrueToFalseTransition_WritesCompleteExactlyOnce()
        {
            Dispatch(StartParams("lighting"));

            _lightingRunning = true;
            BakeWatchdog.Tick(); // latches _sawRunning
            Assert.AreEqual("running", Status(Dispatch(StatusParams())));

            _lightingRunning = false;
            BakeWatchdog.Tick(); // observes true -> false: writes 'complete'

            var status = Dispatch(StatusParams());
            Assert.AreEqual("complete", Status(status));

            // Sticky: further ticks (even a spurious true again) must not un-write it.
            _lightingRunning = true;
            BakeWatchdog.Tick();
            Assert.AreEqual("complete", Status(Dispatch(StatusParams())));
        }

        [Test]
        public void ReloadEpochBumped_NonTerminalJob_IsRunningFalse_ResolvesToCancelled_NeverComplete()
        {
            // The single most important test in this design (DESIGN-06 §9): a domain reload happened
            // (epoch bumped) while a job was non-terminal, and nothing is running now.
            Dispatch(StartParams("lighting"));
            _lightingRunning = false;

            BakeReloadEpoch.Bump();

            var status = Dispatch(StatusParams());
            Assert.AreEqual("cancelled", Status(status));
            Assert.AreNotEqual("complete", Status(status));
        }

        [Test]
        public void ReloadEpochBumped_IsRunningTrue_ReturnsIndeterminate_WritesNoTerminal()
        {
            Dispatch(StartParams("lighting"));
            BakeReloadEpoch.Bump();
            _lightingRunning = true; // could be the user's own bake

            var status = Dispatch(StatusParams());
            Assert.AreEqual("indeterminate", Status(status));

            // Deliberately non-sticky - a later poll after it stops must still resolve to 'cancelled'.
            _lightingRunning = false;
            var later = Dispatch(StatusParams());
            Assert.AreEqual("cancelled", Status(later));
        }

        [Test]
        public void Watchdog_EpochMismatch_NeverWritesCompleteRegardlessOfTransition()
        {
            // The bolded row: epoch bumped, then isRunning goes true -> false (the user's OWN bake) -
            // must never be attributed to Hades' job as 'complete'.
            Dispatch(StartParams("lighting"));
            BakeReloadEpoch.Bump();

            _lightingRunning = true;
            BakeWatchdog.Tick(); // must do nothing across an epoch mismatch - deferred to 'status'
            _lightingRunning = false;
            BakeWatchdog.Tick();

            var status = Dispatch(StatusParams());
            Assert.AreNotEqual("complete", Status(status));
            Assert.AreEqual("cancelled", Status(status));
        }

        // ============================================================== cancel

        [Test]
        public void Cancel_RunningJob_WritesCancelledRequest()
        {
            var started = Dispatch(StartParams("occlusion"));
            _occlusionRunning = true;

            var result = Dispatch(CancelParams(JobId(started)));
            Assert.AreEqual("cancelled", Status(result));
            StringAssert.Contains("cancel", result.TryGetProperty("detail", out var d) ? d.AsString() : "");
            Assert.IsFalse(_occlusionRunning); // the real cancel action actually ran
        }

        [Test]
        public void Cancel_WhileAnUnownedBakeRuns_Refused()
        {
            Dispatch(StartParams("lighting"));
            _lightingRunning = false; // our own job is not running

            BakeReloadEpoch.Bump(); // now unattributable
            _lightingRunning = true; // something IS running, but it is not (confirmably) ours

            var ex = Assert.Throws<InvalidOperationException>(() => Dispatch(CancelParams()));
            StringAssert.Contains("did not start", ex.Message);
        }

        [Test]
        public void Cancel_Navmesh_AfterReload_Refuses_NeverSilentlyNoOps()
        {
            // DESIGN-06 §10.18.
            var started = Dispatch(StartParams("navmesh"));
            _navMeshResolve = _ => (RunningState.Running, null);

            BakeReloadEpoch.Bump();

            var ex = Assert.Throws<InvalidOperationException>(() => Dispatch(CancelParams(JobId(started))));
            StringAssert.Contains("reloaded", ex.Message);
        }

        // ============================================================== navmesh (scoped predicate)

        [Test]
        public void NavMesh_ScopeRecorded_OneSurfaceStillBaking_ReturnsRunning()
        {
            _navMeshResolve = scope =>
            {
                Assert.AreEqual(new[] { "navmesh-surface-1" }, scope);
                return (RunningState.Running, null);
            };

            Dispatch(StartParams("navmesh"));
            var status = Dispatch(StatusParams());
            Assert.AreEqual("running", Status(status));
        }

        [Test]
        public void NavMesh_ProgressIsAlwaysNull()
        {
            _navMeshResolve = _ => (RunningState.Running, null);
            Dispatch(StartParams("navmesh"));

            var status = Dispatch(StatusParams());
            Assert.IsFalse(status.TryGetProperty("progress", out _));
        }

        [Test]
        public void NavMesh_ResolveFailure_ReturnsIndeterminate_NeverACompletionSignal()
        {
            _navMeshResolve = scope => (RunningState.Running, null); // running at start time so the watchdog latches _sawRunning
            Dispatch(StartParams("navmesh"));
            BakeWatchdog.Tick();

            // The recorded surface stops resolving (deleted / scene closed) - three-valued predicate,
            // never collapsed to "not running".
            _navMeshResolve = scope => (RunningState.Indeterminate, scope.Length > 0 ? scope[0] : null);
            BakeWatchdog.Tick(); // must NOT write 'complete' from this transition

            var status = Dispatch(StatusParams());
            Assert.AreEqual("indeterminate", Status(status));
            StringAssert.Contains("navmesh-surface-1", status.TryGetProperty("detail", out var d) ? d.AsString() : "");
        }

        // ============================================================== capability probe (execution-free)

        [Test]
        public void CapabilityProbe_Compute_ReturnsAllThreeBakeIds_ExecutionFree()
        {
            var capabilities = CapabilityProbe.Compute();
            Assert.AreEqual(3, capabilities.Count);
            CollectionAssert.Contains(new System.Collections.Generic.List<string> { capabilities[0].Id, capabilities[1].Id, capabilities[2].Id },
                "bake.lighting");
            Assert.IsTrue(capabilities[0].Available); // bake.lighting - built-in
            Assert.IsTrue(capabilities[1].Available); // bake.occlusion - built-in
        }
    }
}
