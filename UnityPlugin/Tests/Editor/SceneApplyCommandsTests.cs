// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Linq;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// scene.apply (Plan 10 Task 1's plugin-side half): one wire call carrying the WHOLE
    /// scene_apply 'operations' array, applied inside ONE CommandTable.Dispatch, so the headline
    /// property SceneApplyTests (Hades.Server.Tests) could not prove from the app side - a single
    /// Undo.PerformUndo reverting every operation in the spec, not just the last one - is provable
    /// here, against a real Undo stack. See SceneCommandsTests' own doc comment for why every
    /// mutation test here also proves the result + never-touches-the-lease shape, and for why
    /// <see cref="SceneTestFixtures.ResetScene"/> is reused rather than duplicated.
    ///
    /// The "one Undo group for the whole batch, even though CommandTable.Dispatch ALSO increments
    /// once before calling this handler at all" cross-cutting property (scene.apply is a registered
    /// MutatingMethods entry, exactly like scene.setup/component.set_properties/animation.edit_
    /// controller) is proven in CommandTableUndoGroupingTests instead, alongside those three - this
    /// file's own <see cref="Apply_RegistersUndoAsOneGroup_PerformUndoRevertsEveryOperation"/>
    /// proves the same thing in isolation (a manual pre-increment, then one Dispatch call, matching
    /// SceneCommandsTests/ComponentCommandsTests' own per-tool "RegistersUndo" tests).
    /// </summary>
    [TestFixture]
    public sealed class SceneApplyCommandsTests
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

        static (ReloadGate gate, FakeEditorLockApi fake, MainThreadPump pump) NoopGateParts()
        {
            var fake = new FakeEditorLockApi();
            var pump = new MainThreadPump();
            var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            return (gate, fake, pump);
        }

        static void AssertNeverTouchedLease(FakeEditorLockApi fake, ReloadGate gate)
        {
            Assert.AreEqual(0, fake.LockCalls, "scene.apply must never call Lock");
            Assert.AreEqual(0, fake.UnlockCalls, "scene.apply must never call Unlock");
            Assert.IsFalse(gate.IsHeld, "scene.apply must never leave a lease held");
        }

        /// <summary>Builds one 'operations' array entry: {op, ...fields}. Mirrors SceneApplyTests
        /// (Hades.Server.Tests)' own Obj() helper, with 'op' folded in since every entry needs one.</summary>
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

        static string Str(JsonValue obj, string key) =>
            obj.TryGetProperty(key, out var v) && v!.Kind == JsonValueKind.String ? v.AsString() : null;

        // ---------------------------------------------------------------- structural validation

        [Test]
        public void Apply_MissingOperationsArray_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var ex = Assert.Throws<ArgumentException>(() =>
                CommandTable.Dispatch(gate, Request("scene.apply", JsonValue.NewObject())));

            StringAssert.Contains("operations", ex.Message);
        }

        [Test]
        public void Apply_OperationsNotAnArray_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("operations", JsonValue.String("nope"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("scene.apply", @params)));

            StringAssert.Contains("operations", ex.Message);
        }

        // ---------------------------------------------------------------- op: create

        [Test]
        public void Create_SecondOperationParentsUnderFirstOperationsNewObject()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var result = CommandTable.Dispatch(gate, Request("scene.apply", Params(
                Op("create", ("name", JsonValue.String("Root"))),
                Op("create", ("name", JsonValue.String("Child")), ("parent", JsonValue.String("Root"))))));

            var root = GameObject.Find("Root");
            Assert.IsNotNull(root);
            Assert.IsNotNull(root.transform.Find("Child"));
            CollectionAssert.AreEqual(new[] { 0, 1 }, AppliedIndices(result));
        }

        [Test]
        public void Create_WithTagAndLayer_NoPrimitive_SetsBoth()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var result = CommandTable.Dispatch(gate, Request("scene.apply", Params(
                Op("create", ("name", JsonValue.String("Tagged")), ("tag", JsonValue.String("Player")), ("layer", JsonValue.String("Default"))))));

            var go = GameObject.Find("Tagged");
            Assert.IsNotNull(go);
            Assert.AreEqual("Player", go.tag);
            Assert.AreEqual(LayerMask.NameToLayer("Default"), go.layer);
            CollectionAssert.AreEqual(new[] { 0 }, AppliedIndices(result));
        }

        [Test]
        public void Create_PrimitiveCombinedWithTagOrLayer_IsThisOperationsFailure_BatchContinues()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var result = CommandTable.Dispatch(gate, Request("scene.apply", Params(
                Op("create", ("name", JsonValue.String("Bad")), ("primitive", JsonValue.String("Cube")), ("tag", JsonValue.String("Player"))),
                Op("create", ("name", JsonValue.String("Good"))))));

            Assert.IsNull(GameObject.Find("Bad"), "the conflicting op must not create anything");
            Assert.IsNotNull(GameObject.Find("Good"), "a later, valid op must still apply");

            CollectionAssert.AreEqual(new[] { 1 }, AppliedIndices(result));
            var failed = FailedItems(result);
            Assert.AreEqual(1, failed.Items.Count);
            Assert.AreEqual(0L, failed.Items[0].TryGetProperty("index", out var idx) ? idx!.AsInteger() : -1);
            StringAssert.Contains("primitive", Str(failed.Items[0], "error"));
        }

        // ---------------------------------------------------------------- op: setProperties

        [Test]
        public void SetProperties_OnePropertyFails_WholeOperationFailed_ButSuccessfulPropertyStillApplied()
        {
            var enemy = new GameObject("Enemy");
            enemy.AddComponent<Rigidbody>();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var result = CommandTable.Dispatch(gate, Request("scene.apply", Params(
                Op("setProperties", ("target", JsonValue.String("Enemy")), ("component", JsonValue.String("Rigidbody")),
                    ("values", JsonValue.NewObject().SetProperty("mass", JsonValue.Float(7)).SetProperty("bogus", JsonValue.Float(1)))))));

            Assert.AreEqual(7f, enemy.GetComponent<Rigidbody>().mass,
                "the valid property must still be applied - a failed sibling property does not roll it back");

            CollectionAssert.AreEqual(Array.Empty<int>(), AppliedIndices(result));
            var failed = FailedItems(result);
            Assert.AreEqual(1, failed.Items.Count);
            StringAssert.Contains("bogus", Str(failed.Items[0], "error"));
        }

        // ---------------------------------------------------------------- unknown op

        [Test]
        public void UnknownOp_RecordedAsPerOperationFailure_BatchContinues()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var result = CommandTable.Dispatch(gate, Request("scene.apply", Params(
                Op("frobnicate", ("target", JsonValue.String("Nope"))),
                Op("create", ("name", JsonValue.String("Enemy"))))));

            Assert.IsNotNull(GameObject.Find("Enemy"), "a later, valid op must still apply");
            CollectionAssert.AreEqual(new[] { 1 }, AppliedIndices(result));
            var failed = FailedItems(result);
            Assert.AreEqual(1, failed.Items.Count);
            Assert.AreEqual("frobnicate", Str(failed.Items[0], "op"));
            StringAssert.Contains("frobnicate", Str(failed.Items[0], "error"));
        }

        // ---------------------------------------------------------------- partial failure, ordering, no rollback

        [Test]
        public void PartialFailure_MiddleOperationFails_EarlierAppliedNotRolledBack_LaterStillAttempted()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var result = CommandTable.Dispatch(gate, Request("scene.apply", Params(
                Op("create", ("name", JsonValue.String("Enemy"))),
                Op("addComponent", ("target", JsonValue.String("Ghost")), ("type", JsonValue.String("Rigidbody"))),
                Op("select", ("target", JsonValue.String("Enemy"))))));

            var enemy = GameObject.Find("Enemy");
            Assert.IsNotNull(enemy, "op 0 must have applied and must not be rolled back by op 1's failure");
            Assert.AreEqual(enemy, Selection.activeGameObject, "op 2 must still have applied after op 1 failed");

            CollectionAssert.AreEqual(new[] { 0, 2 }, AppliedIndices(result));
            var failed = FailedItems(result);
            Assert.AreEqual(1, failed.Items.Count);
            Assert.AreEqual(1L, failed.Items[0].TryGetProperty("index", out var idx) ? idx!.AsInteger() : -1);
            Assert.AreEqual("addComponent", Str(failed.Items[0], "op"));
            StringAssert.Contains("Ghost", Str(failed.Items[0], "error"));
        }

        // ---------------------------------------------------------------- the headline: one undo, whole spec reverted

        [Test]
        public void Apply_RegistersUndoAsOneGroup_PerformUndoRevertsEveryOperation()
        {
            new GameObject("OldEnemy");
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            CommandTable.Dispatch(gate, Request("scene.apply", Params(
                Op("create", ("name", JsonValue.String("Enemy"))),
                Op("addComponent", ("target", JsonValue.String("Enemy")), ("type", JsonValue.String("Rigidbody"))),
                Op("setProperties", ("target", JsonValue.String("Enemy")), ("component", JsonValue.String("Rigidbody")),
                    ("values", JsonValue.NewObject().SetProperty("mass", JsonValue.Float(5)))),
                Op("rename", ("target", JsonValue.String("Enemy")), ("newName", JsonValue.String("Enemy_01"))),
                Op("delete", ("target", JsonValue.String("OldEnemy"))))));

            // sanity: the batch really did all of this before we undo it
            Assert.IsNull(GameObject.Find("Enemy"));
            var renamed = GameObject.Find("Enemy_01");
            Assert.IsNotNull(renamed);
            Assert.AreEqual(5f, renamed.GetComponent<Rigidbody>().mass);
            Assert.IsNull(GameObject.Find("OldEnemy"));

            Undo.PerformUndo(); // a single Ctrl/Cmd+Z

            Assert.IsNull(GameObject.Find("Enemy_01"), "the whole batch must revert together");
            Assert.IsNull(GameObject.Find("Enemy"), "the create must be undone too, not just the rename");
            Assert.IsNotNull(GameObject.Find("OldEnemy"), "the delete must be undone - OldEnemy recreated");
        }

        // ---------------------------------------------------------------- full op vocabulary sweep

        /// <summary>One scene.apply call touching every op this handler supports, chained so later
        /// operations act on GameObjects/components earlier ones in the SAME call created, added, or
        /// wired - the plugin-side "no capability lost, and ordering really works end to end" proof
        /// that mirrors SceneApplyTests' own FullOperationSweep test on the app side (which can only
        /// prove the wire params were built correctly, not that a real Editor accepts and applies
        /// them).</summary>
        [Test]
        public void FullOperationSweep_AppliesEveryOpInOrder_AllApplied_NoLeaseTouched()
        {
            new GameObject("OldObj");
            var manager = new GameObject("Manager");
            manager.AddComponent<ListenerTarget>();
            var container = new GameObject("Container");
            var image = new GameObject("TargetImage").AddComponent<Image>();

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("scene.apply", Params(
                    Op("create", ("name", JsonValue.String("Enemy")), ("primitive", JsonValue.String("Cube"))),
                    Op("addComponent", ("target", JsonValue.String("Enemy")), ("type", JsonValue.String("Rigidbody"))),
                    Op("addComponent", ("target", JsonValue.String("Enemy")), ("type", JsonValue.String("Button"))),
                    Op("setProperties", ("target", JsonValue.String("Enemy")), ("component", JsonValue.String("Rigidbody")),
                        ("values", JsonValue.NewObject().SetProperty("mass", JsonValue.Float(5)))),
                    Op("setReference", ("target", JsonValue.String("Enemy")), ("component", JsonValue.String("Button")),
                        ("property", JsonValue.String("m_TargetGraphic")), ("targetPath", JsonValue.String("TargetImage")),
                        ("targetComponentType", JsonValue.String("Image"))),
                    Op("addListener", ("target", JsonValue.String("Enemy")), ("component", JsonValue.String("Button")),
                        ("event", JsonValue.String("m_OnClick")), ("targetObject", JsonValue.String("Manager")), ("method", JsonValue.String("DoThing"))),
                    Op("removeListener", ("target", JsonValue.String("Enemy")), ("component", JsonValue.String("Button")),
                        ("event", JsonValue.String("m_OnClick")), ("index", JsonValue.Integer(0))),
                    Op("removeComponent", ("target", JsonValue.String("Enemy")), ("type", JsonValue.String("BoxCollider"))),
                    Op("delete", ("target", JsonValue.String("OldObj"))),
                    Op("reparent", ("target", JsonValue.String("Enemy")), ("newParent", JsonValue.String("Container"))),
                    Op("rename", ("target", JsonValue.String("Container/Enemy")), ("newName", JsonValue.String("Enemy_01"))),
                    Op("select", ("target", JsonValue.String("Container/Enemy_01"))))));

                CollectionAssert.AreEqual(Enumerable.Range(0, 12).ToArray(), AppliedIndices(result));
                Assert.AreEqual(0, FailedItems(result).Items.Count);
                StringAssert.Contains("12", Str(result, "summary"));

                Assert.IsNull(GameObject.Find("OldObj"));
                Assert.IsNull(GameObject.Find("Enemy"));
                var enemyTransform = container.transform.Find("Enemy_01");
                Assert.IsNotNull(enemyTransform, "renamed+reparented object must exist at Container/Enemy_01");
                var enemy = enemyTransform.gameObject;

                Assert.AreEqual(5f, enemy.GetComponent<Rigidbody>().mass);
                Assert.IsNull(enemy.GetComponent<BoxCollider>(), "removeComponent must have removed it");
                Assert.IsNotNull(enemy.GetComponent<MeshRenderer>(), "untouched primitive component must remain");
                Assert.AreSame(image, enemy.GetComponent<Button>().targetGraphic);
                Assert.AreEqual(0, enemy.GetComponent<Button>().onClick.GetPersistentEventCount(), "added then removed");
                Assert.AreEqual(enemy, Selection.activeGameObject);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        // ---------------------------------------------------------------- reparent op - cycle guard (F21)

        /// <summary>Uneven-validation audit: pins that scene_apply's 'reparent' op inherits
        /// SceneCommands.ReparentGameObject's own self/descendant cycle guard (F21) by DELEGATING to
        /// it (see SceneApplyCommands.DoReparent) rather than reimplementing reparent logic - this is
        /// the exact scenario the external tester's release-blocker report described ("scene_apply
        /// accepted a self-parent"), already closed by construction, but until this test existed
        /// nothing in this file pinned it AT the batch-tool layer - only SceneCommandsTests did, one
        /// level down, against scene.reparent_gameobject directly.</summary>
        [Test]
        public void ReparentOp_UnderItself_RecordedAsOperationFailure_NotSilentlyAccepted()
        {
            var go = new GameObject("SelfParent");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("scene.apply", Params(
                    Op("reparent", ("target", JsonValue.String("SelfParent")), ("newParent", JsonValue.String("SelfParent"))))));

                Assert.AreEqual(0, AppliedIndices(result).Length);
                var failed = FailedItems(result);
                Assert.AreEqual(1, failed.Items.Count);
                StringAssert.Contains("SelfParent", Str(failed.Items[0], "error"));
                Assert.IsNull(go.transform.parent);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        // ---------------------------------------------------------------- 'results': deliberately NOT added yet

        /// <summary>Pins a DELIBERATE non-convergence, not an overlooked one - see
        /// Documentation/MutationToolValidation.md gap #4. scene.apply is the only one of the seven
        /// apply/manage batch families that still has no 'results' array alongside 'applied'/
        /// 'failed'/'summary': material.apply/animation.apply/prefab.apply/asset.manage/scene.manage/
        /// projectSettings.apply all echo each successful operation's own result payload in 'results'
        /// (index/op/result - see e.g. MaterialApplyCommandsTests' own ResultsItems-based
        /// assertions), and scene.apply does not.
        ///
        /// Investigated and left this way rather than converged: scripts/regression/fixtures/
        /// editor-routed.json has four already-recorded scene.apply replay entries whose 'expected'
        /// value has exactly three top-level members (applied/failed/summary). hades_regression's
        /// legacy replay path (ProjectCommands.RegressionReplay, UnityPlugin) compares the actual
        /// dispatch result against that recorded 'expected' with ProjectCommands.JsonValueEquals,
        /// whose Object case starts "if (a.Members.Count != b.Members.Count) return false" - an
        /// EXACT member-count match, not a subset/tolerant one. Adding a 'results' member here would
        /// make every one of those four recorded entries stop matching on replay: all four have at
        /// least one applied op, so all four would gain a non-empty 'results' array and a member
        /// count of 4 where the recorded 'expected' has 3. Fixing the comparator to tolerate
        /// additive members, or re-recording the fixture with the new shape, both require editing
        /// ProjectCommands.cs and/or scripts/regression/fixtures/editor-routed.json - files outside
        /// this task's ownership boundary (a sibling owns ProjectCommands.cs/EditorProjectTools.cs;
        /// scripts/ is off-limits entirely). This test exists so a future change that adds 'results'
        /// here is a deliberate, coordinated one (paired with that fixture/comparator work), not an
        /// accidental extra key that silently breaks fixture replay.</summary>
        [Test]
        public void Apply_ResponseHasNoResultsKey_DeliberatelyUnconvergedPendingFixtureCoordination()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var result = CommandTable.Dispatch(gate, Request("scene.apply", Params(
                Op("create", ("name", JsonValue.String("ResultsKeyCheck"))))));

            Assert.IsFalse(result.TryGetProperty("results", out _),
                "scene.apply intentionally has no 'results' key yet - see this test's own doc comment for why");
        }

        /// <summary>Listener-method host for the addListener/removeListener ops - needs at least one
        /// public void, zero-arg method a persistent UnityEvent call can bind to. Deliberately a
        /// second, private copy rather than promoting ComponentCommandsTests' own identical
        /// ListenerTarget to shared - see that file's own doc comment; this is the same "trivial,
        /// single-purpose fixture" judgment call, made twice rather than once.</summary>
        sealed class ListenerTarget : MonoBehaviour
        {
            public void DoThing() { }
        }
    }
}
