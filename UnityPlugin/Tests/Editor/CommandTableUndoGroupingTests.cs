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

        /// <summary>The external tester's own release-blocker repro, verbatim: "create three objects
        /// in one batch, press Cmd+Z once, count survivors - 0 confirms the claim, 1 refutes it."
        /// SceneApplyBatch_MultipleOperations_StillOneUndoStep_ForWholeBatch above already proves the
        /// same property with two objects; this is a deliberately literal three-object reproduction
        /// of the exact recipe the release-blocker report described, as its own individually-named,
        /// permanent regression guard - so a reader auditing that report can find the exact scenario
        /// it asked for by name, rather than having to trust that a two-object test generalises.</summary>
        [Test]
        public void SceneApplyBatch_ThreeGameObjects_OnePerformUndo_ZeroOfThreeSurvive()
        {
            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var ops = JsonValue.NewArray()
                    .Add(JsonValue.NewObject().SetProperty("op", JsonValue.String("create")).SetProperty("name", JsonValue.String("TesterOne")))
                    .Add(JsonValue.NewObject().SetProperty("op", JsonValue.String("create")).SetProperty("name", JsonValue.String("TesterTwo")))
                    .Add(JsonValue.NewObject().SetProperty("op", JsonValue.String("create")).SetProperty("name", JsonValue.String("TesterThree")));

                CommandTable.Dispatch(gate, Request("scene.apply", JsonValue.NewObject().SetProperty("operations", ops)));

                // sanity: the batch really did create all three before we undo it
                Assert.IsNotNull(GameObject.Find("TesterOne"));
                Assert.IsNotNull(GameObject.Find("TesterTwo"));
                Assert.IsNotNull(GameObject.Find("TesterThree"));

                Undo.PerformUndo(); // press Cmd+Z once

                Assert.IsNull(GameObject.Find("TesterOne"), "0 survivors confirms the claim; any survivor refutes it - the release-blocker's own recipe");
                Assert.IsNull(GameObject.Find("TesterTwo"), "0 survivors confirms the claim; any survivor refutes it - the release-blocker's own recipe");
                Assert.IsNull(GameObject.Find("TesterThree"), "0 survivors confirms the claim; any survivor refutes it - the release-blocker's own recipe");
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

        /// <summary>The explicit control the release-blocker review asked for: TWO SEPARATE
        /// scene.apply calls (not two operations inside one batch) - proving Undo grouping for THIS
        /// specific family is per-CALL, not global, and that this section's "whole batch reverts
        /// together" tests above can actually detect a difference (a methodology that always found
        /// "everything reverted" regardless of call boundaries would be worthless as a regression
        /// guard). Mirrors TwoConsecutiveMutations_SameMethod_LandInDifferentUndoGroups_
        /// OneUndoRevertsOnlyTheSecond above, but with scene.apply itself - a batch tool - on both
        /// sides of the boundary, rather than the single-object scene.create_gameobject that test
        /// uses.</summary>
        [Test]
        public void TwoConsecutiveSceneApplyBatches_OnePerformUndo_OnlySecondBatchReverts()
        {
            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var firstOps = JsonValue.NewArray()
                    .Add(JsonValue.NewObject().SetProperty("op", JsonValue.String("create")).SetProperty("name", JsonValue.String("FirstBatchObj")));
                CommandTable.Dispatch(gate, Request("scene.apply", JsonValue.NewObject().SetProperty("operations", firstOps)));

                var secondOps = JsonValue.NewArray()
                    .Add(JsonValue.NewObject().SetProperty("op", JsonValue.String("create")).SetProperty("name", JsonValue.String("SecondBatchObj")));
                CommandTable.Dispatch(gate, Request("scene.apply", JsonValue.NewObject().SetProperty("operations", secondOps)));

                Assert.IsNotNull(GameObject.Find("FirstBatchObj"));
                Assert.IsNotNull(GameObject.Find("SecondBatchObj"));

                Undo.PerformUndo(); // a single Ctrl/Cmd+Z

                Assert.IsNull(GameObject.Find("SecondBatchObj"), "a single undo must revert the SECOND scene.apply call");
                Assert.IsNotNull(GameObject.Find("FirstBatchObj"), "a single undo must NOT also revert the FIRST scene.apply call - proves per-call grouping, not global");
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

        /// <summary>The exact shape the release-blocker review additionally asked for on
        /// material.apply specifically: "multiple property sets / creates in one call". TWO 'create'
        /// ops plus a 'setProperty' op on one of the freshly-created materials, all in ONE
        /// material.apply call - mixing Undo.RegisterCreatedObjectUndo (the two creates) and
        /// Undo.RecordObject (the property set) in the SAME batch, which
        /// MaterialApplyBatch_MultipleOperations_StillOneUndoStep_ForWholeBatch above (two creates
        /// only) and MaterialApplyCommandsTests' own headline test (create+duplicate, also both
        /// creates) never exercise together.</summary>
        [Test]
        public void MaterialApplyBatch_CreatesAndPropertySet_OnePerformUndo_WholeBatchReverted()
        {
            const string scratchDir = "Assets/Tests/_HadesUndoGroupingMaterialPropertyScratch";
            if (AssetDatabase.IsValidFolder(scratchDir)) AssetDatabase.DeleteAsset(scratchDir);
            AssetDatabase.CreateFolder("Assets/Tests", "_HadesUndoGroupingMaterialPropertyScratch");
            try
            {
                var (gate, pump) = NoopGateParts();
                using (pump) using (gate)
                {
                    var pathA = scratchDir + "/PropA.mat";
                    var pathB = scratchDir + "/PropB.mat";
                    var ops = JsonValue.NewArray()
                        .Add(JsonValue.NewObject().SetProperty("op", JsonValue.String("create")).SetProperty("path", JsonValue.String(pathA)))
                        .Add(JsonValue.NewObject().SetProperty("op", JsonValue.String("create")).SetProperty("path", JsonValue.String(pathB)))
                        .Add(JsonValue.NewObject().SetProperty("op", JsonValue.String("setProperty"))
                            .SetProperty("materialPath", JsonValue.String(pathA))
                            .SetProperty("propertyName", JsonValue.String("_Metallic"))
                            .SetProperty("value", JsonValue.Float(0.5)));

                    CommandTable.Dispatch(gate, Request("material.apply", JsonValue.NewObject().SetProperty("operations", ops)));

                    // sanity: the batch really did both creates AND the property set before we undo it
                    var matA = AssetDatabase.LoadAssetAtPath<Material>(pathA);
                    Assert.IsNotNull(matA);
                    Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(pathB));
                    Assert.AreEqual(0.5f, matA.GetFloat("_Metallic"));

                    Undo.PerformUndo(); // a single Ctrl/Cmd+Z

                    Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(pathA), "the whole batch - both creates AND the property set - must revert together");
                    Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(pathB), "the second create must revert too, not just the property set");
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
