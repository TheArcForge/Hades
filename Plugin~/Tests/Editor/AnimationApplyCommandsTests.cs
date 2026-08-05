// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Linq;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// animation.apply (Plan 10 Task 2's plugin-side half): one wire call carrying the WHOLE
    /// animation_apply 'operations' array, applied inside ONE CommandTable.Dispatch - see
    /// SceneApplyCommandsTests' own doc comment for the headline property (one Undo.PerformUndo
    /// reverting the whole batch) this proves against a real Undo stack, and MaterialCommandsTests'
    /// own doc comment for why controllers/clips (assets) additionally need a scratch folder per
    /// test.
    ///
    /// <see cref="Apply_RegistersUndoAsOneGroup_PerformUndoRevertsEveryOperation"/> specifically
    /// includes an 'editController' op - the one op whose underlying AnimationCommands.EditController
    /// normally opens ITS OWN Undo group when dispatched standalone (animation.edit_controller is a
    /// real batch tool in its own right). This is the test that would catch a regression where
    /// AnimationApplyCommands accidentally called EditController instead of the group-free
    /// AnimationCommands.DoEditController - such a bug would split this batch into two Undo steps,
    /// and a single PerformUndo would leave the editController op's own effects in place.
    /// </summary>
    [TestFixture]
    public sealed class AnimationApplyCommandsTests
    {
        const string ScratchDir = "Assets/Tests/_HadesAnimationApplyScratch";

        [SetUp]
        public void SetUp()
        {
            SceneTestFixtures.ResetScene();
            Undo.ClearAll();
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
            AssetDatabase.CreateFolder("Assets/Tests", "_HadesAnimationApplyScratch");
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
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

        static void AssertNeverTouchedLease(FakeEditorLockApi fake, ReloadGate gate)
        {
            Assert.AreEqual(0, fake.LockCalls, "animation.apply must never call Lock");
            Assert.AreEqual(0, fake.UnlockCalls, "animation.apply must never call Unlock");
            Assert.IsFalse(gate.IsHeld, "animation.apply must never leave a lease held");
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

        static AnimatorState FindState(AnimatorStateMachine sm, string name) =>
            sm.states.Select(cs => cs.state).FirstOrDefault(s => s.name == name);

        // ---------------------------------------------------------------- structural validation

        [Test]
        public void Apply_MissingOperationsArray_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var ex = Assert.Throws<ArgumentException>(() =>
                CommandTable.Dispatch(gate, Request("animation.apply", JsonValue.NewObject())));

            StringAssert.Contains("operations", ex.Message);
        }

        // ---------------------------------------------------------------- full op vocabulary sweep, incl. controllerPath -> path

        /// <summary>One animation.apply call touching every op this handler supports. Also proves
        /// the 'controllerPath' -&gt; 'path' rename for createController/editController: the wire
        /// params below use 'controllerPath' EXCLUSIVELY (matching what AnimationApplyTool actually
        /// sends - see that class's own doc comment), and this only works at all if
        /// AnimationApplyCommands' adapter renames it before calling the underlying
        /// animation.create_controller/animation.edit_controller handlers, which read 'path'.</summary>
        [Test]
        public void FullOperationSweep_AppliesEveryOpInOrder_AllApplied_NoLeaseTouched()
        {
            var player = new GameObject("Player");
            var newControllerPath = ScratchDir + "/New.controller";
            var clipPath = ScratchDir + "/Idle.anim";
            AssetDatabase.CreateAsset(new AnimationClip(), clipPath);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("animation.apply", Params(
                    Op("createController", ("controllerPath", JsonValue.String(newControllerPath)),
                        ("states", JsonValue.NewArray().Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("Idle"))))),
                    Op("editController", ("controllerPath", JsonValue.String(newControllerPath)),
                        ("addParameters", JsonValue.NewArray().Add(JsonValue.NewObject()
                            .SetProperty("name", JsonValue.String("Speed")).SetProperty("type", JsonValue.String("Float"))))),
                    Op("assignController", ("gameObjectPath", JsonValue.String("Player")), ("controllerPath", JsonValue.String(newControllerPath))),
                    Op("assignClip", ("controllerPath", JsonValue.String(newControllerPath)), ("stateName", JsonValue.String("Idle")), ("clipPath", JsonValue.String(clipPath))))));

                CollectionAssert.AreEqual(Enumerable.Range(0, 4).ToArray(), AppliedIndices(result));
                Assert.AreEqual(0, FailedItems(result).Items.Count);
                StringAssert.Contains("4", Str(result, "summary"));

                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(newControllerPath);
                Assert.IsNotNull(controller, "createController must have used 'controllerPath' as the asset path (renamed to 'path' internally)");
                Assert.IsTrue(controller.parameters.Any(p => p.name == "Speed"), "editController's addParameters must have applied");

                var animator = player.GetComponent<Animator>();
                Assert.IsNotNull(animator);
                Assert.AreEqual(controller, animator.runtimeAnimatorController);

                var sm = controller.layers[0].stateMachine;
                var idle = FindState(sm, "Idle");
                Assert.IsNotNull(idle);
                Assert.AreEqual(clipPath, AssetDatabase.GetAssetPath(idle.motion));

                var results = ResultsItems(result);
                Assert.AreEqual(4, results.Items.Count);
                Assert.AreEqual("createController", Str(results.Items[0], "op"));
                Assert.AreEqual("editController", Str(results.Items[1], "op"));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        // ---------------------------------------------------------------- unknown op

        [Test]
        public void UnknownOp_RecordedAsPerOperationFailure_BatchContinues()
        {
            var player = new GameObject("Player2");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("animation.apply", Params(
                    Op("frobnicate", ("controllerPath", JsonValue.String("Assets/Nope.controller"))),
                    Op("assignController", ("gameObjectPath", JsonValue.String("Player2")), ("controllerPath", JsonValue.String(ScratchDir + "/Missing.controller"))))));

                var failed = FailedItems(result);
                Assert.AreEqual(2, failed.Items.Count, "both the unknown op AND the missing-controller assign must fail");
                Assert.AreEqual("frobnicate", Str(failed.Items[0], "op"));
                StringAssert.Contains("frobnicate", Str(failed.Items[0], "error"));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        // ---------------------------------------------------------------- partial failure, no rollback

        [Test]
        public void PartialFailure_MiddleOperationFails_EarlierAppliedNotRolledBack_LaterStillAttempted()
        {
            var player = new GameObject("Player3");
            var controllerPath = ScratchDir + "/Partial.controller";
            // A REAL clip, so the failure below is genuinely about the missing STATE ('Ghost') -
            // AssignClip resolves the clip before searching for the state, so a bad clip path would
            // fail for a different reason than the one this test means to exercise.
            var clipPath = ScratchDir + "/Real.anim";
            AssetDatabase.CreateAsset(new AnimationClip(), clipPath);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("animation.apply", Params(
                    Op("createController", ("controllerPath", JsonValue.String(controllerPath))),
                    Op("assignClip", ("controllerPath", JsonValue.String(controllerPath)), ("stateName", JsonValue.String("Ghost")), ("clipPath", JsonValue.String(clipPath))),
                    Op("assignController", ("gameObjectPath", JsonValue.String("Player3")), ("controllerPath", JsonValue.String(controllerPath))))));

                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath), "op 0 must have applied and must not be rolled back by op 1's failure");
                Assert.IsNotNull(player.GetComponent<Animator>(), "op 2 must still have applied after op 1 failed");

                CollectionAssert.AreEqual(new[] { 0, 2 }, AppliedIndices(result));
                var failed = FailedItems(result);
                Assert.AreEqual(1, failed.Items.Count);
                Assert.AreEqual(1L, failed.Items[0].TryGetProperty("index", out var idx) ? idx!.AsInteger() : -1);
                StringAssert.Contains("Ghost", Str(failed.Items[0], "error"));
            }
        }

        // ---------------------------------------------------------------- the headline: one undo, whole spec reverted

        [Test]
        public void Apply_RegistersUndoAsOneGroup_PerformUndoRevertsEveryOperation()
        {
            var player = new GameObject("UndoPlayer");
            var controllerPath = ScratchDir + "/UndoMe.controller";

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            CommandTable.Dispatch(gate, Request("animation.apply", Params(
                Op("createController", ("controllerPath", JsonValue.String(controllerPath)),
                    ("states", JsonValue.NewArray().Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("Idle"))))),
                // editController is the op whose underlying handler normally opens its OWN Undo
                // group when dispatched standalone - see this fixture's own class doc comment.
                Op("editController", ("controllerPath", JsonValue.String(controllerPath)),
                    ("addParameters", JsonValue.NewArray().Add(JsonValue.NewObject()
                        .SetProperty("name", JsonValue.String("Speed")).SetProperty("type", JsonValue.String("Float"))))),
                Op("assignController", ("gameObjectPath", JsonValue.String("UndoPlayer")), ("controllerPath", JsonValue.String(controllerPath))))));

            // sanity: the batch really did all of this before we undo it
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            Assert.IsNotNull(controller);
            Assert.IsTrue(controller.parameters.Any(p => p.name == "Speed"));
            Assert.IsNotNull(player.GetComponent<Animator>());

            Undo.PerformUndo(); // a single Ctrl/Cmd+Z

            Assert.IsNull(player.GetComponent<Animator>(), "the assignController op's added Animator must be undone too, not just the last op");
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath),
                "the whole batch must revert together - createController's asset must be undone too, not just the last op "
                + "(this is what would fail if editController still opened its OWN Undo group mid-batch, splitting this into two steps)");
        }
    }
}
