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
    /// scriptableObject.apply (DESIGN-04 Lane D's plugin-side half): one wire call carrying the WHOLE
    /// scriptable_object_apply 'operations' array, applied inside ONE CommandTable.Dispatch - the
    /// SAME shape MaterialApplyCommandsTests proves for material.apply, including the headline
    /// property (a single Undo.PerformUndo reverting the whole batch's Undo-registered work). See
    /// that file's own doc comment for why <see cref="SceneTestFixtures.ResetScene"/> is reused
    /// rather than duplicated, and MaterialCommandsTests' own doc comment for why ScriptableObjects -
    /// like materials - additionally need a scratch asset folder per test.
    ///
    /// Uses <see cref="HadesTestConfig"/>, a test-only ScriptableObject type compiled into this same
    /// assembly (Hades.Tests.Editor), so <see cref="UnityTypeSearch.FindAll"/> can resolve it by name
    /// exactly the way it would resolve a project's own script - see DESIGN-04 §10.3.
    /// </summary>
    [TestFixture]
    public sealed class ScriptableObjectApplyCommandsTests
    {
        const string ScratchDir = "Assets/Tests/_HadesScriptableObjectApplyScratch";

        [SetUp]
        public void SetUp()
        {
            SceneTestFixtures.ResetScene();
            Undo.ClearAll();
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
            AssetDatabase.CreateFolder("Assets/Tests", "_HadesScriptableObjectApplyScratch");
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
            Assert.AreEqual(0, fake.LockCalls, "scriptableObject.apply must never call Lock");
            Assert.AreEqual(0, fake.UnlockCalls, "scriptableObject.apply must never call Unlock");
            Assert.IsFalse(gate.IsHeld, "scriptableObject.apply must never leave a lease held");
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
            obj != null && obj.TryGetProperty(key, out var v) && v!.Kind == JsonValueKind.String ? v.AsString() : null;

        static JsonValue Result(JsonValue batchResult, int index)
        {
            foreach (var entry in ResultsItems(batchResult).Items)
            {
                if (entry.TryGetProperty("index", out var i) && i!.AsInteger() == index)
                    return entry.TryGetProperty("result", out var r) ? r : null;
            }
            return null;
        }

        static string AbsolutePath(string projectRelativePath) =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));

        // ---------------------------------------------------------------- 1: create resolves a type, writes the file

        [Test]
        public void Create_ResolvesTestAssemblyType_ResultCarriesPathTypeAssemblyGuid_FileExistsOnDisk()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var path = ScratchDir + "/Sword.asset";
                var result = CommandTable.Dispatch(gate, Request("scriptableObject.apply", Params(
                    Op("create", ("path", JsonValue.String(path)), ("type", JsonValue.String("HadesTestConfig"))))));

                CollectionAssert.AreEqual(new[] { 0 }, AppliedIndices(result));
                var createResult = Result(result, 0);
                Assert.AreEqual(path, Str(createResult, "path"));
                Assert.AreEqual(typeof(HadesTestConfig).FullName, Str(createResult, "type"));
                Assert.IsFalse(string.IsNullOrEmpty(Str(createResult, "assembly")));
                Assert.IsFalse(string.IsNullOrEmpty(Str(createResult, "guid")));

                Assert.IsTrue(File.Exists(AbsolutePath(path)), "the .asset file must exist on disk");
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<HadesTestConfig>(path));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        // ---------------------------------------------------------------- 2: creates missing ancestor folders

        [Test]
        public void Create_CreatesMissingAncestorFolders()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var path = ScratchDir + "/Deeply/Nested/Sub/Folder/Sword.asset";
                var result = CommandTable.Dispatch(gate, Request("scriptableObject.apply", Params(
                    Op("create", ("path", JsonValue.String(path)), ("type", JsonValue.String("HadesTestConfig"))))));

                CollectionAssert.AreEqual(new[] { 0 }, AppliedIndices(result));
                Assert.IsTrue(AssetDatabase.IsValidFolder(ScratchDir + "/Deeply/Nested/Sub/Folder"));
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<HadesTestConfig>(path));
            }
        }

        // ---------------------------------------------------------------- 3: refuses an existing path

        [Test]
        public void Create_RefusesAnExistingPath_AssetPathGuardMessageShape()
        {
            var path = ScratchDir + "/AlreadyThere.asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<HadesTestConfig>(), path);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("scriptableObject.apply", Params(
                    Op("create", ("path", JsonValue.String(path)), ("type", JsonValue.String("HadesTestConfig"))))));

                Assert.AreEqual(0, AppliedIndices(result).Length);
                var failed = FailedItems(result);
                Assert.AreEqual(1, failed.Items.Count);
                StringAssert.Contains("already exists", Str(failed.Items[0], "error"));
                StringAssert.Contains("setProperties", Str(failed.Items[0], "error"));
            }
        }

        // ---------------------------------------------------------------- 4: refuses a MonoBehaviour name

        [Test]
        public void Create_RefusesAMonoBehaviourDerivedName_NamesItAsMonoBehaviour_PointsAtSceneApply()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("scriptableObject.apply", Params(
                    Op("create", ("path", JsonValue.String(ScratchDir + "/Nope.asset")), ("type", JsonValue.String(nameof(HadesTestMonoBehaviour)))))));

                Assert.AreEqual(0, AppliedIndices(result).Length);
                var failed = FailedItems(result);
                Assert.AreEqual(1, failed.Items.Count);
                var error = Str(failed.Items[0], "error");
                StringAssert.Contains("MonoBehaviour", error);
                StringAssert.Contains("scene_apply", error);
                LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(error);
            }
        }

        // ---------------------------------------------------------------- 5: refuses an abstract type before CreateInstance

        [Test]
        public void Create_RefusesAnAbstractType_BeforeCreateInstanceIsCalled()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("scriptableObject.apply", Params(
                    Op("create", ("path", JsonValue.String(ScratchDir + "/Nope.asset")), ("type", JsonValue.String(nameof(HadesAbstractTestConfig)))))));

                Assert.AreEqual(0, AppliedIndices(result).Length);
                var failed = FailedItems(result);
                Assert.AreEqual(1, failed.Items.Count);
                StringAssert.Contains("abstract", Str(failed.Items[0], "error"));
                Assert.IsFalse(File.Exists(AbsolutePath(ScratchDir + "/Nope.asset")), "nothing must be written for an abstract type");
            }
        }

        // ---------------------------------------------------------------- 6: refuses an unknown type

        [Test]
        public void Create_RefusesAnUnknownType_NamesRecompileRecipe_OnlyLiveToolNames()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("scriptableObject.apply", Params(
                    Op("create", ("path", JsonValue.String(ScratchDir + "/Nope.asset")), ("type", JsonValue.String("ThisTypeDoesNotExistAnywhere"))))));

                Assert.AreEqual(0, AppliedIndices(result).Length);
                var failed = FailedItems(result);
                Assert.AreEqual(1, failed.Items.Count);
                var error = Str(failed.Items[0], "error");
                StringAssert.Contains("project_recompile_scripts", error);
                StringAssert.Contains("hades_charon_status", error);
                LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(error);
            }
        }

        // ---------------------------------------------------------------- 7: setProperties per-field applied/failed

        [Test]
        public void SetProperties_SetsEachSupportedType_ReportsPerFieldAppliedFailed_OneBadFieldDoesNotFailOthers()
        {
            var path = ScratchDir + "/Config.asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<HadesTestConfig>(), path);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("scriptableObject.apply", Params(
                    Op("setProperties", ("path", JsonValue.String(path)), ("values", JsonValue.NewObject()
                        .SetProperty("intValue", JsonValue.Integer(7))
                        .SetProperty("floatValue", JsonValue.Float(1.5))
                        .SetProperty("stringValue", JsonValue.String("hello"))
                        .SetProperty("mode", JsonValue.String("Beta"))
                        .SetProperty("noSuchField", JsonValue.Integer(1)))))));

                CollectionAssert.AreEqual(new[] { 0 }, AppliedIndices(result));
                var setResult = Result(result, 0);
                Assert.AreEqual(path, Str(setResult, "path"));

                Assert.IsTrue(setResult.TryGetProperty("applied", out var applied) && applied.Kind == JsonValueKind.Array);
                var appliedNames = applied.Items.Select(i => i.AsString()).ToList();
                CollectionAssert.Contains(appliedNames, "intValue");
                CollectionAssert.Contains(appliedNames, "floatValue");
                CollectionAssert.Contains(appliedNames, "stringValue");
                CollectionAssert.Contains(appliedNames, "mode");

                Assert.IsTrue(setResult.TryGetProperty("failed", out var failed) && failed.Kind == JsonValueKind.Array);
                Assert.AreEqual(1, failed.Items.Count);
                Assert.AreEqual("noSuchField", Str(failed.Items[0], "property"));

                var reloaded = AssetDatabase.LoadAssetAtPath<HadesTestConfig>(path);
                Assert.AreEqual(7, reloaded.intValue);
                Assert.AreEqual(1.5f, reloaded.floatValue);
                Assert.AreEqual("hello", reloaded.stringValue);
                Assert.AreEqual(HadesTestConfigMode.Beta, reloaded.mode);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        // ---------------------------------------------------------------- 8: setProperties on the wrong asset type

        [Test]
        public void SetProperties_OnAMaterial_Refuses_NamesActualTypeAndTheRightTool()
        {
            var path = ScratchDir + "/NotAScriptableObject.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("scriptableObject.apply", Params(
                    Op("setProperties", ("path", JsonValue.String(path)), ("values", JsonValue.NewObject().SetProperty("x", JsonValue.Integer(1)))))));

                Assert.AreEqual(0, AppliedIndices(result).Length);
                var failed = FailedItems(result);
                Assert.AreEqual(1, failed.Items.Count);
                var error = Str(failed.Items[0], "error");
                StringAssert.Contains("Material", error);
                StringAssert.Contains("material_apply", error);
            }
        }

        // ---------------------------------------------------------------- 9: cross-op reference resolution in one batch

        [Test]
        public void CrossOp_CreateA_CreateB_SetPropertiesBReferencingA_ResolvesInOneBatch()
        {
            var pathA = ScratchDir + "/A.asset";
            var pathB = ScratchDir + "/B.asset";

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("scriptableObject.apply", Params(
                    Op("create", ("path", JsonValue.String(pathA)), ("type", JsonValue.String("HadesTestConfig"))),
                    Op("create", ("path", JsonValue.String(pathB)), ("type", JsonValue.String("HadesTestConfig"))),
                    Op("setProperties", ("path", JsonValue.String(pathB)), ("values", JsonValue.NewObject()
                        .SetProperty("reference", JsonValue.String(pathA)))))));

                CollectionAssert.AreEqual(new[] { 0, 1, 2 }, AppliedIndices(result));

                var b = AssetDatabase.LoadAssetAtPath<HadesTestConfig>(pathB);
                var a = AssetDatabase.LoadAssetAtPath<HadesTestConfig>(pathA);
                Assert.IsNotNull(a);
                Assert.IsNotNull(b);
                Assert.AreEqual(a, b.reference, "B's reference field must resolve to A, created earlier in the SAME batch");

                AssertNeverTouchedLease(fake, gate);
            }
        }

        // ---------------------------------------------------------------- 10: one Undo group; file survives undo

        [Test]
        public void Apply_RegistersUndoAsOneGroup_PerformUndoDestroysTheObject_ButTheAssetFileRemains()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var path = ScratchDir + "/UndoMe.asset";

            Undo.IncrementCurrentGroup();
            CommandTable.Dispatch(gate, Request("scriptableObject.apply", Params(
                Op("create", ("path", JsonValue.String(path)), ("type", JsonValue.String("HadesTestConfig"))))));

            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<HadesTestConfig>(path), "sanity: the batch really did create the asset");

            Undo.PerformUndo(); // a single Ctrl/Cmd+Z

            // DESIGN-04 §5.2, probe-verified: the in-memory object is destroyed by undo, but the
            // .asset FILE it wrote is not removed. Asserting the file still exists (not asserting its
            // removal) is the whole point - a test asserting removal would codify the bug.
            Assert.IsTrue(File.Exists(AbsolutePath(path)), "the .asset file must remain on disk after undo");
        }

        // ---------------------------------------------------------------- 11: unknown op inside the batch

        [Test]
        public void UnknownOp_RecordedAsPerOperationFailure_BatchContinues()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var path = ScratchDir + "/Good.asset";
                var result = CommandTable.Dispatch(gate, Request("scriptableObject.apply", Params(
                    Op("frobnicate"),
                    Op("create", ("path", JsonValue.String(path)), ("type", JsonValue.String("HadesTestConfig"))))));

                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<HadesTestConfig>(path), "a later, valid op must still apply");
                CollectionAssert.AreEqual(new[] { 1 }, AppliedIndices(result));
                var failed = FailedItems(result);
                Assert.AreEqual(1, failed.Items.Count);
                Assert.AreEqual("frobnicate", Str(failed.Items[0], "op"));
                StringAssert.Contains("frobnicate", Str(failed.Items[0], "error"));

                AssertNeverTouchedLease(fake, gate);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Test-only fixture types, compiled into this same assembly (Hades.Tests.Editor) so
    // UnityTypeSearch.FindAll can resolve them by name - see this file's own class doc comment.

    public enum HadesTestConfigMode { Alpha, Beta, Gamma }

    /// <summary>An int, a float, a string, an enum, and an Object reference field - enough to
    /// exercise every SerializedPropertyJson.Set branch DESIGN-04's own test plan (§10.3) calls for.</summary>
    public sealed class HadesTestConfig : ScriptableObject
    {
        public int intValue;
        public float floatValue;
        public string stringValue;
        public HadesTestConfigMode mode;
        public UnityEngine.Object reference;
    }

    /// <summary>Abstract ScriptableObject-derived type - DESIGN-04 §4.3's "abstract type" refusal,
    /// which must fire BEFORE ScriptableObject.CreateInstance is ever called.</summary>
    public abstract class HadesAbstractTestConfig : ScriptableObject
    {
    }

    /// <summary>A MonoBehaviour, not a ScriptableObject - DESIGN-04 §4.3's "wrong kind" refusal.</summary>
    public sealed class HadesTestMonoBehaviour : MonoBehaviour
    {
    }
}
