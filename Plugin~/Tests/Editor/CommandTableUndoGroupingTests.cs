// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// Task 7's Defect 3: two back-to-back RPC-driven class-1 mutations used to land in the SAME
    /// Unity undo group, so a single Cmd/Z reverted BOTH, contradicting every class-1 tool's own
    /// description ("a single Ctrl/Cmd+Z removes it"). Cause: individual handlers never called
    /// Undo.IncrementCurrentGroup() themselves - only the three explicit batch tools did, around
    /// their OWN batch - and an RPC call never passes through the interactive GUI event cycle that
    /// normally opens a fresh group between distinct user actions. See CommandTable.Dispatch's own
    /// doc comment for the fix: one Undo.IncrementCurrentGroup() call per Dispatch, for mutating
    /// (Class 1) methods only.
    ///
    /// Every test here calls CommandTable.Dispatch directly with NO manual Undo.IncrementCurrentGroup()
    /// beforehand - unlike SceneCommandsTests/ComponentCommandsTests/etc.'s own per-tool Undo tests,
    /// which each manually increment before a SINGLE Dispatch call (proving that one call's own
    /// registration is undoable in isolation) - because the property this file exists to prove is
    /// specifically about what happens with NO manual boundary between two calls, exactly how a real
    /// RPC caller invokes Dispatch.
    /// </summary>
    [TestFixture]
    public sealed class CommandTableUndoGroupingTests
    {
        [SetUp]
        public void SetUp()
        {
            SceneTestFixtures.ResetScene();
            Undo.ClearAll();
        }

        [TearDown]
        public void TearDown() => Undo.ClearAll();

        static JsonRpcRequest Request(string method, JsonValue @params) =>
            new JsonRpcRequest { Id = JsonValue.Integer(1), Method = method, Params = @params };

        static (ReloadGate gate, MainThreadPump pump) NoopGateParts()
        {
            var pump = new MainThreadPump();
            var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            return (gate, pump);
        }

        // ---------------------------------------------------------------- the defect itself, fixed

        [Test]
        public void TwoConsecutiveMutations_SameMethod_LandInDifferentUndoGroups_OneUndoRevertsOnlyTheSecond()
        {
            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("scene.create_gameobject",
                    JsonValue.NewObject().SetProperty("name", JsonValue.String("First"))));
                CommandTable.Dispatch(gate, Request("scene.create_gameobject",
                    JsonValue.NewObject().SetProperty("name", JsonValue.String("Second"))));

                Assert.IsNotNull(GameObject.Find("First"));
                Assert.IsNotNull(GameObject.Find("Second"));

                Undo.PerformUndo(); // a single Ctrl/Cmd+Z

                Assert.IsNull(GameObject.Find("Second"), "a single undo must revert the SECOND (most recent) call");
                Assert.IsNotNull(GameObject.Find("First"), "a single undo must NOT also revert the FIRST call - this is Defect 3");
            }
        }

        [Test]
        public void TwoConsecutiveMutations_DifferentMethods_LandInDifferentUndoGroups()
        {
            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("scene.create_gameobject",
                    JsonValue.NewObject().SetProperty("name", JsonValue.String("Target"))));
                CommandTable.Dispatch(gate, Request("component.add", JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("Target"))
                    .SetProperty("componentType", JsonValue.String("BoxCollider"))));

                var target = GameObject.Find("Target");
                Assert.IsNotNull(target);
                Assert.IsNotNull(target.GetComponent<BoxCollider>());

                Undo.PerformUndo(); // a single Ctrl/Cmd+Z

                target = GameObject.Find("Target");
                Assert.IsNotNull(target, "the FIRST call (GameObject creation) must survive a single undo after the SECOND call");
                Assert.IsNull(target.GetComponent<BoxCollider>(), "the SECOND call (component add) must be what a single undo reverted");
            }
        }

        [Test]
        public void ThreeConsecutiveMutations_EachOwnGroup_ThreeUndosUnwindOneAtATime()
        {
            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("scene.create_gameobject", JsonValue.NewObject().SetProperty("name", JsonValue.String("A"))));
                CommandTable.Dispatch(gate, Request("scene.create_gameobject", JsonValue.NewObject().SetProperty("name", JsonValue.String("B"))));
                CommandTable.Dispatch(gate, Request("scene.create_gameobject", JsonValue.NewObject().SetProperty("name", JsonValue.String("C"))));

                Undo.PerformUndo();
                Assert.IsNull(GameObject.Find("C"));
                Assert.IsNotNull(GameObject.Find("B"));
                Assert.IsNotNull(GameObject.Find("A"));

                Undo.PerformUndo();
                Assert.IsNull(GameObject.Find("B"));
                Assert.IsNotNull(GameObject.Find("A"));

                Undo.PerformUndo();
                Assert.IsNull(GameObject.Find("A"));
            }
        }

        // ---------------------------------------------------------------- no regression: batch tools stay one step

        [Test]
        public void SceneSetupBatch_MultipleGameObjects_StillOneUndoStep_ForWholeBatch()
        {
            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("gameObjects", JsonValue.NewArray()
                    .Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("BatchA")))
                    .Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("BatchB"))));

                CommandTable.Dispatch(gate, Request("scene.setup", @params));

                Assert.IsNotNull(GameObject.Find("BatchA"));
                Assert.IsNotNull(GameObject.Find("BatchB"));

                Undo.PerformUndo(); // a single Ctrl/Cmd+Z

                Assert.IsNull(GameObject.Find("BatchA"), "the whole batch must revert together");
                Assert.IsNull(GameObject.Find("BatchB"), "the whole batch must revert together");
            }
        }

        [Test]
        public void MutationBeforeAndAfterABatch_BatchStillOneStep_NeighboursUnaffected()
        {
            // The batch tool's own internal IncrementCurrentGroup must not "leak" into its
            // neighbours on either side, now that Dispatch ALSO increments before every call.
            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("scene.create_gameobject", JsonValue.NewObject().SetProperty("name", JsonValue.String("Before"))));

                var @params = JsonValue.NewObject().SetProperty("gameObjects", JsonValue.NewArray()
                    .Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("BatchA")))
                    .Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("BatchB"))));
                CommandTable.Dispatch(gate, Request("scene.setup", @params));

                CommandTable.Dispatch(gate, Request("scene.create_gameobject", JsonValue.NewObject().SetProperty("name", JsonValue.String("After"))));

                Undo.PerformUndo(); // reverts "After" only
                Assert.IsNull(GameObject.Find("After"));
                Assert.IsNotNull(GameObject.Find("BatchA"));
                Assert.IsNotNull(GameObject.Find("BatchB"));
                Assert.IsNotNull(GameObject.Find("Before"));

                Undo.PerformUndo(); // reverts the WHOLE batch in one step
                Assert.IsNull(GameObject.Find("BatchA"));
                Assert.IsNull(GameObject.Find("BatchB"));
                Assert.IsNotNull(GameObject.Find("Before"));

                Undo.PerformUndo(); // reverts "Before" only
                Assert.IsNull(GameObject.Find("Before"));
            }
        }

        // ---------------------------------------------------------------- scene.apply: the same no-double-group proof

        /// <summary>scene.apply (Plan 10 Task 1) is a registered MutatingMethods entry, exactly like
        /// scene.setup/component.set_properties/animation.edit_controller above - Dispatch increments
        /// once before calling it, and the handler ALSO increments once itself before doing any work.
        /// Same proof as SceneSetupBatch_MultipleGameObjects_StillOneUndoStep_ForWholeBatch: the two
        /// increments collapse into one harmless empty leading group, never a second REAL one, so a
        /// single Ctrl/Cmd+Z still reverts the WHOLE batch.</summary>
        [Test]
        public void SceneApplyBatch_MultipleOperations_StillOneUndoStep_ForWholeBatch()
        {
            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var ops = JsonValue.NewArray()
                    .Add(JsonValue.NewObject().SetProperty("op", JsonValue.String("create")).SetProperty("name", JsonValue.String("BatchA")))
                    .Add(JsonValue.NewObject().SetProperty("op", JsonValue.String("create")).SetProperty("name", JsonValue.String("BatchB")));

                CommandTable.Dispatch(gate, Request("scene.apply", JsonValue.NewObject().SetProperty("operations", ops)));

                Assert.IsNotNull(GameObject.Find("BatchA"));
                Assert.IsNotNull(GameObject.Find("BatchB"));

                Undo.PerformUndo(); // a single Ctrl/Cmd+Z

                Assert.IsNull(GameObject.Find("BatchA"), "the whole scene.apply batch must revert together");
                Assert.IsNull(GameObject.Find("BatchB"), "the whole scene.apply batch must revert together");
            }
        }

        /// <summary>Mirrors MutationBeforeAndAfterABatch_BatchStillOneStep_NeighboursUnaffected above,
        /// with scene.apply as the batch: proves Dispatch's own pre-increment for scene.apply does not
        /// bleed into (merge with) the PRECEDING call's group, and the batch's internal increment does
        /// not bleed into the FOLLOWING call's group either - three clean, independently-revertible
        /// steps.</summary>
        [Test]
        public void MutationBeforeAndAfterASceneApplyBatch_BatchStillOneStep_NeighboursUnaffected()
        {
            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("scene.create_gameobject", JsonValue.NewObject().SetProperty("name", JsonValue.String("Before"))));

                var ops = JsonValue.NewArray()
                    .Add(JsonValue.NewObject().SetProperty("op", JsonValue.String("create")).SetProperty("name", JsonValue.String("BatchA")))
                    .Add(JsonValue.NewObject().SetProperty("op", JsonValue.String("create")).SetProperty("name", JsonValue.String("BatchB")));
                CommandTable.Dispatch(gate, Request("scene.apply", JsonValue.NewObject().SetProperty("operations", ops)));

                CommandTable.Dispatch(gate, Request("scene.create_gameobject", JsonValue.NewObject().SetProperty("name", JsonValue.String("After"))));

                Undo.PerformUndo(); // reverts "After" only
                Assert.IsNull(GameObject.Find("After"));
                Assert.IsNotNull(GameObject.Find("BatchA"));
                Assert.IsNotNull(GameObject.Find("BatchB"));
                Assert.IsNotNull(GameObject.Find("Before"));

                Undo.PerformUndo(); // reverts the WHOLE scene.apply batch in one step
                Assert.IsNull(GameObject.Find("BatchA"));
                Assert.IsNull(GameObject.Find("BatchB"));
                Assert.IsNotNull(GameObject.Find("Before"));

                Undo.PerformUndo(); // reverts "Before" only
                Assert.IsNull(GameObject.Find("Before"));
            }
        }

        // ---------------------------------------------------------------- material.apply / animation.apply: the same no-double-group proof

        /// <summary>material.apply (Plan 10 Task 2) is a registered MutatingMethods entry, exactly
        /// like scene.apply above - same "two increments collapse into one harmless empty leading
        /// group" proof. Manages its own scratch asset folder locally (rather than via this
        /// fixture's shared SetUp/TearDown, which only resets the scene) since materials are
        /// assets, not scene objects - cleaned up in a finally so a failed assertion never leaks it
        /// into a later test.</summary>
        [Test]
        public void MaterialApplyBatch_MultipleOperations_StillOneUndoStep_ForWholeBatch()
        {
            const string scratchDir = "Assets/Tests/_HadesUndoGroupingMaterialScratch";
            if (AssetDatabase.IsValidFolder(scratchDir)) AssetDatabase.DeleteAsset(scratchDir);
            AssetDatabase.CreateFolder("Assets/Tests", "_HadesUndoGroupingMaterialScratch");
            try
            {
                var (gate, pump) = NoopGateParts();
                using (pump) using (gate)
                {
                    var pathA = scratchDir + "/BatchA.mat";
                    var pathB = scratchDir + "/BatchB.mat";
                    var ops = JsonValue.NewArray()
                        .Add(JsonValue.NewObject().SetProperty("op", JsonValue.String("create")).SetProperty("path", JsonValue.String(pathA)))
                        .Add(JsonValue.NewObject().SetProperty("op", JsonValue.String("create")).SetProperty("path", JsonValue.String(pathB)));

                    CommandTable.Dispatch(gate, Request("material.apply", JsonValue.NewObject().SetProperty("operations", ops)));

                    Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(pathA));
                    Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(pathB));

                    Undo.PerformUndo(); // a single Ctrl/Cmd+Z

                    Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(pathA), "the whole material.apply batch must revert together");
                    Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(pathB), "the whole material.apply batch must revert together");
                }
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(scratchDir)) AssetDatabase.DeleteAsset(scratchDir);
            }
        }

        /// <summary>animation.apply (Plan 10 Task 2) is a registered MutatingMethods entry, exactly
        /// like scene.apply/material.apply above - same proof.</summary>
        [Test]
        public void AnimationApplyBatch_MultipleOperations_StillOneUndoStep_ForWholeBatch()
        {
            const string scratchDir = "Assets/Tests/_HadesUndoGroupingAnimationScratch";
            if (AssetDatabase.IsValidFolder(scratchDir)) AssetDatabase.DeleteAsset(scratchDir);
            AssetDatabase.CreateFolder("Assets/Tests", "_HadesUndoGroupingAnimationScratch");
            try
            {
                var (gate, pump) = NoopGateParts();
                using (pump) using (gate)
                {
                    var pathA = scratchDir + "/BatchA.controller";
                    var pathB = scratchDir + "/BatchB.controller";
                    var ops = JsonValue.NewArray()
                        .Add(JsonValue.NewObject().SetProperty("op", JsonValue.String("createController")).SetProperty("controllerPath", JsonValue.String(pathA)))
                        .Add(JsonValue.NewObject().SetProperty("op", JsonValue.String("createController")).SetProperty("controllerPath", JsonValue.String(pathB)));

                    CommandTable.Dispatch(gate, Request("animation.apply", JsonValue.NewObject().SetProperty("operations", ops)));

                    Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(pathA));
                    Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(pathB));

                    Undo.PerformUndo(); // a single Ctrl/Cmd+Z

                    Assert.IsNull(AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(pathA), "the whole animation.apply batch must revert together");
                    Assert.IsNull(AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(pathB), "the whole animation.apply batch must revert together");
                }
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(scratchDir)) AssetDatabase.DeleteAsset(scratchDir);
            }
        }

        // ---------------------------------------------------------------- non-mutating methods: no boundary

        [Test]
        public void NonMutatingMethod_LeaseAcquire_DoesNotIncrementUndoGroup()
        {
            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var before = Undo.GetCurrentGroup();

                CommandTable.Dispatch(gate, Request("lease.acquire", JsonValue.NewObject().SetProperty("leaseId", JsonValue.String("probe-lease"))));

                Assert.AreEqual(before, Undo.GetCurrentGroup(), "a lease.* bookkeeping call must never open an Undo group");
            }
        }

        [Test]
        public void NonMutatingMethod_ConsoleLogRead_DoesNotIncrementUndoGroup()
        {
            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var before = Undo.GetCurrentGroup();

                CommandTable.Dispatch(gate, Request("project.get_console_log", JsonValue.NewObject()));

                Assert.AreEqual(before, Undo.GetCurrentGroup(), "a class-4 read must never open an Undo group");
            }
        }
    }
}
