// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Linq;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// material.apply (Plan 10 Task 2's plugin-side half): one wire call carrying the WHOLE
    /// material_apply 'operations' array, applied inside ONE CommandTable.Dispatch - the headline
    /// property (a single Undo.PerformUndo reverting every operation in the spec) is provable here,
    /// against a real Undo stack, exactly as SceneApplyCommandsTests proves for scene.apply. See
    /// that file's own doc comment for why <see cref="SceneTestFixtures.ResetScene"/> is reused
    /// rather than duplicated, and MaterialCommandsTests' own doc comment for why materials
    /// additionally need a scratch asset folder per test.
    /// </summary>
    [TestFixture]
    public sealed class MaterialApplyCommandsTests
    {
        const string ScratchDir = "Assets/Tests/_HadesMaterialApplyScratch";

        [SetUp]
        public void SetUp()
        {
            SceneTestFixtures.ResetScene();
            Undo.ClearAll();
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
            AssetDatabase.CreateFolder("Assets/Tests", "_HadesMaterialApplyScratch");
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
            Assert.AreEqual(0, fake.LockCalls, "material.apply must never call Lock");
            Assert.AreEqual(0, fake.UnlockCalls, "material.apply must never call Unlock");
            Assert.IsFalse(gate.IsHeld, "material.apply must never leave a lease held");
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

        // ---------------------------------------------------------------- structural validation

        [Test]
        public void Apply_MissingOperationsArray_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var ex = Assert.Throws<ArgumentException>(() =>
                CommandTable.Dispatch(gate, Request("material.apply", JsonValue.NewObject())));

            StringAssert.Contains("operations", ex.Message);
        }

        // ---------------------------------------------------------------- full op vocabulary sweep

        /// <summary>One material.apply call touching every op this handler supports, chained so
        /// later operations act on a material an earlier one in the SAME call just created - the
        /// plugin-side "no capability lost, ordering really works, never touches the lease" proof.
        /// Also where the carried-forward Plan 9 finding is proven positively: swapShader's own
        /// result (in 'results', not just a bare 'applied' index) reports 'survivedProperties'/
        /// 'lostProperties'.</summary>
        [Test]
        public void FullOperationSweep_AppliesEveryOpInOrder_AllApplied_NoLeaseTouched()
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Cube";

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var createPath = ScratchDir + "/Foo.mat";
                var duplicatePath = ScratchDir + "/Bar.mat";

                var result = CommandTable.Dispatch(gate, Request("material.apply", Params(
                    Op("create", ("path", JsonValue.String(createPath)), ("shader", JsonValue.String("Standard"))),
                    Op("setProperty", ("materialPath", JsonValue.String(createPath)), ("propertyName", JsonValue.String("_Metallic")), ("value", JsonValue.Float(0.5))),
                    Op("assign", ("gameObjectPath", JsonValue.String("Cube")), ("materialPath", JsonValue.String(createPath))),
                    Op("duplicate", ("sourcePath", JsonValue.String(createPath)), ("destPath", JsonValue.String(duplicatePath))),
                    Op("swapShader", ("materialPath", JsonValue.String(duplicatePath)), ("shader", JsonValue.String("Unlit/Color"))))));

                CollectionAssert.AreEqual(Enumerable.Range(0, 5).ToArray(), AppliedIndices(result));
                Assert.AreEqual(0, FailedItems(result).Items.Count);
                StringAssert.Contains("5", Str(result, "summary"));

                var created = AssetDatabase.LoadAssetAtPath<Material>(createPath);
                Assert.IsNotNull(created);
                Assert.AreEqual(0.5f, created.GetFloat("_Metallic"));
                Assert.AreEqual(created, cube.GetComponent<Renderer>().sharedMaterial);

                var duplicated = AssetDatabase.LoadAssetAtPath<Material>(duplicatePath);
                Assert.IsNotNull(duplicated);
                Assert.AreEqual("Unlit/Color", duplicated.shader.name);

                // The carried-forward finding: swapShader's own result reports survived/lost
                // properties, verbatim, in THIS operation's 'results' entry.
                var results = ResultsItems(result);
                Assert.AreEqual(5, results.Items.Count);
                var swapResult = results.Items[4];
                Assert.AreEqual(4L, swapResult.TryGetProperty("index", out var idx) ? idx!.AsInteger() : -1);
                Assert.AreEqual("swapShader", Str(swapResult, "op"));
                var swapData = swapResult.TryGetProperty("result", out var r) ? r : null;
                Assert.IsNotNull(swapData);
                Assert.IsTrue(swapData.TryGetProperty("survivedProperties", out var survived) && survived.Kind == JsonValueKind.Array);
                Assert.IsTrue(swapData.TryGetProperty("lostProperties", out var lost) && lost.Kind == JsonValueKind.Array);
                // Unlit/Color has no _Metallic property, so the value this batch itself just set
                // must be reported lost, not silently dropped.
                var lostNames = lost.Items.Select(i => i.AsString()).ToList();
                CollectionAssert.Contains(lostNames, "_Metallic");

                AssertNeverTouchedLease(fake, gate);
            }
        }

        // ---------------------------------------------------------------- unknown op

        [Test]
        public void UnknownOp_RecordedAsPerOperationFailure_BatchContinues()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var path = ScratchDir + "/Good.mat";
                var result = CommandTable.Dispatch(gate, Request("material.apply", Params(
                    Op("frobnicate", ("materialPath", JsonValue.String(path))),
                    Op("create", ("path", JsonValue.String(path))))));

                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(path), "a later, valid op must still apply");
                CollectionAssert.AreEqual(new[] { 1 }, AppliedIndices(result));
                var failed = FailedItems(result);
                Assert.AreEqual(1, failed.Items.Count);
                Assert.AreEqual("frobnicate", Str(failed.Items[0], "op"));
                StringAssert.Contains("frobnicate", Str(failed.Items[0], "error"));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        // ---------------------------------------------------------------- partial failure, no rollback

        [Test]
        public void PartialFailure_MiddleOperationFails_EarlierAppliedNotRolledBack_LaterStillAttempted()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var path = ScratchDir + "/Foo.mat";
                var result = CommandTable.Dispatch(gate, Request("material.apply", Params(
                    Op("create", ("path", JsonValue.String(path))),
                    Op("setProperty", ("materialPath", JsonValue.String(path)), ("propertyName", JsonValue.String("_NoSuchProperty")), ("value", JsonValue.Float(1))),
                    Op("duplicate", ("sourcePath", JsonValue.String(path)), ("destPath", JsonValue.String(ScratchDir + "/Copy.mat"))))));

                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(path), "op 0 must have applied and must not be rolled back by op 1's failure");
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(ScratchDir + "/Copy.mat"), "op 2 must still have applied after op 1 failed");

                CollectionAssert.AreEqual(new[] { 0, 2 }, AppliedIndices(result));
                var failed = FailedItems(result);
                Assert.AreEqual(1, failed.Items.Count);
                Assert.AreEqual(1L, failed.Items[0].TryGetProperty("index", out var idx) ? idx!.AsInteger() : -1);
                StringAssert.Contains("_NoSuchProperty", Str(failed.Items[0], "error"));
            }
        }

        // ---------------------------------------------------------------- the headline: one undo, whole spec reverted

        [Test]
        public void Apply_RegistersUndoAsOneGroup_PerformUndoRevertsEveryOperation()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var createPath = ScratchDir + "/UndoMe.mat";
            var duplicatePath = ScratchDir + "/UndoMeToo.mat";

            Undo.IncrementCurrentGroup();
            CommandTable.Dispatch(gate, Request("material.apply", Params(
                Op("create", ("path", JsonValue.String(createPath))),
                Op("duplicate", ("sourcePath", JsonValue.String(createPath)), ("destPath", JsonValue.String(duplicatePath))))));

            // sanity: the batch really did both creates before we undo it
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(createPath));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(duplicatePath));

            Undo.PerformUndo(); // a single Ctrl/Cmd+Z

            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(createPath), "the whole batch must revert together");
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(duplicatePath), "the duplicate must be undone too, not just the create");
        }
    }
}
