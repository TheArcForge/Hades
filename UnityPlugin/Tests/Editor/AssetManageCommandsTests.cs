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
    /// asset.manage (Plan 10 Task 5's plugin-side half): one wire call carrying the WHOLE
    /// asset_manage 'operations' array, applied inside ONE CommandTable.Dispatch, spanning "move"
    /// (class-1), "import" and "refresh" (both class-2). Like ProjectSettingsApplyCommandsTests
    /// (which this file's shape mirrors most closely), every test here proves the SAME headline
    /// property: the reload lease is acquired and released EXACTLY ONCE for the whole batch, never
    /// once per operation - see <see cref="AssertExactlyOneLeaseWindow"/> - even though "move" never
    /// individually touches the gate.
    /// </summary>
    [TestFixture]
    public sealed class AssetManageCommandsTests
    {
        const string ScratchDir = "Assets/Tests/_HadesAssetManageScratch";

        [SetUp]
        public void SetUp()
        {
            Undo.ClearAll();
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
            AssetDatabase.CreateFolder("Assets/Tests", "_HadesAssetManageScratch");
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

        /// <summary>Same property PrefabApplyCommandsTests/ProjectSettingsApplyCommandsTests' own
        /// helpers prove for their own batch tools - EXACTLY one Lock/Unlock pair for the WHOLE
        /// batch, regardless of how many operations it contains or which of the three ops
        /// (only two of which are individually lease-bound) each one is.</summary>
        static void AssertExactlyOneLeaseWindow(FakeEditorLockApi fake, ReloadGate gate)
        {
            Assert.AreEqual(1, fake.LockCalls, "asset.manage must acquire the reload lock EXACTLY ONCE for the whole batch, not once per operation");
            Assert.AreEqual(1, fake.UnlockCalls, "asset.manage must release the reload lock EXACTLY ONCE for the whole batch");
            Assert.IsFalse(gate.IsHeld, "asset.manage must never leave a lease held");
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
                CommandTable.Dispatch(gate, Request("asset.manage", JsonValue.NewObject())));

            StringAssert.Contains("operations", ex.Message);
        }

        // ---------------------------------------------------------------- full op vocabulary sweep, one lease window

        /// <summary>One asset.manage call touching all three ops - move (of a pre-existing asset),
        /// import (of a file freshly dropped on disk), refresh - in order, with EXACTLY one lease
        /// window for the whole thing even though "move" never individually needs one.</summary>
        [Test]
        public void FullOperationSweep_AppliesEveryOpInOrder_OneLeaseWindow()
        {
            var moveSource = ScratchDir + "/ToMove.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), moveSource);
            var moveDest = ScratchDir + "/Moved.mat";

            var importPath = ScratchDir + "/dropped.txt";
            File.WriteAllText(AbsolutePath(importPath), "hello");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("asset.manage", Params(
                    Op("move", ("sourcePath", JsonValue.String(moveSource)), ("destPath", JsonValue.String(moveDest))),
                    Op("import", ("path", JsonValue.String(importPath))),
                    Op("refresh"))));

                CollectionAssert.AreEqual(new[] { 0, 1, 2 }, AppliedIndices(result));
                Assert.AreEqual(0, FailedItems(result).Items.Count);

                // Verified via real project state, not by trusting the response.
                Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(moveSource));
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(moveDest));
                Assert.IsNotEmpty(AssetDatabase.AssetPathToGUID(importPath));

                var results = ResultsItems(result);
                Assert.AreEqual(3, results.Items.Count);
                var moveResult = results.Items[0].TryGetProperty("result", out var mr) ? mr : null;
                Assert.AreEqual(moveDest, Str(moveResult, "destination"));
                var importResult = results.Items[1].TryGetProperty("result", out var ir) ? ir : null;
                Assert.AreEqual(AssetDatabase.AssetPathToGUID(importPath), Str(importResult, "guid"));
                var refreshResult = results.Items[2].TryGetProperty("result", out var rr) ? rr : null;
                Assert.IsTrue(refreshResult.TryGetProperty("refreshed", out var refreshedVal) && refreshedVal.AsBoolean());

                StringAssert.Contains("3", Str(result, "summary") ?? "");

                AssertExactlyOneLeaseWindow(fake, gate);
            }
        }

        // ---------------------------------------------------------------- unknown op

        [Test]
        public void UnknownOp_RecordedAsPerOperationFailure_BatchContinues_OneLeaseWindowStill()
        {
            var moveSource = ScratchDir + "/UnknownOpMoveSource.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), moveSource);
            var moveDest = ScratchDir + "/UnknownOpMoveDest.mat";

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("asset.manage", Params(
                    Op("frobnicate"),
                    Op("move", ("sourcePath", JsonValue.String(moveSource)), ("destPath", JsonValue.String(moveDest))))));

                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(moveDest), "a later, valid op must still apply");
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
            var moveSource = ScratchDir + "/PartialMoveSource.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), moveSource);
            var moveDest = ScratchDir + "/PartialMoveDest.mat";

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("asset.manage", Params(
                    Op("move", ("sourcePath", JsonValue.String(moveSource)), ("destPath", JsonValue.String(moveDest))),
                    Op("import", ("path", JsonValue.String(ScratchDir + "/Ghost.png"))), // nothing on disk - fails
                    Op("refresh"))));

                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(moveDest), "op 0 must have applied, not rolled back by op 1's failure");

                CollectionAssert.AreEqual(new[] { 0, 2 }, AppliedIndices(result), "op 2 must still be attempted after op 1 fails");
                var failed = FailedItems(result);
                Assert.AreEqual(1, failed.Items.Count);
                Assert.AreEqual(1L, failed.Items[0].TryGetProperty("index", out var idx) ? idx.AsInteger() : -1);
                Assert.AreEqual("import", Str(failed.Items[0], "op"));
                StringAssert.Contains("Ghost.png", Str(failed.Items[0], "error"));

                // The unified partial-batch shape: 'results' carries an entry for every APPLIED op
                // (by the same index 'applied' reports), never for the failed one - a caller can
                // learn which operations landed, which did not, and why from ONE response.
                var results = ResultsItems(result);
                Assert.AreEqual(2, results.Items.Count);
                Assert.AreEqual(0L, results.Items[0].TryGetProperty("index", out var rIdx0) ? rIdx0.AsInteger() : -1);
                Assert.AreEqual("move", Str(results.Items[0], "op"));
                Assert.AreEqual(2L, results.Items[1].TryGetProperty("index", out var rIdx1) ? rIdx1.AsInteger() : -1);
                Assert.AreEqual("refresh", Str(results.Items[1], "op"));
                Assert.IsTrue(results.Items[0].TryGetProperty("result", out var r0) && r0 != null,
                    "each results entry must carry the op's own result payload, not just index/op");

                AssertExactlyOneLeaseWindow(fake, gate);
            }
        }

        // ---------------------------------------------------------------- one lease window even for an all-class-1 batch

        [Test]
        public void Apply_AcquiresLeaseExactlyOnce_EvenWhenBatchIsEntirelyClass1Ops()
        {
            // "move" never individually touches the gate (see AssetCommands.MoveAsset's own doc
            // comment) - this proves asset.manage still wraps the WHOLE batch in its own uniform
            // lease window even when nothing in THIS PARTICULAR call actually needed one, the same
            // "does this specific op happen to be lease-bound is not a caller's problem" property
            // PrefabApplyCommands/ProjectSettingsApplyCommands establish.
            var moveSource = ScratchDir + "/OnlyMoveSource.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), moveSource);
            var moveDest = ScratchDir + "/OnlyMoveDest.mat";

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("asset.manage", Params(
                    Op("move", ("sourcePath", JsonValue.String(moveSource)), ("destPath", JsonValue.String(moveDest))))));

                CollectionAssert.AreEqual(new[] { 0 }, AppliedIndices(result));
                AssertExactlyOneLeaseWindow(fake, gate);
            }
        }
    }
}
