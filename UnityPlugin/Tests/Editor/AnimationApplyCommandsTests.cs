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

                // The unified partial-batch shape: 'results' carries an entry for every APPLIED op
                // (by the same index 'applied' reports), never for the failed one - a caller can
                // learn which operations landed, which did not, and why from ONE response.
                var results = ResultsItems(result);
                Assert.AreEqual(2, results.Items.Count);
                Assert.AreEqual(0L, results.Items[0].TryGetProperty("index", out var rIdx0) ? rIdx0!.AsInteger() : -1);
                Assert.AreEqual("createController", Str(results.Items[0], "op"));
                Assert.AreEqual(2L, results.Items[1].TryGetProperty("index", out var rIdx1) ? rIdx1!.AsInteger() : -1);
                Assert.AreEqual("assignController", Str(results.Items[1], "op"));
                Assert.IsTrue(results.Items[0].TryGetProperty("result", out var r0) && r0 != null,
                    "each results entry must carry the op's own result payload, not just index/op");
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

        // ================================================================== DESIGN-05 Wave 1-3

        static JsonValue Dispatch(string method, JsonValue @params)
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            return CommandTable.Dispatch(gate, Request(method, @params));
        }

        static JsonValue Apply(params JsonValue[] operations) => Dispatch("animation.apply", Params(operations));

        // ---------------------------------------------------------------- setBlendTree: round trip (the keystone test, DESIGN-05 §9.2)

        [Test]
        public void SetBlendTree_Describe_RoundTrip_ReturnsTheSameTypeParameterAndChildren()
        {
            var controllerPath = ScratchDir + "/Loco.controller";
            var idlePath = ScratchDir + "/Idle.anim";
            var walkPath = ScratchDir + "/Walk.anim";
            AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            AssetDatabase.CreateAsset(new AnimationClip(), idlePath);
            AssetDatabase.CreateAsset(new AnimationClip(), walkPath);

            var tree = JsonValue.NewObject()
                .SetProperty("type", JsonValue.String("Simple1D"))
                .SetProperty("parameter", JsonValue.String("Speed"))
                .SetProperty("children", JsonValue.NewArray()
                    .Add(JsonValue.NewObject().SetProperty("clip", JsonValue.String(idlePath)).SetProperty("threshold", JsonValue.Float(0)))
                    .Add(JsonValue.NewObject().SetProperty("clip", JsonValue.String(walkPath)).SetProperty("threshold", JsonValue.Float(1))));

            var setResult = Apply(Op("setBlendTree",
                ("controllerPath", JsonValue.String(controllerPath)),
                ("stateName", JsonValue.String("Locomotion")),
                ("tree", tree)));
            Assert.AreEqual(1, AppliedIndices(setResult).Length, "setBlendTree must have applied cleanly");

            var describeResult = Apply(Op("describe", ("controllerPath", JsonValue.String(controllerPath))));
            Assert.AreEqual(1, AppliedIndices(describeResult).Length, "describe must have applied cleanly");

            Assert.IsTrue(ResultsItems(describeResult).Items[0].TryGetProperty("result", out var describePayload) && describePayload != null);

            Assert.IsTrue(describePayload.TryGetProperty("layers", out var layers) && layers.Items.Count > 0);
            Assert.IsTrue(layers.Items[0].TryGetProperty("states", out var states) && states != null);
            var locomotion = states.Items.Single(st => Str(st, "name") == "Locomotion");
            var describedTree = locomotion.TryGetProperty("tree", out var t) ? t : null;
            Assert.IsNotNull(describedTree, "the state's motion must describe back as a tree, not a clip");

            Assert.AreEqual("Simple1D", Str(describedTree, "type"));
            Assert.AreEqual("Speed", Str(describedTree, "parameter"));
            var children = describedTree.TryGetProperty("children", out var c) ? c : null;
            Assert.AreEqual(2, children.Items.Count);
            Assert.AreEqual(idlePath, Str(children.Items[0], "clip"));
            Assert.AreEqual(0.0, children.Items[0].TryGetProperty("threshold", out var th0) ? th0.AsDouble() : -1, 0.0001);
            Assert.AreEqual(walkPath, Str(children.Items[1], "clip"));
            Assert.AreEqual(1.0, children.Items[1].TryGetProperty("threshold", out var th1) ? th1.AsDouble() : -1, 0.0001);
        }

        // ---------------------------------------------------------------- setBlendTree: F4 - explicit thresholds are never recomputed

        [Test]
        public void SetBlendTree_Simple1D_ExplicitThresholds_AreStoredVerbatim_NotEvenlyRespaced()
        {
            // F4, live-verified: Unity's default useAutomaticThresholds=true silently recomputes
            // every child's threshold, evenly spaced over [0,1], the moment a child is added -
            // 0/1/3 sent, 0/0.5/1 stored. A TWO-child 0/1 tree (the round-trip test above) cannot
            // catch this: automatic respacing of two children happens to land on 0/1 too. A third,
            // unevenly-spaced value is what actually distinguishes "stored as sent" from "recomputed".
            var controllerPath = ScratchDir + "/LocoThresholds.controller";
            var idlePath = ScratchDir + "/IdleT.anim";
            var walkPath = ScratchDir + "/WalkT.anim";
            var runPath = ScratchDir + "/RunT.anim";
            AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            AssetDatabase.CreateAsset(new AnimationClip(), idlePath);
            AssetDatabase.CreateAsset(new AnimationClip(), walkPath);
            AssetDatabase.CreateAsset(new AnimationClip(), runPath);

            var tree = JsonValue.NewObject()
                .SetProperty("type", JsonValue.String("Simple1D"))
                .SetProperty("parameter", JsonValue.String("Speed"))
                .SetProperty("children", JsonValue.NewArray()
                    .Add(JsonValue.NewObject().SetProperty("clip", JsonValue.String(idlePath)).SetProperty("threshold", JsonValue.Float(0)))
                    .Add(JsonValue.NewObject().SetProperty("clip", JsonValue.String(walkPath)).SetProperty("threshold", JsonValue.Float(1)))
                    .Add(JsonValue.NewObject().SetProperty("clip", JsonValue.String(runPath)).SetProperty("threshold", JsonValue.Float(3))));

            var setResult = Apply(Op("setBlendTree",
                ("controllerPath", JsonValue.String(controllerPath)),
                ("stateName", JsonValue.String("Locomotion")),
                ("tree", tree)));
            Assert.AreEqual(1, AppliedIndices(setResult).Length, "setBlendTree must have applied cleanly");

            var blendTree = AssetDatabase.LoadAllAssetsAtPath(controllerPath).OfType<BlendTree>().Single();
            Assert.IsFalse(blendTree.useAutomaticThresholds, "explicit thresholds require useAutomaticThresholds=false");
            Assert.AreEqual(3, blendTree.children.Length);
            Assert.AreEqual(0.0, blendTree.children[0].threshold, 0.0001);
            Assert.AreEqual(1.0, blendTree.children[1].threshold, 0.0001);
            Assert.AreEqual(3.0, blendTree.children[2].threshold, 0.0001, "an explicit threshold must be stored verbatim, never evenly re-spaced");

            var describeResult = Apply(Op("describe", ("controllerPath", JsonValue.String(controllerPath))));
            var states = ResultsItems(describeResult).Items[0]
                .TryGetProperty("result", out var describePayload) && describePayload != null
                && describePayload.TryGetProperty("layers", out var layers) ? layers.Items[0].TryGetProperty("states", out var s) ? s : null : null;
            var describedTree = states.Items.Single(st => Str(st, "name") == "Locomotion").TryGetProperty("tree", out var t) ? t : null;
            var describedChildren = describedTree.TryGetProperty("children", out var c) ? c : null;
            Assert.AreEqual(3.0, describedChildren.Items[2].TryGetProperty("threshold", out var th2) ? th2.AsDouble() : -1, 0.0001,
                "describe must report the same stored threshold, not a recomputed one");
        }

        // ---------------------------------------------------------------- setBlendTree: pre-flight atomicity (DESIGN-05 §4.4)

        [Test]
        public void SetBlendTree_InvalidTree_FailsAtomically_ControllerLeftUntouched_ReportsEveryProblem()
        {
            var controllerPath = ScratchDir + "/Bad.controller";
            AnimatorController.CreateAnimatorControllerAtPath(controllerPath);

            // Two independent problems in one tree: an unknown key on child[0], and child[1] (a
            // FreeformCartesian2D type) missing its required 'position'. Both must be reported from
            // ONE pass, not just the first hit.
            var tree = JsonValue.NewObject()
                .SetProperty("type", JsonValue.String("FreeformCartesian2D"))
                .SetProperty("parameterX", JsonValue.String("X"))
                .SetProperty("parameterY", JsonValue.String("Y"))
                .SetProperty("children", JsonValue.NewArray()
                    .Add(JsonValue.NewObject()
                        .SetProperty("clip", JsonValue.String(ScratchDir + "/DoesNotMatter.anim"))
                        .SetProperty("threshhold", JsonValue.Float(0))) // typo'd key, and this type doesn't use 'threshold' anyway
                    .Add(JsonValue.NewObject().SetProperty("clip", JsonValue.String(ScratchDir + "/AlsoMissing.anim"))));

            var result = Apply(Op("setBlendTree",
                ("controllerPath", JsonValue.String(controllerPath)),
                ("stateName", JsonValue.String("Locomotion")),
                ("tree", tree)));

            var failed = FailedItems(result);
            Assert.AreEqual(1, failed.Items.Count);
            var error = Str(failed.Items[0], "error");
            StringAssert.Contains("threshhold", error);
            StringAssert.Contains("position", error);

            // Nothing was created or replaced - the controller is exactly as it was before the call.
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            Assert.AreEqual(0, controller.layers[0].stateMachine.states.Length, "no state may have been created by a rejected tree");
            var allAssets = AssetDatabase.LoadAllAssetsAtPath(controllerPath);
            Assert.IsFalse(allAssets.Any(a => a is BlendTree), "no BlendTree sub-asset may have been created by a rejected tree");
        }

        // ---------------------------------------------------------------- setBlendTree: orphan-accumulation guard (DESIGN-05 §4.7)

        [Test]
        public void SetBlendTree_Replace_DestroysSupersededTree_ExactlyOneBlendTreeSubAssetRemains()
        {
            var controllerPath = ScratchDir + "/Replace.controller";
            var clipPath = ScratchDir + "/Clip.anim";
            AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            AssetDatabase.CreateAsset(new AnimationClip(), clipPath);

            JsonValue TreeWithThreshold(double threshold) => JsonValue.NewObject()
                .SetProperty("type", JsonValue.String("Simple1D"))
                .SetProperty("parameter", JsonValue.String("Speed"))
                .SetProperty("children", JsonValue.NewArray()
                    .Add(JsonValue.NewObject().SetProperty("clip", JsonValue.String(clipPath)).SetProperty("threshold", JsonValue.Float(threshold))));

            var first = Apply(Op("setBlendTree", ("controllerPath", JsonValue.String(controllerPath)), ("stateName", JsonValue.String("Locomotion")), ("tree", TreeWithThreshold(0))));
            Assert.AreEqual(1, AppliedIndices(first).Length);

            var second = Apply(Op("setBlendTree", ("controllerPath", JsonValue.String(controllerPath)), ("stateName", JsonValue.String("Locomotion")), ("tree", TreeWithThreshold(1))));
            Assert.AreEqual(1, AppliedIndices(second).Length);
            Assert.IsTrue(second.TryGetProperty("results", out _));
            var replacedFlag = ResultsItems(second).Items[0].TryGetProperty("result", out var r) && r.TryGetProperty("replacedExistingTree", out var rf) && rf.AsBoolean();
            Assert.IsTrue(replacedFlag, "the second call must report that it replaced an existing tree");

            var allAssets = AssetDatabase.LoadAllAssetsAtPath(controllerPath);
            Assert.AreEqual(1, allAssets.Count(a => a is BlendTree), "replacing a tree must leave EXACTLY ONE BlendTree sub-asset, not an accumulating orphan");
        }

        // ---------------------------------------------------------------- setCurve: easing sampled from the curve itself (DESIGN-05 §9 test 5a)

        [Test]
        public void SetCurve_EveryEasing_ActuallyEasesTheEvaluatedCurve()
        {
            var clipPath = ScratchDir + "/Eased.anim";
            AssetDatabase.CreateAsset(new AnimationClip(), clipPath);

            JsonValue KeysFor(string easing) => JsonValue.NewArray()
                .Add(JsonValue.NewObject().SetProperty("time", JsonValue.Float(0)).SetProperty("value", JsonValue.Float(0)))
                .Add(JsonValue.NewObject().SetProperty("time", JsonValue.Float(1)).SetProperty("value", JsonValue.Float(1)).SetProperty("easing", JsonValue.String(easing)));

            AnimationCurve CurveFor(string easing)
            {
                var result = Apply(Op("setCurve",
                    ("clipPath", JsonValue.String(clipPath)),
                    ("path", JsonValue.String("")),
                    ("type", JsonValue.String("Transform")),
                    ("property", JsonValue.String("m_LocalPosition.x")),
                    ("keys", KeysFor(easing))));
                Assert.AreEqual(1, AppliedIndices(result).Length, "setCurve(" + easing + ") must have applied cleanly: " + result);

                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                var binding = new EditorCurveBinding { path = "", type = typeof(Transform), propertyName = "m_LocalPosition.x" };
                return AnimationUtility.GetEditorCurve(clip, binding);
            }

            const float epsilon = 0.01f;

            Assert.AreEqual(0.5f, CurveFor("linear").Evaluate(0.5f), epsilon);

            var easeOut = CurveFor("easeOut").Evaluate(0.5f);
            Assert.AreEqual(0.75f, easeOut, epsilon, "easeOut at the midpoint must be the exact quadratic 0.75, strictly ahead of linear");

            var easeIn = CurveFor("easeIn").Evaluate(0.5f);
            Assert.AreEqual(0.25f, easeIn, epsilon, "easeIn at the midpoint must be the exact quadratic 0.25, strictly behind linear");

            // A symmetric S-curve passes exactly through the midpoint even when broken (design's own
            // warning) - sample the QUARTER points instead, which the midpoint cannot fake.
            var easeInOutCurve = CurveFor("easeInOut");
            Assert.AreEqual(0.5f, easeInOutCurve.Evaluate(0.5f), epsilon);
            Assert.AreEqual(0.156f, easeInOutCurve.Evaluate(0.25f), 0.02f, "easeInOut must LAG linear at the quarter point");
            Assert.AreEqual(0.844f, easeInOutCurve.Evaluate(0.75f), 0.02f, "easeInOut must LEAD linear at the three-quarter point");

            Assert.AreEqual(0f, CurveFor("constant").Evaluate(0.99f), epsilon, "constant must still hold the SOURCE key's value just before the next key");
        }

        // ---------------------------------------------------------------- setEvents: replace-all, [] clears everything

        [Test]
        public void SetEvents_EmptyArray_ClearsEveryPreviouslySetEvent()
        {
            var clipPath = ScratchDir + "/Events.anim";
            AssetDatabase.CreateAsset(new AnimationClip(), clipPath);

            var withEvents = Apply(Op("setEvents",
                ("clipPath", JsonValue.String(clipPath)),
                ("events", JsonValue.NewArray()
                    .Add(JsonValue.NewObject().SetProperty("time", JsonValue.Float(0.1)).SetProperty("function", JsonValue.String("OnFoo")))
                    .Add(JsonValue.NewObject().SetProperty("time", JsonValue.Float(0.2)).SetProperty("function", JsonValue.String("OnBar"))))));
            Assert.AreEqual(1, AppliedIndices(withEvents).Length);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            Assert.AreEqual(2, AnimationUtility.GetAnimationEvents(clip).Length);

            var cleared = Apply(Op("setEvents", ("clipPath", JsonValue.String(clipPath)), ("events", JsonValue.NewArray())));
            Assert.AreEqual(1, AppliedIndices(cleared).Length);
            Assert.AreEqual(0, AnimationUtility.GetAnimationEvents(clip).Length, "an explicit empty 'events' array must clear every event");
        }

        // ---------------------------------------------------------------- setCurve/setEvents: imported-clip refusal names the other tool

        [Test]
        public void SetCurve_AgainstAnImportedClip_RefusesNamingProjectSettingsApply()
        {
            // A non-clip main asset with an AnimationClip SUB-asset - the same shape an FBX has
            // (main asset is the imported model, the clip is a sub-asset), without needing a real
            // FBX import. An AnimatorController stands in for "some other main asset type".
            var fakeImportedPath = ScratchDir + "/FakeImported.controller";
            var mainAsset = AnimatorController.CreateAnimatorControllerAtPath(fakeImportedPath);
            var subClip = new AnimationClip { name = "EmbeddedClip" };
            AssetDatabase.AddObjectToAsset(subClip, mainAsset);
            AssetDatabase.SaveAssets();

            var result = Apply(Op("setCurve",
                ("clipPath", JsonValue.String(fakeImportedPath)),
                ("path", JsonValue.String("")),
                ("type", JsonValue.String("Transform")),
                ("property", JsonValue.String("m_LocalPosition.x")),
                ("keys", JsonValue.NewArray().Add(JsonValue.NewObject().SetProperty("time", JsonValue.Float(0)).SetProperty("value", JsonValue.Float(0)))
                    .Add(JsonValue.NewObject().SetProperty("time", JsonValue.Float(1)).SetProperty("value", JsonValue.Float(1))))));

            var failed = FailedItems(result);
            Assert.AreEqual(1, failed.Items.Count);
            var error = Str(failed.Items[0], "error");
            StringAssert.Contains("project_settings_apply", error);
            LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(error);
        }

        // ---------------------------------------------------------------- addLayer + layer field back-compat

        [Test]
        public void AddLayer_ThenAssignClipAndEditController_TargetTheNamedLayer_LayerOmitted_StaysOnLayer0()
        {
            var controllerPath = ScratchDir + "/Layers.controller";
            AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            var clipPath = ScratchDir + "/Punch.anim";
            AssetDatabase.CreateAsset(new AnimationClip(), clipPath);

            var addLayerResult = Apply(Op("addLayer", ("controllerPath", JsonValue.String(controllerPath)), ("name", JsonValue.String("Combat"))));
            Assert.AreEqual(1, AppliedIndices(addLayerResult).Length);

            var editResult = Apply(Op("editController",
                ("controllerPath", JsonValue.String(controllerPath)),
                ("layer", JsonValue.String("Combat")),
                ("addStates", JsonValue.NewArray().Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("Punch"))))));
            Assert.AreEqual(1, AppliedIndices(editResult).Length);

            var assignResult = Apply(Op("assignClip",
                ("controllerPath", JsonValue.String(controllerPath)),
                ("layer", JsonValue.String("Combat")),
                ("stateName", JsonValue.String("Punch")),
                ("clipPath", JsonValue.String(clipPath))));
            Assert.AreEqual(1, AppliedIndices(assignResult).Length);

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            Assert.AreEqual(2, controller.layers.Length);
            var combatLayer = controller.layers.Single(l => l.name == "Combat");
            var punch = combatLayer.stateMachine.states.Single(cs => cs.state.name == "Punch");
            Assert.AreEqual(clipPath, AssetDatabase.GetAssetPath(punch.state.motion));

            // Layer 0 (Base Layer) must be untouched - 'layer' scoped both ops to Combat only.
            Assert.AreEqual(0, controller.layers[0].stateMachine.states.Length);
        }
    }
}
