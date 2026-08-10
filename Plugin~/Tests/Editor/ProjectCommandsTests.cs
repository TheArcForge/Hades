// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Collections.Generic;
using System.IO;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// The class-2 project-level tools (see the "52 Editor tools" plan's operation-class table):
    /// scene.open, project.recompile_scripts, project.run_tests, hades.regression_replay - plus
    /// CommandTable's own "assets.refresh" handler, now wrapped in the same LeaseScope.Run. Same
    /// lease-invariant convention as PrefabCommandsTests/AssetCommandsTests (see
    /// PrefabCommandsTests' own class doc comment for the full rationale of
    /// <see cref="AssertLeaseCleanlyReleased"/>).
    ///
    /// project.recompile_scripts and project.run_tests get their own dedicated ORDER tests: each
    /// swaps ProjectCommands' public TriggerRecompile/StartTestRun seam for a fake that appends to
    /// a shared list, alongside an IEditorLockApi that does the same on Lock/Unlock, so "release
    /// happens before the trigger" is a deterministic assertion on that list's contents rather than
    /// an inference from timing. Both seams are reassigned in a try/finally that restores the
    /// original - static, process-wide state that must never leak into another test or (for
    /// StartTestRun) into a real Editor session.
    ///
    /// Also covers hades.regression_record (class 3, like BeginScriptEditing/EndScriptEditing -
    /// see ScriptEditingSessionTests for those - but placed here, alongside hades.regression_replay,
    /// rather than in that file: unlike Begin/End, recording never touches ReloadGate at all - see
    /// RegressionRecordStart/Stop's own doc comment for why a session that only ever risks leaking
    /// memory, never Unity's reload lock, does not need or want to share the gate with class 2's
    /// operations. CloseAnyLeakedRecordingSession mirrors PrefabCommandsTests'
    /// CloseAnyLeakedEditingSession: ProjectCommands' recording state is a static field with no
    /// InternalsVisibleTo escape hatch into this test assembly, so a session left open by a failed
    /// prior test is closed the same way a real caller would, through the public CommandTable.
    ///
    /// Also covers two of Plan 9 Task 5's class 4 (live-state reads - no lease at all) handlers:
    /// project.get_console_log (backed by <see cref="ConsoleLogBuffer"/>) and
    /// project.get_test_results (backed by <see cref="TestRunResultStore"/>, reconciled against
    /// project.run_tests' own runId - see "project.run_tests &lt;-&gt; project.get_test_results
    /// reconciliation" below). Both are static, process-wide state with the same no-
    /// InternalsVisibleTo constraint as recording above, so SetUp/TearDown reset them through their
    /// own public Clear()/Reset() seams - see each class's own doc comment (Tools/ProjectCommands.cs)
    /// for why plain static fields are safe for one (every caller is a command handler, guaranteed
    /// main-thread by MainThreadPump) but not the other (a log can arrive off-thread).
    /// </summary>
    [TestFixture]
    public sealed class ProjectCommandsTests
    {
        const string ScratchDir = "Assets/Tests/_HadesProjectScratch";

        [SetUp]
        public void SetUp()
        {
            SceneTestFixtures.ResetScene();
            CloseAnyLeakedRecordingSession();
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
            AssetDatabase.CreateFolder("Assets/Tests", "_HadesProjectScratch");

            // Class 4 (live-state reads, Plan 9 Task 5) support state - both are static/process-wide
            // (see their own class doc comments for why), so a prior test - in this fixture or,
            // for ConsoleLogBuffer, anywhere else in this same batchmode run - must never leak into
            // the next one.
            ConsoleLogBuffer.Clear();
            TestRunResultStore.Reset();
            TestRunResultStore.ResultsPath = Path.Combine(Path.GetTempPath(), "hades-test-results-" + Guid.NewGuid().ToString("N") + ".xml");
        }

        [TearDown]
        public void TearDown()
        {
            CloseAnyLeakedRecordingSession();
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);

            ConsoleLogBuffer.Clear();
            TestRunResultStore.Reset();
            if (File.Exists(TestRunResultStore.ResultsPath)) File.Delete(TestRunResultStore.ResultsPath);
            TestRunResultStore.ResultsPath = Path.Combine(Application.persistentDataPath, "TestResults.xml");
        }

        /// <summary>hades.regression_record_stop is unconditionally idempotent (see its own doc
        /// comment) - unlike PrefabCommandsTests' equivalent helper, no try/catch is needed
        /// here.</summary>
        static void CloseAnyLeakedRecordingSession()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            CommandTable.Dispatch(gate, Request("hades.regression_record_stop", JsonValue.NewObject()));
        }

        static JsonRpcRequest Request(string method, JsonValue @params) =>
            new JsonRpcRequest { Id = JsonValue.Integer(1), Method = method, Params = @params };

        static (ReloadGate gate, FakeEditorLockApi fake, MainThreadPump pump) NoopGateParts()
        {
            var fake = new FakeEditorLockApi();
            var pump = new MainThreadPump();
            var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            return (gate, fake, pump);
        }

        /// <summary>Same helper as PrefabCommandsTests/AssetCommandsTests - see either's own doc
        /// comment for the full rationale (balance, not an exact call count).</summary>
        static void AssertLeaseCleanlyReleased(FakeEditorLockApi fake, ReloadGate gate)
        {
            Assert.IsFalse(gate.IsHeld, "a class-2 project operation must never leave a lease held");
            Assert.GreaterOrEqual(fake.LockCalls, 1, "expected at least one Lock across the call(s) so far");
            Assert.AreEqual(fake.LockCalls, fake.UnlockCalls, "every Lock must be balanced by exactly one Unlock - no leaked lease");
            Assert.AreEqual(0, fake.Counter, "the fake's signed counter must land back at 0");
        }

        /// <summary>Unlike <see cref="AssertLeaseCleanlyReleased"/> (class 2: acquires, then
        /// releases), a class-4 live-state read - project.get_console_log, project.get_test_results
        /// - must never touch the gate AT ALL, the same as every class-1 mutation's own
        /// AssertNeverTouchedLease (see e.g. InspectorCommandsTests).</summary>
        static void AssertNeverTouchedLease(FakeEditorLockApi fake, ReloadGate gate)
        {
            Assert.AreEqual(0, fake.LockCalls, "a class-4 live-state read must never call Lock");
            Assert.AreEqual(0, fake.UnlockCalls, "a class-4 live-state read must never call Unlock");
            Assert.IsFalse(gate.IsHeld, "a class-4 live-state read must never leave a lease held");
        }

        static string StringProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.String ? v.AsString() : null;

        static bool BoolProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.Boolean && v.AsBoolean();

        /// <summary>Appends to a shared, test-owned list on both Lock and Unlock - used (alongside
        /// a swapped TriggerRecompile/StartTestRun that appends to the SAME list) to prove release
        /// happens strictly before the recompile/test-run trigger, as a plain ordered sequence
        /// rather than a timing inference.</summary>
        sealed class OrderTrackingLockApi : IEditorLockApi
        {
            readonly List<string> _order;
            public OrderTrackingLockApi(List<string> order) => _order = order;
            public void Lock() => _order.Add("lock");
            public void Unlock() => _order.Add("unlock");
        }

        // ---------------------------------------------------------------------------- scene.open

        [Test]
        public void SceneOpen_OpensExistingScene_ReportsSingleMode_LeaseCleanlyReleased()
        {
            var scenePath = ScratchDir + "/ToOpen.unity";
            var freshScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            EditorSceneManager.SaveScene(freshScene, scenePath);
            EditorSceneManager.CloseScene(freshScene, true);
            SceneTestFixtures.ResetScene(); // back to the scratch scene, as if the caller had something else open

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(scenePath));
                var result = CommandTable.Dispatch(gate, Request("scene.open", @params));

                Assert.AreEqual(scenePath, StringProp(result, "opened"));
                Assert.AreEqual("Single", StringProp(result, "mode"));
                Assert.IsTrue(BoolProp(result, "isLoaded"));
                Assert.AreEqual(scenePath, UnityEngine.SceneManagement.SceneManager.GetActiveScene().path);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        [Test]
        public void SceneOpen_Additive_UsesAdditiveMode_LeaseCleanlyReleased()
        {
            var scenePath = ScratchDir + "/Additive.unity";
            var freshScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            EditorSceneManager.SaveScene(freshScene, scenePath);
            EditorSceneManager.CloseScene(freshScene, true);
            SceneTestFixtures.ResetScene();

            var scenesBefore = EditorSceneManager.sceneCount;

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("path", JsonValue.String(scenePath))
                    .SetProperty("additive", JsonValue.Bool(true));
                var result = CommandTable.Dispatch(gate, Request("scene.open", @params));

                Assert.AreEqual("Additive", StringProp(result, "mode"));
                Assert.AreEqual(scenesBefore + 1, EditorSceneManager.sceneCount);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        [Test]
        public void SceneOpen_UnknownScene_ThrowsActionableError_StillReleasesLease()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(ScratchDir + "/Ghost.unity"));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("scene.open", @params)));
                StringAssert.Contains("Ghost.unity", ex.Message);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        // ---------------------------------------------------------------- project.recompile_scripts

        [Test]
        public void RecompileScripts_ReleasesLeaseBeforeRefreshingBeforeTriggeringRecompile_OrderProven()
        {
            var order = new List<string>();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new OrderTrackingLockApi(order), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var originalTrigger = ProjectCommands.TriggerRecompile;
            var originalRefresh = ProjectCommands.RefreshAssets;
            ProjectCommands.TriggerRecompile = () => order.Add("recompile");
            ProjectCommands.RefreshAssets = () => order.Add("refresh");
            try
            {
                var result = CommandTable.Dispatch(gate, Request("project.recompile_scripts", JsonValue.NewObject()));
                Assert.IsTrue(BoolProp(result, "requested"));
            }
            finally
            {
                ProjectCommands.TriggerRecompile = originalTrigger;
                ProjectCommands.RefreshAssets = originalRefresh;
            }

            CollectionAssert.AreEqual(new[] { "lock", "unlock", "refresh", "recompile" }, order,
                "release must happen strictly BEFORE refresh, which must happen strictly BEFORE the recompile trigger - "
                + "holding the lease across either would block the very reload they are asking for, and a brand-new "
                + "file must be imported (refresh) before compilation of it is requested (recompile) - mutation-tool-defects.md #1");
            Assert.IsFalse(gate.IsHeld);
        }

        [Test]
        public void RecompileScripts_LeaseBusyElsewhere_ThrowsActionableError_NeverRefreshesOrTriggersRecompile()
        {
            var order = new List<string>();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            gate.Acquire("someone-else", TimeSpan.FromMinutes(5));

            var originalTrigger = ProjectCommands.TriggerRecompile;
            var originalRefresh = ProjectCommands.RefreshAssets;
            ProjectCommands.TriggerRecompile = () => order.Add("recompile");
            ProjectCommands.RefreshAssets = () => order.Add("refresh");
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    CommandTable.Dispatch(gate, Request("project.recompile_scripts", JsonValue.NewObject())));
                StringAssert.Contains("someone-else", ex.Message);
            }
            finally
            {
                ProjectCommands.TriggerRecompile = originalTrigger;
                ProjectCommands.RefreshAssets = originalRefresh;
            }

            Assert.IsEmpty(order, "must never refresh or trigger recompile when the lease could not be acquired at all");
            Assert.IsTrue(gate.IsHeld);
            Assert.AreEqual("someone-else", gate.CurrentLeaseId);
            gate.Release("someone-else");
        }

        // ---------------------------------------------------------------------- project.run_tests

        [Test]
        public void RunTests_ReleasesLeaseBeforeStartingRun_ReturnsHandleImmediately_OrderProven()
        {
            var order = new List<string>();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new OrderTrackingLockApi(order), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var original = ProjectCommands.StartTestRun;
            ProjectCommands.StartTestRun = (runId, mode, filter) => { order.Add("start:" + mode); return (true, null); };
            try
            {
                var @params = JsonValue.NewObject().SetProperty("testMode", JsonValue.String("EditMode"));
                var result = CommandTable.Dispatch(gate, Request("project.run_tests", @params));

                Assert.AreEqual("started", StringProp(result, "status"));
                Assert.IsNotEmpty(StringProp(result, "runId"));
                Assert.AreEqual("EditMode", StringProp(result, "testMode"));
                Assert.IsFalse(result.TryGetProperty("error", out _));
            }
            finally
            {
                ProjectCommands.StartTestRun = original;
            }

            CollectionAssert.AreEqual(new[] { "lock", "unlock", "start:EditMode" }, order,
                "release must happen strictly BEFORE starting the run - EditMode triggers its own domain reload, which a still-held lease would block");
            Assert.IsFalse(gate.IsHeld);
        }

        [Test]
        public void RunTests_DefaultsToEditModeAndAllFilter_WhenOmitted()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var original = ProjectCommands.StartTestRun;
                string capturedMode = null;
                string capturedFilter = "unset";
                ProjectCommands.StartTestRun = (runId, mode, filter) => { capturedMode = mode; capturedFilter = filter; return (true, null); };
                try
                {
                    var result = CommandTable.Dispatch(gate, Request("project.run_tests", JsonValue.NewObject()));
                    Assert.AreEqual("EditMode", StringProp(result, "testMode"));
                    Assert.IsFalse(result.TryGetProperty("filter", out var f) && f.Kind == JsonValueKind.String);
                }
                finally
                {
                    ProjectCommands.StartTestRun = original;
                }

                Assert.AreEqual("EditMode", capturedMode);
                Assert.IsNull(capturedFilter);
                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        [Test]
        public void RunTests_WhenStartFails_ReportsFailedStatusAndError_LeaseStillCleanlyReleased()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var original = ProjectCommands.StartTestRun;
                ProjectCommands.StartTestRun = (runId, mode, filter) => (false, "Test Framework package not installed.");
                try
                {
                    var result = CommandTable.Dispatch(gate, Request("project.run_tests", JsonValue.NewObject()));
                    Assert.AreEqual("failed", StringProp(result, "status"));
                    Assert.AreEqual("Test Framework package not installed.", StringProp(result, "error"));
                }
                finally
                {
                    ProjectCommands.StartTestRun = original;
                }

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        // ---------------------------------------------------------------- project.run_tests <-> project.get_test_results reconciliation

        [Test]
        public void RunTests_OnSuccessfulStart_MarksTestRunResultStoreWithTheSameRunId()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var original = ProjectCommands.StartTestRun;
                ProjectCommands.StartTestRun = (runId, mode, filter) => (true, null);
                try
                {
                    var result = CommandTable.Dispatch(gate, Request("project.run_tests", JsonValue.NewObject()));
                    var runId = StringProp(result, "runId");

                    Assert.IsTrue(TestRunResultStore.HasStarted);
                    Assert.AreEqual(runId, TestRunResultStore.CurrentRunId);
                }
                finally
                {
                    ProjectCommands.StartTestRun = original;
                }
            }
        }

        [Test]
        public void RunTests_WhenStartFails_NeverMarksTestRunResultStoreAsStarted()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var original = ProjectCommands.StartTestRun;
                ProjectCommands.StartTestRun = (runId, mode, filter) => (false, "Test Framework package not installed.");
                try
                {
                    CommandTable.Dispatch(gate, Request("project.run_tests", JsonValue.NewObject()));

                    Assert.IsFalse(TestRunResultStore.HasStarted,
                        "a run that never started must not leave project_get_test_results reporting 'running' forever");
                }
                finally
                {
                    ProjectCommands.StartTestRun = original;
                }
            }
        }

        [Test]
        public void TestRunnerBridge_TypesAreResolvable_InThisTestEnvironment()
        {
            // Execution-free: only proves the reflection LOOKUP finds the real Test Runner API
            // types - this test assembly itself references UnityEditor.TestRunner (see
            // Hades.Tests.Editor.asmdef), so they are guaranteed loaded here. Never calls TryStart
            // for real - see ProjectCommands.StartTestRun's own doc comment for why that would be
            // an unsupported, recursive Test Runner invocation from inside this very test run.
            Assert.IsTrue(TestRunnerBridge.TypesAreResolvable());
        }

        // ---------------------------------------------------------------- hades.regression_replay

        [Test]
        public void RegressionReplay_MixOfPassingAndFailingCalls_ReportsPerEntryResults_NoOuterLeaseTaken()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var calls = JsonValue.NewArray()
                    // class-1, passes: no lease anywhere in this entry.
                    .Add(JsonValue.NewObject()
                        .SetProperty("method", JsonValue.String("scene.create_gameobject"))
                        .SetProperty("params", JsonValue.NewObject().SetProperty("name", JsonValue.String("ReplayedGO"))))
                    // unknown method: caught per-entry, does not abort the batch.
                    .Add(JsonValue.NewObject().SetProperty("method", JsonValue.String("not.a.real.method")));

                var @params = JsonValue.NewObject().SetProperty("calls", calls);
                var result = CommandTable.Dispatch(gate, Request("hades.regression_replay", @params));

                Assert.AreEqual(2, result.TryGetProperty("total", out var t) ? t.AsInteger() : -1);
                Assert.AreEqual(1, result.TryGetProperty("passed", out var p) ? p.AsInteger() : -1);
                Assert.AreEqual(1, result.TryGetProperty("failed", out var f) ? f.AsInteger() : -1);
                Assert.IsNotNull(GameObject.Find("ReplayedGO"));

                Assert.IsTrue(result.TryGetProperty("results", out var results) && results.Kind == JsonValueKind.Array && results.Items.Count == 2);
                Assert.IsTrue(results.Items[0].TryGetProperty("passed", out var passed0) && passed0.AsBoolean());
                Assert.IsTrue(results.Items[1].TryGetProperty("passed", out var passed1) && !passed1.AsBoolean());
                Assert.IsTrue(results.Items[1].TryGetProperty("error", out _));

                // This handler takes NO lease of its own - see RegressionReplay's own doc comment.
                // The one nested entry was class-1 (no lease), so nothing was ever locked.
                Assert.AreEqual(0, fake.LockCalls);
                Assert.AreEqual(0, fake.UnlockCalls);
                Assert.IsFalse(gate.IsHeld);
            }
        }

        [Test]
        public void RegressionReplay_NestedClassTwoCall_AcquiresAndReleasesItsOwnLease()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var go = new GameObject("ToSaveAsPrefab");
                var assetPath = ScratchDir + "/Replayed.prefab";

                var calls = JsonValue.NewArray().Add(JsonValue.NewObject()
                    .SetProperty("method", JsonValue.String("prefab.create"))
                    .SetProperty("params", JsonValue.NewObject()
                        .SetProperty("gameObjectPath", JsonValue.String("ToSaveAsPrefab"))
                        .SetProperty("assetPath", JsonValue.String(assetPath))));

                var @params = JsonValue.NewObject().SetProperty("calls", calls);
                var result = CommandTable.Dispatch(gate, Request("hades.regression_replay", @params));

                Assert.AreEqual(1, result.TryGetProperty("passed", out var p) ? p.AsInteger() : -1);
                Assert.IsTrue(AssetDatabase.LoadAssetAtPath<GameObject>(assetPath) != null);

                // The NESTED prefab.create call acquired and released its own lease exactly once -
                // this outer replay handler never took one of its own.
                Assert.AreEqual(1, fake.LockCalls);
                Assert.AreEqual(1, fake.UnlockCalls);
                Assert.IsFalse(gate.IsHeld);
                Assert.AreEqual(0, fake.Counter);
            }
        }

        [Test]
        public void RegressionReplay_ExpectedValueSupplied_ComparesAndReportsMismatch()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var calls = JsonValue.NewArray().Add(JsonValue.NewObject()
                    .SetProperty("method", JsonValue.String("scene.create_gameobject"))
                    .SetProperty("params", JsonValue.NewObject().SetProperty("name", JsonValue.String("Mismatch")))
                    .SetProperty("expected", JsonValue.NewObject().SetProperty("name", JsonValue.String("SomethingElse"))));

                var result = CommandTable.Dispatch(gate, Request("hades.regression_replay", JsonValue.NewObject().SetProperty("calls", calls)));

                Assert.AreEqual(0, result.TryGetProperty("passed", out var p) ? p.AsInteger() : -1);
                Assert.AreEqual(1, result.TryGetProperty("failed", out var f) ? f.AsInteger() : -1);
            }
        }

        [Test]
        public void RegressionReplay_EmptyCallsArray_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var ex = Assert.Throws<ArgumentException>(() =>
                CommandTable.Dispatch(gate, Request("hades.regression_replay", JsonValue.NewObject().SetProperty("calls", JsonValue.NewArray()))));

            StringAssert.Contains("calls", ex.Message);
        }

        // ---------------------------------------------------------------------------- assets.refresh

        [Test]
        public void AssetsRefresh_NowClass2_AcquiresAndReleasesLease()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("assets.refresh", JsonValue.NewObject()));

                Assert.IsTrue(BoolProp(result, "refreshed"));

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        // ---------------------------------------------------------------- hades.regression_record

        static long IntProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.Integer ? v.AsInteger() : -1;

        [Test]
        public void RegressionRecordStart_ThenStopImmediately_ReturnsEmptyCallsArray_NoLeaseTaken()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var startResult = CommandTable.Dispatch(gate, Request("hades.regression_record_start", JsonValue.NewObject()));
                Assert.IsTrue(BoolProp(startResult, "recording"));

                var stopResult = CommandTable.Dispatch(gate, Request("hades.regression_record_stop", JsonValue.NewObject()));
                Assert.AreEqual(0, IntProp(stopResult, "count"));
                Assert.IsTrue(stopResult.TryGetProperty("calls", out var calls) && calls.Kind == JsonValueKind.Array && calls.Items.Count == 0);

                // Recording never touches ReloadGate at all - see RegressionRecordStart/Stop's own
                // doc comment for why it must not share the gate with class 2's LeaseScope.Run.
                Assert.AreEqual(0, fake.LockCalls);
                Assert.AreEqual(0, fake.UnlockCalls);
            }
        }

        [Test]
        public void RegressionRecordStop_WithoutStart_IsIdempotent_ReturnsEmptyCallsArray()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var stopResult = CommandTable.Dispatch(gate, Request("hades.regression_record_stop", JsonValue.NewObject()));

                Assert.AreEqual(0, IntProp(stopResult, "count"));
                Assert.IsTrue(stopResult.TryGetProperty("calls", out var calls) && calls.Kind == JsonValueKind.Array && calls.Items.Count == 0);
            }
        }

        [Test]
        public void RegressionRecordStart_WhileAlreadyActive_ThrowsActionableError()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("hades.regression_record_start", JsonValue.NewObject()));

                var ex = Assert.Throws<InvalidOperationException>(() =>
                    CommandTable.Dispatch(gate, Request("hades.regression_record_start", JsonValue.NewObject())));
                StringAssert.Contains("stop", ex.Message);

                CommandTable.Dispatch(gate, Request("hades.regression_record_stop", JsonValue.NewObject())); // cleanup
            }
        }

        [Test]
        public void RegressionRecordStart_CapturesSubsequentDispatchedCalls_ExcludingItsOwnStartStop()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("hades.regression_record_start", JsonValue.NewObject()));

                CommandTable.Dispatch(gate, Request("scene.create_gameobject",
                    JsonValue.NewObject().SetProperty("name", JsonValue.String("RecordedOne"))));
                CommandTable.Dispatch(gate, Request("scene.create_gameobject",
                    JsonValue.NewObject().SetProperty("name", JsonValue.String("RecordedTwo"))));

                var stopResult = CommandTable.Dispatch(gate, Request("hades.regression_record_stop", JsonValue.NewObject()));

                Assert.AreEqual(2, IntProp(stopResult, "count"),
                    "must capture exactly the two dispatched calls - never its own start/stop control calls");
                Assert.IsTrue(stopResult.TryGetProperty("calls", out var calls) && calls.Kind == JsonValueKind.Array);
                Assert.AreEqual(2, calls.Items.Count);

                Assert.IsTrue(calls.Items[0].TryGetProperty("method", out var m0) && m0.AsString() == "scene.create_gameobject");
                Assert.IsTrue(calls.Items[0].TryGetProperty("params", out var p0) && p0.Kind == JsonValueKind.Object);
                Assert.IsTrue(p0.TryGetProperty("name", out var n0) && n0.AsString() == "RecordedOne");
                Assert.IsTrue(calls.Items[0].TryGetProperty("expected", out _),
                    "captured entries must include the ACTUAL result as 'expected', for replay to compare against later");
            }
        }

        [Test]
        public void RegressionRecordStopOutput_IsDirectlyAcceptedByRegressionReplayAsCallsInput()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("hades.regression_record_start", JsonValue.NewObject()));
                CommandTable.Dispatch(gate, Request("scene.create_gameobject",
                    JsonValue.NewObject().SetProperty("name", JsonValue.String("RoundTrip"))));
                var stopResult = CommandTable.Dispatch(gate, Request("hades.regression_record_stop", JsonValue.NewObject()));
                Assert.IsTrue(stopResult.TryGetProperty("calls", out var recordedCalls));

                // "make record's storage and replay's input agree": the exact array
                // hades.regression_record_stop returned, handed to hades.regression_replay's
                // 'calls' parameter completely unchanged - no translation step, no dataset-by-id
                // lookup in between. See RegressionRecordStart/Stop's own doc comment.
                var replayParams = JsonValue.NewObject().SetProperty("calls", recordedCalls);
                var replayResult = CommandTable.Dispatch(gate, Request("hades.regression_replay", replayParams));

                Assert.AreEqual(1, IntProp(replayResult, "total"),
                    "hades.regression_replay must accept record's own output as valid 'calls' input with no shape mismatch");
            }
        }

        [Test]
        public void RegressionRecordSession_ExpiresAfterTtlOfSilence_UsingInjectedClock_DiscardsCapturedCalls()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var original = ProjectCommands.RecordingClock;
                ProjectCommands.RecordingClock = () => clock;
                try
                {
                    CommandTable.Dispatch(gate, Request("hades.regression_record_start", JsonValue.NewObject()));
                    CommandTable.Dispatch(gate, Request("scene.create_gameobject",
                        JsonValue.NewObject().SetProperty("name", JsonValue.String("WillBeDiscarded"))));

                    clock += TimeSpan.FromMinutes(11); // past the recording session's own TTL, no further activity

                    var stopResult = CommandTable.Dispatch(gate, Request("hades.regression_record_stop", JsonValue.NewObject()));
                    Assert.AreEqual(0, IntProp(stopResult, "count"),
                        "a recording session left silent past its own TTL must be discarded, not returned stale");
                }
                finally
                {
                    ProjectCommands.RecordingClock = original;
                }
            }
        }

        // ---------------------------------------------------------------- project.get_console_log

        [Test]
        public void GetConsoleLog_ReturnsCapturedEntries_ChronologicalOrder_WithTypeMessageAndStack_NoLeaseTouched()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                // NoopGateParts' own ReloadGate construction just logged its "BOOT reconcile" trace
                // (see ReloadGate.Trace) through this SAME real, always-installed ConsoleLogBuffer
                // subscription - wipe it so only the three entries below are present for the
                // assertions that follow.
                ConsoleLogBuffer.Clear();

                ConsoleLogBuffer.Capture(LogType.Log, "first", "stack-1");
                ConsoleLogBuffer.Capture(LogType.Warning, "second", "stack-2");
                ConsoleLogBuffer.Capture(LogType.Error, "third", "stack-3");

                var result = CommandTable.Dispatch(gate, Request("project.get_console_log", JsonValue.NewObject()));

                Assert.AreEqual(3, IntProp(result, "count"));
                Assert.AreEqual(3, IntProp(result, "totalBuffered"));
                Assert.IsTrue(result.TryGetProperty("entries", out var entries) && entries.Kind == JsonValueKind.Array);
                Assert.AreEqual(3, entries.Items.Count);

                Assert.AreEqual("Log", StringProp(entries.Items[0], "type"));
                Assert.AreEqual("first", StringProp(entries.Items[0], "message"));
                Assert.AreEqual("stack-1", StringProp(entries.Items[0], "stackTrace"));
                Assert.AreEqual("Warning", StringProp(entries.Items[1], "type"));
                Assert.AreEqual("second", StringProp(entries.Items[1], "message"));
                Assert.AreEqual("Error", StringProp(entries.Items[2], "type"));
                Assert.AreEqual("third", StringProp(entries.Items[2], "message"));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void GetConsoleLog_FiltersAcrossWholeBufferBeforeTakingCount_NotJustTheLastCountEntries()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                ConsoleLogBuffer.Clear(); // see GetConsoleLog_ReturnsCapturedEntries... for why

                // An Error sandwiched between four Logs: a "last N total, THEN filter"
                // implementation would return nothing for count=1/type=Error (the last raw entry
                // is a Log) - this proves the filter is applied across the WHOLE buffer first.
                ConsoleLogBuffer.Capture(LogType.Log, "log-1", "");
                ConsoleLogBuffer.Capture(LogType.Log, "log-2", "");
                ConsoleLogBuffer.Capture(LogType.Error, "the-error", "");
                ConsoleLogBuffer.Capture(LogType.Log, "log-3", "");
                ConsoleLogBuffer.Capture(LogType.Log, "log-4", "");

                var @params = JsonValue.NewObject().SetProperty("count", JsonValue.Integer(1)).SetProperty("type", JsonValue.String("Error"));
                var result = CommandTable.Dispatch(gate, Request("project.get_console_log", @params));

                Assert.AreEqual(1, IntProp(result, "count"));
                Assert.AreEqual(5, IntProp(result, "totalBuffered"), "totalBuffered reports the WHOLE buffer, independent of 'type'/'count'");
                Assert.IsTrue(result.TryGetProperty("entries", out var entries) && entries.Items.Count == 1);
                Assert.AreEqual("the-error", StringProp(entries.Items[0], "message"));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void GetConsoleLog_DefaultsCountTo50_ClampsAboveMax200AndBelowMin1()
        {
            for (var i = 0; i < 205; i++) ConsoleLogBuffer.Capture(LogType.Log, "msg-" + i, "");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var defaultResult = CommandTable.Dispatch(gate, Request("project.get_console_log", JsonValue.NewObject()));
                Assert.AreEqual(50, IntProp(defaultResult, "count"), "omitted 'count' must default to 50");

                var hugeResult = CommandTable.Dispatch(gate, Request("project.get_console_log",
                    JsonValue.NewObject().SetProperty("count", JsonValue.Integer(9999))));
                Assert.AreEqual(200, IntProp(hugeResult, "count"), "'count' above 200 must clamp to 200 (the buffer's own capacity)");

                var zeroResult = CommandTable.Dispatch(gate, Request("project.get_console_log",
                    JsonValue.NewObject().SetProperty("count", JsonValue.Integer(0))));
                Assert.AreEqual(1, IntProp(zeroResult, "count"), "'count' of 0 (or negative) must clamp up to 1, not return empty");

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void GetConsoleLog_BufferBounded_DropsOldestBeyondCapacity_NoLeaseTouched()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                ConsoleLogBuffer.Clear(); // see GetConsoleLog_ReturnsCapturedEntries... for why

                var total = ConsoleLogBuffer.Capacity + 10;
                for (var i = 0; i < total; i++) ConsoleLogBuffer.Capture(LogType.Log, "msg-" + i, "");

                var result = CommandTable.Dispatch(gate, Request("project.get_console_log",
                    JsonValue.NewObject().SetProperty("count", JsonValue.Integer(ConsoleLogBuffer.Capacity))));

                Assert.AreEqual(ConsoleLogBuffer.Capacity, IntProp(result, "totalBuffered"), "buffer must never grow past its own capacity");
                Assert.IsTrue(result.TryGetProperty("entries", out var entries) && entries.Items.Count == ConsoleLogBuffer.Capacity);

                // The oldest 10 (msg-0..msg-9) must have been dropped; the buffer's own oldest
                // surviving entry is msg-10, its newest is msg-(total-1).
                Assert.AreEqual("msg-10", StringProp(entries.Items[0], "message"));
                Assert.AreEqual("msg-" + (total - 1), StringProp(entries.Items[entries.Items.Count - 1], "message"));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void GetConsoleLog_InvalidTypeFilter_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("type", JsonValue.String("Eror"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("project.get_console_log", @params)));

            StringAssert.Contains("Eror", ex.Message);
            StringAssert.Contains("Error", ex.Message);
            StringAssert.Contains("Warning", ex.Message);
            StringAssert.Contains("Log", ex.Message);
        }

        [Test]
        public void GetConsoleLog_ConcurrentCaptureFromMultipleThreads_NoCorruption_NoLeaseTouched()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                ConsoleLogBuffer.Clear(); // see GetConsoleLog_ReturnsCapturedEntries... for why

                const int threadCount = 8;
                const int perThread = 100;

                var tasks = new System.Threading.Tasks.Task[threadCount];
                for (var t = 0; t < threadCount; t++)
                {
                    var threadIndex = t;
                    tasks[t] = System.Threading.Tasks.Task.Run(() =>
                    {
                        for (var i = 0; i < perThread; i++)
                            ConsoleLogBuffer.Capture(LogType.Log, "t" + threadIndex + "-" + i, "");
                    });
                }
                System.Threading.Tasks.Task.WaitAll(tasks);

                var result = CommandTable.Dispatch(gate, Request("project.get_console_log",
                    JsonValue.NewObject().SetProperty("count", JsonValue.Integer(ConsoleLogBuffer.Capacity))));

                Assert.AreEqual(ConsoleLogBuffer.Capacity, IntProp(result, "totalBuffered"),
                    threadCount * perThread + " concurrent captures must leave exactly Capacity entries buffered, never more, never corrupted into fewer");
                Assert.IsTrue(result.TryGetProperty("entries", out var entries) && entries.Items.Count == ConsoleLogBuffer.Capacity);

                foreach (var entry in entries.Items)
                    StringAssert.IsMatch(@"^t\d+-\d+$", StringProp(entry, "message"), "a corrupted concurrent write would produce a mangled message");

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void GetConsoleLog_RealDebugLogPipeline_IsCapturedByBootInstalledSubscription()
        {
            // Deliberately Debug.LogWarning, never LogError: Unity's own Test Runner fails a test
            // on an UNHANDLED LogType.Error/Assert/Exception (see UnityEngine.TestTools.LogAssert)
            // but not on Warning/Log, so this proves the REAL Application.logMessageReceivedThreaded
            // subscription - installed by HadesBoot's static constructor, which already ran long
            // before this test executed, and which THIS TEST NEVER CALLS ITSELF - actually flows
            // into ConsoleLogBuffer, without fighting that machinery.
            const string marker = "hades-real-log-pipeline-marker";
            Debug.LogWarning(marker);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("project.get_console_log",
                    JsonValue.NewObject().SetProperty("count", JsonValue.Integer(ConsoleLogBuffer.Capacity))));

                Assert.IsTrue(result.TryGetProperty("entries", out var entries) && entries.Kind == JsonValueKind.Array);

                var found = false;
                foreach (var entry in entries.Items)
                    if (StringProp(entry, "message") == marker) found = true;

                Assert.IsTrue(found, "a real Debug.LogWarning must reach ConsoleLogBuffer through the subscription HadesBoot installs at boot");

                AssertNeverTouchedLease(fake, gate);
            }
        }

        // ---------------------------------------------------------------- project.get_test_results

        const string FakeResultsXml =
            "<test-run total=\"3\" passed=\"2\" failed=\"1\" skipped=\"0\" inconclusive=\"0\" duration=\"1.234\">"
            + "<test-case fullname=\"MyNamespace.MyTests.TestA\" result=\"Passed\" />"
            + "<test-case fullname=\"MyNamespace.MyTests.TestB\" result=\"Passed\" />"
            + "<test-case fullname=\"MyNamespace.MyTests.TestC\" result=\"Failed\">"
            + "<failure><message>Expected true but was false</message></failure>"
            + "</test-case>"
            + "</test-run>";

        [Test]
        public void GetTestResults_NoRunEverStarted_ReportsNoneStatus_NoLeaseTouched()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("project.get_test_results", JsonValue.NewObject()));

                Assert.AreEqual("none", StringProp(result, "status"));
                Assert.IsFalse(string.IsNullOrEmpty(StringProp(result, "note")), "must explain what to do next, not return an empty result");

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void GetTestResults_StartedButResultsFileNotYetWritten_ReportsRunningPlainly_NoLeaseTouched()
        {
            TestRunResultStore.MarkStarted("run-in-progress");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("project.get_test_results", JsonValue.NewObject()));

                Assert.AreEqual("running", StringProp(result, "status"));
                Assert.AreEqual("run-in-progress", StringProp(result, "runId"));
                Assert.IsFalse(string.IsNullOrEmpty(StringProp(result, "note")));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void GetTestResults_ResultsFileWrittenAfterBaseline_ReportsCompleteWithCountsAndFailures()
        {
            TestRunResultStore.MarkStarted("run-complete");
            File.WriteAllText(TestRunResultStore.ResultsPath, FakeResultsXml);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("project.get_test_results", JsonValue.NewObject()));

                Assert.AreEqual("complete", StringProp(result, "status"));
                Assert.AreEqual("run-complete", StringProp(result, "runId"));
                Assert.AreEqual(3, IntProp(result, "total"));
                Assert.AreEqual(2, IntProp(result, "passed"));
                Assert.AreEqual(1, IntProp(result, "failed"));
                Assert.AreEqual(0, IntProp(result, "skipped"));
                Assert.AreEqual(0, IntProp(result, "inconclusive"));

                Assert.IsTrue(result.TryGetProperty("failures", out var failures) && failures.Kind == JsonValueKind.Array && failures.Items.Count == 1);
                Assert.AreEqual("MyNamespace.MyTests.TestC", StringProp(failures.Items[0], "name"));
                Assert.AreEqual("Expected true but was false", StringProp(failures.Items[0], "message"));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void GetTestResults_StaleResultsFileFromPriorRun_NotMistakenForCurrentRun()
        {
            // The file exists BEFORE MarkStarted's own baseline is taken - simulating a run that
            // finished earlier, whose output must not be handed back as THIS run's result.
            File.WriteAllText(TestRunResultStore.ResultsPath, FakeResultsXml);
            TestRunResultStore.MarkStarted("run-after-stale-file");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("project.get_test_results", JsonValue.NewObject()));

                Assert.AreEqual("running", StringProp(result, "status"),
                    "a results file older than this run's own baseline must never be mistaken for this run's output");
                Assert.AreEqual("run-after-stale-file", StringProp(result, "runId"));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void GetTestResults_UnknownRunId_ReportsUnknownStatus_NotAnEmptyResult()
        {
            TestRunResultStore.MarkStarted("the-real-run");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("runId", JsonValue.String("totally-different-run"));
                var result = CommandTable.Dispatch(gate, Request("project.get_test_results", @params));

                Assert.AreEqual("unknown", StringProp(result, "status"));
                Assert.AreEqual("totally-different-run", StringProp(result, "runId"));
                var note = StringProp(result, "note");
                Assert.IsFalse(string.IsNullOrEmpty(note), "'unknown' must explain itself, not return nothing");
                StringAssert.Contains("the-real-run", note);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void GetTestResults_OmittedRunId_DefaultsToMostRecentlyStartedRun()
        {
            TestRunResultStore.MarkStarted("most-recent-run");
            File.WriteAllText(TestRunResultStore.ResultsPath, FakeResultsXml);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("project.get_test_results", JsonValue.NewObject()));

                Assert.AreEqual("most-recent-run", StringProp(result, "runId"));
                Assert.AreEqual("complete", StringProp(result, "status"));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void GetTestResults_MalformedResultsXml_ThrowsActionableError()
        {
            TestRunResultStore.MarkStarted("run-bad-xml");
            File.WriteAllText(TestRunResultStore.ResultsPath, "this is not xml at all {{{");

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var ex = Assert.Throws<ArgumentException>(() =>
                CommandTable.Dispatch(gate, Request("project.get_test_results", JsonValue.NewObject())));

            StringAssert.Contains("run-bad-xml", ex.Message);
        }
    }
}
