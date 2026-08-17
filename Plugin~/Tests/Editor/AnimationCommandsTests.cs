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
    /// The four class-1 animation mutations (see the "52 Editor tools" plan's operation-class table
    /// - single-tick, no reload lease), dispatched through <see cref="CommandTable.Dispatch"/> the
    /// same way every other class-1 suite does - see SceneCommandsTests' own doc comment for the
    /// three things every mutation test proves. Controllers/clips are assets, so - like
    /// MaterialCommandsTests - each test owns a scratch asset folder reset in SetUp/TearDown.
    /// </summary>
    [TestFixture]
    public sealed class AnimationCommandsTests
    {
        const string ScratchDir = "Assets/Tests/_HadesAnimationScratch";

        [SetUp]
        public void SetUp()
        {
            SceneTestFixtures.ResetScene();
            Undo.ClearAll();
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
            AssetDatabase.CreateFolder("Assets/Tests", "_HadesAnimationScratch");
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
            Assert.AreEqual(0, fake.LockCalls, "a class-1 animation mutation must never call Lock");
            Assert.AreEqual(0, fake.UnlockCalls, "a class-1 animation mutation must never call Unlock");
            Assert.IsFalse(gate.IsHeld, "a class-1 animation mutation must never leave a lease held");
        }

        static string StringProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.String ? v.AsString() : null;

        static long IntProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.Integer ? v.AsInteger() : long.MinValue;

        static bool BoolProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.Boolean && v.AsBoolean();

        static AnimatorState FindState(AnimatorStateMachine sm, string name) =>
            sm.states.Select(cs => cs.state).FirstOrDefault(s => s.name == name);

        // ------------------------------------------------------------------ animation.assign_controller

        [Test]
        public void AssignController_NoExistingAnimator_AddsOneAndAssigns_NoLeaseTouched()
        {
            var go = new GameObject("Target");
            var path = ScratchDir + "/Fresh.controller";
            AnimatorController.CreateAnimatorControllerAtPath(path);
            AssetDatabase.SaveAssets();

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("Target"))
                    .SetProperty("controllerPath", JsonValue.String(path));
                var result = CommandTable.Dispatch(gate, Request("animation.assign_controller", @params));

                Assert.AreEqual(path, StringProp(result, "controller"));
                Assert.IsTrue(BoolProp(result, "addedAnimator"));

                var animator = go.GetComponent<Animator>();
                Assert.IsNotNull(animator);
                Assert.IsNotNull(animator.runtimeAnimatorController);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void AssignController_ExistingAnimator_ReplacesControllerWithoutAddingAnother()
        {
            var go = new GameObject("Target");
            go.AddComponent<Animator>();
            var path = ScratchDir + "/Existing.controller";
            AnimatorController.CreateAnimatorControllerAtPath(path);
            AssetDatabase.SaveAssets();

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Target"))
                .SetProperty("controllerPath", JsonValue.String(path));
            var result = CommandTable.Dispatch(gate, Request("animation.assign_controller", @params));

            Assert.IsFalse(BoolProp(result, "addedAnimator"));
            Assert.AreEqual(1, go.GetComponents<Animator>().Length);
            Assert.IsNotNull(go.GetComponent<Animator>().runtimeAnimatorController);
        }

        [Test]
        public void AssignController_UnknownGameObject_ThrowsActionableError()
        {
            new GameObject("ExistingRoot");
            var path = ScratchDir + "/Ghost.controller";
            AnimatorController.CreateAnimatorControllerAtPath(path);
            AssetDatabase.SaveAssets();

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Ghost"))
                .SetProperty("controllerPath", JsonValue.String(path));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("animation.assign_controller", @params)));

            StringAssert.Contains("Ghost", ex.Message);
            StringAssert.Contains("ExistingRoot", ex.Message);
        }

        [Test]
        public void AssignController_UnknownController_ThrowsActionableError()
        {
            new GameObject("Target");
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Target"))
                .SetProperty("controllerPath", JsonValue.String(ScratchDir + "/NoSuchController.controller"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("animation.assign_controller", @params)));

            StringAssert.Contains("NoSuchController.controller", ex.Message);
        }

        [Test]
        public void AssignController_RegistersUndo_PerformUndoRemovesAddedAnimator()
        {
            var go = new GameObject("Target");
            var path = ScratchDir + "/UndoAdd.controller";
            AnimatorController.CreateAnimatorControllerAtPath(path);
            AssetDatabase.SaveAssets();

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Target"))
                .SetProperty("controllerPath", JsonValue.String(path));
            CommandTable.Dispatch(gate, Request("animation.assign_controller", @params));
            Assert.IsNotNull(go.GetComponent<Animator>());

            Undo.PerformUndo();

            Assert.IsNull(go.GetComponent<Animator>());
        }

        [Test]
        public void AssignController_RegistersUndo_PerformUndoRestoresPriorController_WhenAnimatorAlreadyExisted()
        {
            var go = new GameObject("Target");
            var animator = go.AddComponent<Animator>();
            var path = ScratchDir + "/UndoExisting.controller";
            AnimatorController.CreateAnimatorControllerAtPath(path);
            AssetDatabase.SaveAssets();

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Target"))
                .SetProperty("controllerPath", JsonValue.String(path));
            CommandTable.Dispatch(gate, Request("animation.assign_controller", @params));
            Assert.IsNotNull(animator.runtimeAnimatorController);

            Undo.PerformUndo();

            Assert.IsNull(animator.runtimeAnimatorController);
        }

        // ----------------------------------------------------------------------- animation.assign_clip

        [Test]
        public void AssignClip_SetsMotionOnNamedState_NoLeaseTouched()
        {
            var path = ScratchDir + "/ClipController.controller";
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var sm = controller.layers[0].stateMachine;
            var stateName = sm.AddState("Idle").name; // a fresh controller starts with zero states
            AssetDatabase.SaveAssets();

            var clip = new AnimationClip();
            var clipPath = ScratchDir + "/Clip.anim";
            AssetDatabase.CreateAsset(clip, clipPath);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("controllerPath", JsonValue.String(path))
                    .SetProperty("stateName", JsonValue.String(stateName))
                    .SetProperty("clipPath", JsonValue.String(clipPath));
                var result = CommandTable.Dispatch(gate, Request("animation.assign_clip", @params));

                Assert.AreEqual(stateName, StringProp(result, "state"));
                Assert.AreEqual(clipPath, StringProp(result, "clip"));
                Assert.AreEqual(clip, FindState(sm, stateName).motion);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void AssignClip_UnknownState_ThrowsActionableErrorListingAvailableStates()
        {
            var path = ScratchDir + "/ClipController2.controller";
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var existingStateName = controller.layers[0].stateMachine.AddState("Idle").name; // fresh controller starts with zero states
            AssetDatabase.SaveAssets();
            var clip = new AnimationClip();
            var clipPath = ScratchDir + "/Clip2.anim";
            AssetDatabase.CreateAsset(clip, clipPath);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("controllerPath", JsonValue.String(path))
                .SetProperty("stateName", JsonValue.String("Ghost"))
                .SetProperty("clipPath", JsonValue.String(clipPath));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("animation.assign_clip", @params)));

            StringAssert.Contains("Ghost", ex.Message);
            StringAssert.Contains(existingStateName, ex.Message);
        }

        [Test]
        public void AssignClip_StateUsesBlendTree_ThrowsActionableError()
        {
            var path = ScratchDir + "/BlendController.controller";
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            // BlendTree is a Motion (UnityEngine.Object), not a ScriptableObject - the supported
            // way to create one is via this factory, which also creates the owning state.
            var blendState = controller.CreateBlendTreeInController("BlendState", out _);
            var stateName = blendState.name;
            AssetDatabase.SaveAssets();

            var clip = new AnimationClip();
            var clipPath = ScratchDir + "/BlendClip.anim";
            AssetDatabase.CreateAsset(clip, clipPath);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("controllerPath", JsonValue.String(path))
                .SetProperty("stateName", JsonValue.String(stateName))
                .SetProperty("clipPath", JsonValue.String(clipPath));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("animation.assign_clip", @params)));

            StringAssert.Contains("BlendTree", ex.Message);
        }

        [Test]
        public void AssignClip_RegistersUndo_PerformUndoRestoresPriorMotion()
        {
            var path = ScratchDir + "/ClipUndo.controller";
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var sm = controller.layers[0].stateMachine;
            var stateName = sm.AddState("Idle").name; // a fresh controller starts with zero states
            AssetDatabase.SaveAssets();

            var clip = new AnimationClip();
            var clipPath = ScratchDir + "/ClipUndo.anim";
            AssetDatabase.CreateAsset(clip, clipPath);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var @params = JsonValue.NewObject()
                .SetProperty("controllerPath", JsonValue.String(path))
                .SetProperty("stateName", JsonValue.String(stateName))
                .SetProperty("clipPath", JsonValue.String(clipPath));
            CommandTable.Dispatch(gate, Request("animation.assign_clip", @params));
            Assert.AreEqual(clip, FindState(sm, stateName).motion);

            Undo.PerformUndo();

            Assert.IsNull(FindState(sm, stateName).motion);
        }

        // ------------------------------------------------------------------- animation.create_controller

        [Test]
        public void CreateController_WithParametersStatesAndTransitions_BuildsExpectedGraph_NoLeaseTouched()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var path = ScratchDir + "/Built.controller";
                var parameters = JsonValue.NewArray().Add(
                    JsonValue.NewObject().SetProperty("name", JsonValue.String("Speed")).SetProperty("type", JsonValue.String("Float")).SetProperty("default", JsonValue.Float(2.5)));
                var states = JsonValue.NewArray()
                    .Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("Idle")).SetProperty("isDefault", JsonValue.Bool(true)))
                    .Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("Run")));
                var transitions = JsonValue.NewArray().Add(
                    JsonValue.NewObject()
                        .SetProperty("from", JsonValue.String("Idle"))
                        .SetProperty("to", JsonValue.String("Run"))
                        .SetProperty("hasExitTime", JsonValue.Bool(false))
                        .SetProperty("duration", JsonValue.Float(0.1))
                        .SetProperty("conditions", JsonValue.NewArray().Add(
                            JsonValue.NewObject().SetProperty("parameter", JsonValue.String("Speed"))
                                .SetProperty("mode", JsonValue.String("Greater")).SetProperty("threshold", JsonValue.Float(0.5)))));

                var @params = JsonValue.NewObject()
                    .SetProperty("path", JsonValue.String(path))
                    .SetProperty("parameters", parameters)
                    .SetProperty("states", states)
                    .SetProperty("transitions", transitions);
                var result = CommandTable.Dispatch(gate, Request("animation.create_controller", @params));

                Assert.AreEqual(path, StringProp(result, "path"));
                Assert.AreEqual(1L, IntProp(result, "parameterCount"));
                Assert.AreEqual(2L, IntProp(result, "stateCount"));
                Assert.AreEqual(1L, IntProp(result, "transitionCount"));
                Assert.IsTrue(result.TryGetProperty("errors", out var errors));
                Assert.AreEqual(0, errors.Items.Count);

                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                Assert.IsNotNull(controller);
                Assert.AreEqual(1, controller.parameters.Length);
                Assert.AreEqual("Speed", controller.parameters[0].name);
                Assert.AreEqual(2.5f, controller.parameters[0].defaultFloat, 0.0001f);

                var sm = controller.layers[0].stateMachine;
                Assert.AreEqual(2, sm.states.Length);
                Assert.AreEqual("Idle", sm.defaultState.name);
                Assert.AreEqual(1, FindState(sm, "Idle").transitions.Length);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void CreateController_ParameterWithNonNumericDefault_RecordsAnErrorInsteadOfThrowing_NoOrphanedAsset()
        {
            // Bug: AddParameter's RequireNumber threw straight past CreateController's own
            // per-entry error handling (the bad-type/missing-name checks beside it already
            // degrade to an errors-array entry rather than throwing) - by the time it ran, the
            // .controller asset was already created on disk (CreateAnimatorControllerAtPath,
            // above), so the whole call faulting orphaned it: a retry at the same path then hits
            // "already exists" (CreateController_AlreadyExists_ThrowsActionableError, below).
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var path = ScratchDir + "/BadDefault.controller";
            var parameters = JsonValue.NewArray().Add(
                JsonValue.NewObject().SetProperty("name", JsonValue.String("Speed")).SetProperty("type", JsonValue.String("Float"))
                    .SetProperty("default", JsonValue.String("not-a-number")));
            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(path)).SetProperty("parameters", parameters);

            var result = CommandTable.Dispatch(gate, Request("animation.create_controller", @params));

            Assert.IsTrue(result.TryGetProperty("errors", out var errors));
            Assert.AreEqual(1, errors.Items.Count);
            StringAssert.Contains("Speed", errors.Items[0].AsString());

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            Assert.IsNotNull(controller, "the controller asset must still be usable, with the bad parameter degraded rather than the whole call thrown away and the asset orphaned");
            Assert.AreEqual(1, controller.parameters.Length);
            Assert.AreEqual("Speed", controller.parameters[0].name);
        }

        [Test]
        public void CreateController_TraversalPath_RefusedBeforeAnyWrite()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Assets/../Escaped.controller"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("animation.create_controller", @params)));

            StringAssert.Contains("Escaped.controller", ex.Message);
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<AnimatorController>("Assets/Escaped.controller"));
        }

        [Test]
        public void CreateController_PathNotEndingInController_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(ScratchDir + "/Bad.asset"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("animation.create_controller", @params)));

            StringAssert.Contains(".controller", ex.Message);
        }

        [Test]
        public void CreateController_AlreadyExists_ThrowsActionableError()
        {
            var path = ScratchDir + "/AlreadyThere.controller";
            AnimatorController.CreateAnimatorControllerAtPath(path);
            AssetDatabase.SaveAssets();

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(path));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("animation.create_controller", @params)));

            StringAssert.Contains("animation_edit_controller", ex.Message);
        }

        [Test]
        public void CreateController_RegistersUndo_PerformUndoRemovesAsset()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var path = ScratchDir + "/UndoController.controller";
            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(path));
            CommandTable.Dispatch(gate, Request("animation.create_controller", @params));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<AnimatorController>(path));

            Undo.PerformUndo();

            Assert.IsNull(AssetDatabase.LoadAssetAtPath<AnimatorController>(path));
        }

        // --------------------------------------------------------------------- animation.edit_controller

        [Test]
        public void EditController_NoOperationsProvided_ThrowsActionableError()
        {
            var path = ScratchDir + "/EditEmpty.controller";
            AnimatorController.CreateAnimatorControllerAtPath(path);
            AssetDatabase.SaveAssets();

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(path));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("animation.edit_controller", @params)));

            StringAssert.Contains("addParameters", ex.Message);
        }

        [Test]
        public void EditController_AddsAndRemovesAcrossParametersStatesTransitions_NoLeaseTouched()
        {
            var path = ScratchDir + "/EditMe.controller";
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            controller.AddParameter("Health", AnimatorControllerParameterType.Int);
            controller.AddParameter("ToRemove", AnimatorControllerParameterType.Bool);
            var sm = controller.layers[0].stateMachine;
            var idle = sm.AddState("Idle"); // a fresh controller starts with zero states - nothing to remove first
            sm.defaultState = idle;
            var dying = sm.AddState("Dying");
            var toDying = idle.AddTransition(dying);
            toDying.AddCondition(AnimatorConditionMode.Less, 0, "Health");
            AssetDatabase.SaveAssets();

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var addParameters = JsonValue.NewArray().Add(
                    JsonValue.NewObject().SetProperty("name", JsonValue.String("Speed")).SetProperty("type", JsonValue.String("Float")));
                var removeParameters = JsonValue.NewArray().Add(JsonValue.String("ToRemove"));
                var addStates = JsonValue.NewArray().Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("Run")));
                var removeStates = JsonValue.NewArray().Add(JsonValue.String("Dying"));
                var addTransitions = JsonValue.NewArray().Add(
                    JsonValue.NewObject().SetProperty("from", JsonValue.String("Idle")).SetProperty("to", JsonValue.String("Run")));

                var @params = JsonValue.NewObject()
                    .SetProperty("path", JsonValue.String(path))
                    .SetProperty("addParameters", addParameters)
                    .SetProperty("removeParameters", removeParameters)
                    .SetProperty("addStates", addStates)
                    .SetProperty("removeStates", removeStates)
                    .SetProperty("addTransitions", addTransitions);

                var result = CommandTable.Dispatch(gate, Request("animation.edit_controller", @params));

                Assert.IsTrue(result.TryGetProperty("added", out var added));
                Assert.IsTrue(result.TryGetProperty("removed", out var removed));
                Assert.IsTrue(result.TryGetProperty("errors", out var errors));
                Assert.AreEqual(0, errors.Items.Count);

                var addedStrings = added.Items.Select(v => v.AsString()).ToList();
                var removedStrings = removed.Items.Select(v => v.AsString()).ToList();
                Assert.Contains("parameter:Speed", addedStrings);
                Assert.Contains("state:Run", addedStrings);
                Assert.Contains("transition:Idle->Run", addedStrings);
                Assert.Contains("parameter:ToRemove", removedStrings);
                Assert.Contains("state:Dying", removedStrings);

                Assert.IsFalse(controller.parameters.Any(p => p.name == "ToRemove"));
                Assert.IsTrue(controller.parameters.Any(p => p.name == "Speed"));

                var stateNames = sm.states.Select(s => s.state.name).ToList();
                Assert.Contains("Run", stateNames);
                Assert.IsFalse(stateNames.Contains("Dying"));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void EditController_ParameterWithNonNumericDefault_RecordsAnErrorAndStillAppliesTheOtherOperations()
        {
            // Same bug as CreateController_ParameterWithNonNumericDefault_..., one call further:
            // DoEditController's own addParameters loop had no per-entry error handling either, so
            // RequireNumber throwing here aborted the WHOLE batch - a later, perfectly valid entry
            // (Health, added second here) never got applied, and the removals/additions already
            // computed before the throw were lost along with it.
            var path = ScratchDir + "/EditBadDefault.controller";
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            AssetDatabase.SaveAssets();

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var addParameters = JsonValue.NewArray()
                .Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("Speed")).SetProperty("type", JsonValue.String("Float"))
                    .SetProperty("default", JsonValue.String("not-a-number")))
                .Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("Health")).SetProperty("type", JsonValue.String("Int")));
            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(path)).SetProperty("addParameters", addParameters);

            var result = CommandTable.Dispatch(gate, Request("animation.edit_controller", @params));

            Assert.IsTrue(result.TryGetProperty("errors", out var errors));
            Assert.AreEqual(1, errors.Items.Count);

            Assert.IsTrue(result.TryGetProperty("added", out var added));
            var addedStrings = added.Items.Select(v => v.AsString()).ToList();
            Assert.Contains("parameter:Speed", addedStrings);
            Assert.Contains("parameter:Health", addedStrings); // proves the batch did not abort after the bad entry

            Assert.IsTrue(controller.parameters.Any(p => p.name == "Speed"));
            Assert.IsTrue(controller.parameters.Any(p => p.name == "Health"));
        }

        [Test]
        public void EditController_UnknownControllerPath_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("path", JsonValue.String(ScratchDir + "/Ghost.controller"))
                .SetProperty("addParameters", JsonValue.NewArray().Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("X")).SetProperty("type", JsonValue.String("Bool"))));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("animation.edit_controller", @params)));

            StringAssert.Contains("Ghost.controller", ex.Message);
        }

        [Test]
        public void EditController_RegistersUndo_PerformUndoRemovesAddedParameterAndState()
        {
            var path = ScratchDir + "/EditUndo.controller";
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var sm = controller.layers[0].stateMachine;
            AssetDatabase.SaveAssets();

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var addParameters = JsonValue.NewArray().Add(
                JsonValue.NewObject().SetProperty("name", JsonValue.String("NewParam")).SetProperty("type", JsonValue.String("Bool")));
            var addStates = JsonValue.NewArray().Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("NewState")));
            var @params = JsonValue.NewObject()
                .SetProperty("path", JsonValue.String(path))
                .SetProperty("addParameters", addParameters)
                .SetProperty("addStates", addStates);
            CommandTable.Dispatch(gate, Request("animation.edit_controller", @params));
            Assert.IsTrue(controller.parameters.Any(p => p.name == "NewParam"));
            Assert.IsTrue(sm.states.Any(s => s.state.name == "NewState"));

            Undo.PerformUndo();

            Assert.IsFalse(controller.parameters.Any(p => p.name == "NewParam"));
            Assert.IsFalse(sm.states.Any(s => s.state.name == "NewState"));
        }
    }
}
