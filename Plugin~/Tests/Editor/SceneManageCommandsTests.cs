// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Linq;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// scene.manage (Plan 10 Task 5's plugin-side half): one wire call carrying the WHOLE
    /// scene_manage 'operations' array, applied inside ONE CommandTable.Dispatch, spanning "save"/
    /// "create"/"duplicate" (class-1, from SceneManagementCommands) and "open" (class-2, from
    /// ProjectCommands). Like ProjectSettingsApplyCommandsTests/AssetManageCommandsTests, every test
    /// here proves the SAME headline lease property (<see cref="AssertExactlyOneLeaseWindow"/>), and
    /// <see cref="Create_DoesNotSwitchActiveScene"/> re-proves - at THIS batch dispatch's own entry
    /// point, not just at SceneManagementCommandsTests' direct-call level - the property Plan 9's own
    /// E2E found the hard way: creating a new scene asset must never discard whatever the caller
    /// currently has open (and possibly unsaved) in the Editor.
    /// </summary>
    [TestFixture]
    public sealed class SceneManageCommandsTests
    {
        const string ScratchDir = "Assets/Tests/_HadesSceneManageScratch";

        [SetUp]
        public void SetUp()
        {
            SceneTestFixtures.ResetScene();
            Undo.ClearAll();
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
            AssetDatabase.CreateFolder("Assets/Tests", "_HadesSceneManageScratch");
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
            // scene.manage's create/open/duplicate/save ops may have changed which scene is active -
            // reset once more so the NEXT test (in this file or another) starts from a known scene.
            SceneTestFixtures.ResetScene();
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

        /// <summary>Same property PrefabApplyCommandsTests/ProjectSettingsApplyCommandsTests/
        /// AssetManageCommandsTests' own helpers prove for their own batch tools - EXACTLY one
        /// Lock/Unlock pair for the WHOLE batch, regardless of how many operations it contains or
        /// which of the four ops (only "open" is individually lease-bound) each one is.</summary>
        static void AssertExactlyOneLeaseWindow(FakeEditorLockApi fake, ReloadGate gate)
        {
            Assert.AreEqual(1, fake.LockCalls, "scene.manage must acquire the reload lock EXACTLY ONCE for the whole batch, not once per operation");
            Assert.AreEqual(1, fake.UnlockCalls, "scene.manage must release the reload lock EXACTLY ONCE for the whole batch");
            Assert.IsFalse(gate.IsHeld, "scene.manage must never leave a lease held");
        }

        static JsonValue Op(string op, params (string Key, JsonValue Value)[] fields)
        {
            var o = JsonValue.NewObject().SetProperty("op", JsonValue.String(op));
            foreach (var (key, value) in fields) o.SetProperty(key, value);
            return o;
        }

        static JsonValue Params(params JsonValue[] operations)
        {
            var arr = JsonValue.NewArray();
            foreach (var op in operations) arr.Add(op);
            return JsonValue.NewObject().SetProperty("operations", arr);
        }

        static int[] AppliedIndices(JsonValue result) =>
            result.TryGetProperty("applied", out var a) && a!.Kind == JsonValueKind.Array
                ? a.Items.Select(x => (int)x.AsInteger()).ToArray()
                : Array.Empty<int>();

        static JsonValue FailedItems(JsonValue result) =>
            result.TryGetProperty("failed", out var f) && f!.Kind == JsonValueKind.Array ? f : JsonValue.NewArray();

        static JsonValue ResultsItems(JsonValue result) =>
            result.TryGetProperty("results", out var r) && r!.Kind == JsonValueKind.Array ? r : JsonValue.NewArray();

        static string Str(JsonValue obj, string key) =>
            obj.TryGetProperty(key, out var v) && v!.Kind == JsonValueKind.String ? v.AsString() : null;

        static string SaveAdditiveScratchScene(string fileName)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var path = ScratchDir + "/" + fileName;
            EditorSceneManager.SaveScene(scene, path);
            EditorSceneManager.CloseScene(scene, true);
            return path;
        }

        // ---------------------------------------------------------------- structural validation

        [Test]
        public void Apply_MissingOperationsArray_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var ex = Assert.Throws<ArgumentException>(() =>
                CommandTable.Dispatch(gate, Request("scene.manage", JsonValue.NewObject())));

            StringAssert.Contains("operations", ex.Message);
        }

        // ---------------------------------------------------------------- the headline property: create never switches scenes

        /// <summary>THE required behaviour Plan 9's own E2E found the hard way: OpenSceneMode.Single
        /// would silently discard a caller's unsaved open scene with no prompt in a scripted
        /// context. Proven here at scene.manage's own dispatch entry point (not merely inherited by
        /// reading SceneManagementCommands.CreateScene's source) - if scene_manage's "create" op
        /// were ever rerouted to a different implementation, THIS test would catch it.</summary>
        [Test]
        public void Create_DoesNotSwitchActiveScene()
        {
            var activeBefore = SceneManager.GetActiveScene().path;
            Assert.IsFalse(string.IsNullOrEmpty(activeBefore), "fixture scene must already have a path");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var newPath = ScratchDir + "/Created.unity";
                var result = CommandTable.Dispatch(gate, Request("scene.manage", Params(
                    Op("create", ("path", JsonValue.String(newPath))))));

                CollectionAssert.AreEqual(new[] { 0 }, AppliedIndices(result));
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(newPath), "the new scene file must exist on disk");
                Assert.AreEqual(activeBefore, SceneManager.GetActiveScene().path,
                    "scene_manage's 'create' op must not change which scene is currently open");

                AssertExactlyOneLeaseWindow(fake, gate);
            }
        }

        // ---------------------------------------------------------------- full op vocabulary sweep, one lease window

        /// <summary>One scene.manage call touching all four ops in an order where later ones act on
        /// what earlier ones just created - create a new scene, duplicate an existing fixture scene,
        /// open the just-created scene (switching the active scene for the FIRST time in this whole
        /// sequence), then save in place - with EXACTLY one lease window for the whole thing even
        /// though only "open" individually needs one.</summary>
        [Test]
        public void FullOperationSweep_AppliesEveryOpInOrder_OneLeaseWindow()
        {
            var duplicateSource = SaveAdditiveScratchScene("DupSource.unity");
            var createdPath = ScratchDir + "/Created.unity";
            var duplicateDest = ScratchDir + "/Duplicated.unity";

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("scene.manage", Params(
                    Op("create", ("path", JsonValue.String(createdPath))),
                    Op("duplicate", ("sourcePath", JsonValue.String(duplicateSource)), ("destPath", JsonValue.String(duplicateDest))),
                    Op("open", ("path", JsonValue.String(createdPath))),
                    Op("save"))));

                CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, AppliedIndices(result));
                Assert.AreEqual(0, FailedItems(result).Items.Count);

                // Verified via real project/Editor state, not by trusting the response.
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(createdPath));
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(duplicateDest));
                Assert.AreEqual(createdPath, SceneManager.GetActiveScene().path, "'open' must have switched the active scene");
                Assert.IsFalse(SceneManager.GetActiveScene().isDirty, "'save' must have persisted the now-active scene");

                var results = ResultsItems(result);
                Assert.AreEqual(4, results.Items.Count);
                var openResult = results.Items[2].TryGetProperty("result", out var or) ? or : null;
                Assert.AreEqual("Single", Str(openResult, "mode"));

                StringAssert.Contains("4", Str(result, "summary") ?? "");

                AssertExactlyOneLeaseWindow(fake, gate);
            }
        }

        // ---------------------------------------------------------------- unknown op

        [Test]
        public void UnknownOp_RecordedAsPerOperationFailure_BatchContinues_OneLeaseWindowStill()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var newPath = ScratchDir + "/UnknownOpCreated.unity";
                var result = CommandTable.Dispatch(gate, Request("scene.manage", Params(
                    Op("frobnicate"),
                    Op("create", ("path", JsonValue.String(newPath))))));

                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(newPath), "a later, valid op must still apply");
                CollectionAssert.AreEqual(new[] { 1 }, AppliedIndices(result));
                var failed = FailedItems(result);
                Assert.AreEqual(1, failed.Items.Count);
                Assert.AreEqual("frobnicate", Str(failed.Items[0], "op"));
                StringAssert.Contains("frobnicate", Str(failed.Items[0], "error"));

                AssertExactlyOneLeaseWindow(fake, gate);
            }
        }

        // ---------------------------------------------------------------- partial failure, no rollback

        [Test]
        public void PartialFailure_MiddleOperationFails_EarlierAppliedNotRolledBack_LaterStillAttempted()
        {
            var duplicateSource = SaveAdditiveScratchScene("PartialDupSource.unity");
            var createdPath = ScratchDir + "/PartialCreated.unity";
            var duplicateDest = ScratchDir + "/PartialDuplicated.unity";

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("scene.manage", Params(
                    Op("create", ("path", JsonValue.String(createdPath))),
                    Op("open", ("path", JsonValue.String(ScratchDir + "/Ghost.unity"))), // does not exist - fails
                    Op("duplicate", ("sourcePath", JsonValue.String(duplicateSource)), ("destPath", JsonValue.String(duplicateDest))))));

                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(createdPath), "op 0 must have applied, not rolled back by op 1's failure");
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(duplicateDest), "op 2 must still be attempted after op 1 fails");

                CollectionAssert.AreEqual(new[] { 0, 2 }, AppliedIndices(result));
                var failed = FailedItems(result);
                Assert.AreEqual(1, failed.Items.Count);
                Assert.AreEqual(1L, failed.Items[0].TryGetProperty("index", out var idx) ? idx.AsInteger() : -1);
                Assert.AreEqual("open", Str(failed.Items[0], "op"));
                StringAssert.Contains("Ghost.unity", Str(failed.Items[0], "error"));

                // The unified partial-batch shape: 'results' carries an entry for every APPLIED op
                // (by the same index 'applied' reports), never for the failed one - a caller can
                // learn which operations landed, which did not, and why from ONE response.
                var results = ResultsItems(result);
                Assert.AreEqual(2, results.Items.Count);
                Assert.AreEqual(0L, results.Items[0].TryGetProperty("index", out var rIdx0) ? rIdx0.AsInteger() : -1);
                Assert.AreEqual("create", Str(results.Items[0], "op"));
                Assert.AreEqual(2L, results.Items[1].TryGetProperty("index", out var rIdx1) ? rIdx1.AsInteger() : -1);
                Assert.AreEqual("duplicate", Str(results.Items[1], "op"));
                Assert.IsTrue(results.Items[0].TryGetProperty("result", out var r0) && r0 != null,
                    "each results entry must carry the op's own result payload, not just index/op");

                AssertExactlyOneLeaseWindow(fake, gate);
            }
        }

        // ---------------------------------------------------------------- one lease window even for an all-class-1 batch

        [Test]
        public void Apply_AcquiresLeaseExactlyOnce_EvenWhenBatchIsEntirelyClass1Ops()
        {
            // "save"/"create"/"duplicate" never individually touch the gate (see
            // SceneManagementCommands' own doc comment) - this proves scene.manage still wraps the
            // WHOLE batch in its own uniform lease window even when nothing in THIS PARTICULAR call
            // actually needed one (no "open" op here at all).
            var duplicateSource = SaveAdditiveScratchScene("Class1OnlyDupSource.unity");
            var createdPath = ScratchDir + "/Class1OnlyCreated.unity";
            var duplicateDest = ScratchDir + "/Class1OnlyDuplicated.unity";

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("scene.manage", Params(
                    Op("create", ("path", JsonValue.String(createdPath))),
                    Op("duplicate", ("sourcePath", JsonValue.String(duplicateSource)), ("destPath", JsonValue.String(duplicateDest))))));

                CollectionAssert.AreEqual(new[] { 0, 1 }, AppliedIndices(result));
                AssertExactlyOneLeaseWindow(fake, gate);
            }
        }
    }
}
