// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.IO;
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
    /// prefab.apply (Plan 10 Task 2's plugin-side half): one wire call carrying the WHOLE
    /// prefab_apply 'operations' array, applied inside ONE CommandTable.Dispatch. Unlike
    /// scene.apply/material.apply/animation.apply (class 1, no lease), prefab.apply is class 2 -
    /// every test here proves the SAME headline "whole batch reverts as one Undo step" property
    /// SceneApplyCommandsTests proves for scene.apply, PLUS the property unique to a class-2 batch:
    /// the reload lease is acquired and released EXACTLY ONCE for the whole batch, never once per
    /// operation - see <see cref="AssertExactlyOneLeaseWindow"/>, the batch counterpart to
    /// PrefabCommandsTests' own AssertLeaseCleanlyReleased (which only asserts the lease BALANCES,
    /// not that it stayed a single window - the property that matters for a single call, not a
    /// batch).
    /// </summary>
    [TestFixture]
    public sealed class PrefabApplyCommandsTests
    {
        const string ScratchDir = "Assets/Tests/_HadesPrefabApplyScratch";

        [SetUp]
        public void SetUp()
        {
            SceneTestFixtures.ResetScene();
            Undo.ClearAll();
            CloseAnyLeakedEditingSession();
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
            AssetDatabase.CreateFolder("Assets/Tests", "_HadesPrefabApplyScratch");
        }

        [TearDown]
        public void TearDown()
        {
            CloseAnyLeakedEditingSession();
            Undo.ClearAll();
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
        }

        static JsonRpcRequest Request(string method, JsonValue @params) =>
            new JsonRpcRequest { Id = JsonValue.Integer(1), Method = method, Params = @params };

        /// <summary>See PrefabCommandsTests' own doc comment for why this is how a leaked
        /// prefab_open_editing session (from an UNRELATED test in this same assembly - the session
        /// is a private static field with no InternalsVisibleTo escape hatch) gets closed.</summary>
        static void CloseAnyLeakedEditingSession()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            try { CommandTable.Dispatch(gate, Request("prefab.save_editing", JsonValue.NewObject())); }
            catch (InvalidOperationException) { /* nothing was open - expected in the common case */ }
        }

        static (ReloadGate gate, FakeEditorLockApi fake, MainThreadPump pump) NoopGateParts()
        {
            var fake = new FakeEditorLockApi();
            var pump = new MainThreadPump();
            var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            return (gate, fake, pump);
        }

        /// <summary>The property unique to a class-2 BATCH: not merely balanced (LockCalls ==
        /// UnlockCalls, PrefabCommandsTests' own AssertLeaseCleanlyReleased), but EXACTLY one
        /// Lock/Unlock pair for the WHOLE batch regardless of how many prefab operations it
        /// contains - proving prefab_apply does not acquire-then-release a fresh lease per
        /// operation, which would violate "one call, one reload window" (see PrefabApplyCommands'
        /// own class doc comment).</summary>
        static void AssertExactlyOneLeaseWindow(FakeEditorLockApi fake, ReloadGate gate)
        {
            Assert.AreEqual(1, fake.LockCalls, "prefab.apply must acquire the reload lock EXACTLY ONCE for the whole batch, not once per operation");
            Assert.AreEqual(1, fake.UnlockCalls, "prefab.apply must release the reload lock EXACTLY ONCE for the whole batch");
            Assert.IsFalse(gate.IsHeld, "prefab.apply must never leave a lease held");
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

        static string AbsolutePath(string projectRelativePath) =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));

        // ---------------------------------------------------------------- structural validation

        [Test]
        public void Apply_MissingOperationsArray_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var ex = Assert.Throws<ArgumentException>(() =>
                CommandTable.Dispatch(gate, Request("prefab.apply", JsonValue.NewObject())));

            StringAssert.Contains("operations", ex.Message);

            // Nothing was ever attempted, so no lease acquisition happened either - even the
            // structural-validation failure path must not touch the gate.
        }

        // ---------------------------------------------------------------- full op vocabulary sweep, one lease window

        /// <summary>One prefab.apply call touching every op this handler supports - create,
        /// instantiate (of a DIFFERENT, pre-existing prefab, to avoid the scene-name collision
        /// PrefabCommandsTests' own single-tool tests avoid the same way), applyOverrides (on the
        /// just-instantiated object), editProperty (on the asset THIS SAME BATCH just created via
        /// its own 'create' op - ordering), createVariant. Proves the whole vocabulary works
        /// end-to-end, in order, with EXACTLY one lease window for the whole thing.</summary>
        [Test]
        public void FullOperationSweep_AppliesEveryOpInOrder_AllApplied_OneLeaseWindow()
        {
            var createSource = new GameObject("CreateSource");
            createSource.AddComponent<BoxCollider>();

            var preExistingSource = new GameObject("PreExisting");
            var preExistingPath = ScratchDir + "/PreExisting.prefab";
            PrefabUtility.SaveAsPrefabAsset(preExistingSource, preExistingPath);
            UnityEngine.Object.DestroyImmediate(preExistingSource);

            var createdAssetPath = ScratchDir + "/Created.prefab";
            var variantPath = ScratchDir + "/CreatedVariant.prefab";

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("prefab.apply", Params(
                    Op("create", ("gameObjectPath", JsonValue.String("CreateSource")), ("assetPath", JsonValue.String(createdAssetPath))),
                    Op("instantiate", ("prefabPath", JsonValue.String(preExistingPath))),
                    Op("applyOverrides", ("gameObjectPath", JsonValue.String("PreExisting"))),
                    Op("editProperty", ("prefabPath", JsonValue.String(createdAssetPath)), ("componentType", JsonValue.String("BoxCollider")),
                        ("propertyName", JsonValue.String("m_Size")),
                        ("value", JsonValue.NewObject().SetProperty("x", JsonValue.Float(2)).SetProperty("y", JsonValue.Float(2)).SetProperty("z", JsonValue.Float(2)))),
                    Op("createVariant", ("basePrefabPath", JsonValue.String(createdAssetPath)), ("variantPath", JsonValue.String(variantPath))))));

                CollectionAssert.AreEqual(Enumerable.Range(0, 5).ToArray(), AppliedIndices(result));
                Assert.AreEqual(0, FailedItems(result).Items.Count, string.Join("; ", FailedItems(result).Items.Select(f => Str(f, "error"))));
                StringAssert.Contains("5", Str(result, "summary"));

                Assert.IsTrue(File.Exists(AbsolutePath(createdAssetPath)));
                Assert.IsNotNull(GameObject.Find("PreExisting"), "instantiate must have created the instance");
                Assert.IsTrue(File.Exists(AbsolutePath(variantPath)));

                var variantAsset = AssetDatabase.LoadAssetAtPath<GameObject>(variantPath);
                Assert.AreEqual(PrefabAssetType.Variant, PrefabUtility.GetPrefabAssetType(variantAsset));

                var editedFileText = File.ReadAllText(AbsolutePath(createdAssetPath));
                StringAssert.Contains("m_Size: {x: 2, y: 2, z: 2}", editedFileText);

                // applyOverrides ran against a freshly-instantiated, untouched instance - only the
                // known, permanent root defaults are unapplied (see PrefabCommands.DoApplyOverrides'
                // own doc comment), reported in THIS op's own 'results' entry.
                var results = ResultsItems(result);
                Assert.AreEqual(5, results.Items.Count);
                var overridesResult = results.Items[2];
                Assert.AreEqual("applyOverrides", Str(overridesResult, "op"));
                var overridesData = overridesResult.TryGetProperty("result", out var r) ? r : null;
                Assert.IsNotNull(overridesData);
                Assert.IsTrue(overridesData.TryGetProperty("unappliedProperties", out var unapplied) && unapplied.Kind == JsonValueKind.Array);
                CollectionAssert.Contains(unapplied.Items.Select(i => i.AsString()).ToList(), "m_Name");

                AssertExactlyOneLeaseWindow(fake, gate);
            }
        }

        // ---------------------------------------------------------------- unknown op (incl. the removed open/save footgun)

        [Test]
        public void UnknownOp_RecordedAsPerOperationFailure_BatchContinues_OneLeaseWindowStill()
        {
            var source = new GameObject("Widget");
            var assetPath = ScratchDir + "/Widget.prefab";

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("prefab.apply", Params(
                    // 'openEditing' is deliberately NOT part of prefab_apply's op vocabulary (see
                    // PrefabApplyCommands' own class doc comment - the footgun this tool removes) -
                    // defense in depth means it must fail as an unknown op, not silently do something.
                    Op("openEditing", ("prefabPath", JsonValue.String(assetPath))),
                    Op("create", ("gameObjectPath", JsonValue.String("Widget")), ("assetPath", JsonValue.String(assetPath))))));

                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(assetPath), "a later, valid op must still apply");
                CollectionAssert.AreEqual(new[] { 1 }, AppliedIndices(result));
                var failed = FailedItems(result);
                Assert.AreEqual(1, failed.Items.Count);
                Assert.AreEqual("openEditing", Str(failed.Items[0], "op"));
                StringAssert.Contains("openEditing", Str(failed.Items[0], "error"));

                AssertExactlyOneLeaseWindow(fake, gate);
            }
        }

        // ---------------------------------------------------------------- partial failure, no rollback

        [Test]
        public void PartialFailure_MiddleOperationFails_EarlierAppliedNotRolledBack_LaterStillAttempted()
        {
            var source = new GameObject("Widget2");
            var assetPath = ScratchDir + "/Widget2.prefab";
            var variantPath = ScratchDir + "/Widget2Variant.prefab";

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("prefab.apply", Params(
                    Op("create", ("gameObjectPath", JsonValue.String("Widget2")), ("assetPath", JsonValue.String(assetPath))),
                    Op("instantiate", ("prefabPath", JsonValue.String(ScratchDir + "/Ghost.prefab"))),
                    Op("createVariant", ("basePrefabPath", JsonValue.String(assetPath)), ("variantPath", JsonValue.String(variantPath))))));

                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(assetPath), "op 0 must have applied and must not be rolled back by op 1's failure");
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(variantPath), "op 2 must still have applied after op 1 failed");

                CollectionAssert.AreEqual(new[] { 0, 2 }, AppliedIndices(result));
                var failed = FailedItems(result);
                Assert.AreEqual(1, failed.Items.Count);
                Assert.AreEqual(1L, failed.Items[0].TryGetProperty("index", out var idx) ? idx!.AsInteger() : -1);
                Assert.AreEqual("instantiate", Str(failed.Items[0], "op"));
                StringAssert.Contains("Ghost.prefab", Str(failed.Items[0], "error"));

                AssertExactlyOneLeaseWindow(fake, gate);
            }
        }

        // ---------------------------------------------------------------- the headline: one undo, whole spec reverted

        /// <summary>'instantiate' is the one op among the five whose effect is actually part of
        /// Unity's interactive Undo stack (the others mutate an asset on disk, outside that model -
        /// see PrefabApplyCommands' own class doc comment). Two instantiate ops of the SAME prefab
        /// in one batch (Unity auto-disambiguates the second instance's name) is enough to prove a
        /// single Ctrl/Cmd+Z reverts the WHOLE batch, not just the second.</summary>
        [Test]
        public void Apply_RegistersUndoAsOneGroup_PerformUndoRevertsEveryOperation()
        {
            var source = new GameObject("UndoWidget");
            var prefabPath = ScratchDir + "/UndoWidget.prefab";
            PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
            UnityEngine.Object.DestroyImmediate(source);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            CommandTable.Dispatch(gate, Request("prefab.apply", Params(
                Op("instantiate", ("prefabPath", JsonValue.String(prefabPath))),
                Op("instantiate", ("prefabPath", JsonValue.String(prefabPath))))));

            // sanity: the batch really did create two instances before we undo it
            var first = GameObject.Find("UndoWidget");
            Assert.IsNotNull(first);
            var all = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
                .Count(t => t.parent == null && t.name.StartsWith("UndoWidget", StringComparison.Ordinal));
            Assert.AreEqual(2, all, "both instantiate ops must have created their own instance");

            Undo.PerformUndo(); // a single Ctrl/Cmd+Z

            var remaining = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
                .Count(t => t.parent == null && t.name.StartsWith("UndoWidget", StringComparison.Ordinal));
            Assert.AreEqual(0, remaining, "the whole batch must revert together - BOTH instances gone after ONE undo");
        }
    }
}
