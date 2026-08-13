// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.IO;
using System.Linq;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// projectSettings.apply (Plan 10 Task 4's plugin-side half): one wire call carrying the WHOLE
    /// project_settings_apply 'operations' array, applied inside ONE CommandTable.Dispatch, spanning
    /// THREE different underlying command classes (TagLayerCommands, SceneManagementCommands,
    /// AssetCommands). Like <see cref="PrefabApplyCommandsTests"/> (which this file's shape mirrors
    /// most closely), every test here proves the SAME headline property: the reload lease is
    /// acquired and released EXACTLY ONCE for the whole batch, never once per operation - see
    /// <see cref="AssertExactlyOneLeaseWindow"/> - even though createTag/deleteTag/createLayer/
    /// setBuildScenes are class-1 (no lease individually) and only setImportSettings/
    /// setClipImportSettings are class-2.
    ///
    /// ProjectSettings/TagManager.asset and EditorBuildSettings.scenes are project-global state,
    /// like TagLayerCommandsTests/SceneManagementCommandsTests' own fixtures - captured/cleared in
    /// SetUp AND TearDown (distinct names/slots from those two files' own scratch tags/layers, so
    /// this file's fixture can never collide with theirs even if a run interleaves).
    /// </summary>
    [TestFixture]
    public sealed class ProjectSettingsApplyCommandsTests
    {
        const string ScratchDir = "Assets/Tests/_HadesProjectSettingsApplyScratch";
        const string TestTag = "HadesPSATestTag";
        const string TestTagAlt = "HadesPSATestTagAlt";
        const string TestLayerName = "HadesPSATestLayer";
        static readonly int[] ScratchLayerSlots = { 27, 28 };

        EditorBuildSettingsScene[] _originalBuildScenes;

        [SetUp]
        public void SetUp()
        {
            Undo.ClearAll();
            RemoveTagDirect(TestTag);
            RemoveTagDirect(TestTagAlt);
            foreach (var slot in ScratchLayerSlots) SetLayerSlotDirect(slot, "");
            FlushTagManagerDirect();
            _originalBuildScenes = EditorBuildSettings.scenes;

            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
            AssetDatabase.CreateFolder("Assets/Tests", "_HadesProjectSettingsApplyScratch");
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            RemoveTagDirect(TestTag);
            RemoveTagDirect(TestTagAlt);
            foreach (var slot in ScratchLayerSlots) SetLayerSlotDirect(slot, "");
            FlushTagManagerDirect();
            EditorBuildSettings.scenes = _originalBuildScenes;

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

        /// <summary>Same property PrefabApplyCommandsTests' own helper proves for prefab.apply -
        /// EXACTLY one Lock/Unlock pair for the WHOLE batch, regardless of how many operations it
        /// contains or which of the three underlying classes (only two of which are individually
        /// lease-bound) each one belongs to.</summary>
        static void AssertExactlyOneLeaseWindow(FakeEditorLockApi fake, ReloadGate gate)
        {
            Assert.AreEqual(1, fake.LockCalls, "projectSettings.apply must acquire the reload lock EXACTLY ONCE for the whole batch, not once per operation");
            Assert.AreEqual(1, fake.UnlockCalls, "projectSettings.apply must release the reload lock EXACTLY ONCE for the whole batch");
            Assert.IsFalse(gate.IsHeld, "projectSettings.apply must never leave a lease held");
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

        static long IntVal(JsonValue obj, string key) =>
            obj.TryGetProperty(key, out var v) && v!.Kind == JsonValueKind.Integer ? v.AsInteger() : long.MinValue;

        static string AbsolutePath(string projectRelativePath) =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));

        static UnityEngine.Object LoadTagManager() => AssetDatabase.LoadMainAssetAtPath("ProjectSettings/TagManager.asset");

        static void RemoveTagDirect(string name)
        {
            var so = new SerializedObject(LoadTagManager());
            var tags = so.FindProperty("tags");
            for (var i = tags.arraySize - 1; i >= 0; i--)
                if (tags.GetArrayElementAtIndex(i).stringValue == name) tags.DeleteArrayElementAtIndex(i);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static bool TagExists(string name)
        {
            var so = new SerializedObject(LoadTagManager());
            var tags = so.FindProperty("tags");
            for (var i = 0; i < tags.arraySize; i++)
                if (tags.GetArrayElementAtIndex(i).stringValue == name) return true;
            return false;
        }

        static void SetLayerSlotDirect(int index, string name)
        {
            var so = new SerializedObject(LoadTagManager());
            var layers = so.FindProperty("layers");
            if (index < layers.arraySize) layers.GetArrayElementAtIndex(index).stringValue = name;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static string LayerNameAt(int index)
        {
            var so = new SerializedObject(LoadTagManager());
            var layers = so.FindProperty("layers");
            return index < layers.arraySize ? layers.GetArrayElementAtIndex(index).stringValue : "";
        }

        /// <summary>SetUp/TearDown's own fixture cleanup, after this class's F10 fix: projectSettings.apply
        /// createTag/deleteTag now flush ProjectSettings/TagManager.asset to disk for real (see
        /// Apply's own doc comment), so a PRIOR test's tag/layer edit can genuinely reach disk. The
        /// direct in-memory cleanup above (RemoveTagDirect/SetLayerSlotDirect, via
        /// ApplyModifiedPropertiesWithoutUndo) never did and still does not flush anything itself -
        /// without this, that cleanup would silently stop being enough, leaving a later test's own
        /// "disk starts clean" assertion (see CreateTag_FlushesTagManagerToDisk_WithoutAnySceneSave)
        /// to fail on stale disk content a fresh AssetDatabase.LoadMainAssetAtPath-based check like
        /// TagExists() would never reveal.</summary>
        static void FlushTagManagerDirect() => AssetDatabase.SaveAssets();

        /// <summary>Dispatches once through a fresh, throwaway gate - for building fixture state
        /// (e.g. an existing tag before testing deleteTag) via the SAME code path a real caller
        /// would use, without that setup call polluting a test's own lease-window assertion. Same
        /// helper shape as TagLayerCommandsTests' own DispatchSetup.</summary>
        static void DispatchSetup(string method, JsonValue @params)
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            CommandTable.Dispatch(gate, Request(method, @params));
        }

        static string SaveAdditiveScratchScene(string fileName)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var path = ScratchDir + "/" + fileName;
            EditorSceneManager.SaveScene(scene, path);
            EditorSceneManager.CloseScene(scene, true);
            return path;
        }

        // ---------------------------------------------------------------- structural validation

        [Test]
        public void Apply_MissingOperationsArray_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var ex = Assert.Throws<ArgumentException>(() =>
                CommandTable.Dispatch(gate, Request("projectSettings.apply", JsonValue.NewObject())));

            StringAssert.Contains("operations", ex.Message);
        }

        // ---------------------------------------------------------------- full op vocabulary sweep, one lease window

        /// <summary>One projectSettings.apply call touching all six ops - createTag, deleteTag (of
        /// a pre-existing tag), createLayer (asserting its assigned index rides along in 'results' -
        /// the one thing a caller cannot know in advance), setBuildScenes, setImportSettings
        /// (success), and setClipImportSettings (a DELIBERATE failure against an unknown asset - see
        /// this class's own doc comment for why a genuine clip-import success fixture is not
        /// attempted here, mirroring AssetCommandsTests' own single-tool coverage, which never
        /// builds one either). Proves the whole vocabulary dispatches, in order, with EXACTLY one
        /// lease window for the whole thing regardless of the 5-succeed/1-fail split.</summary>
        [Test]
        public void FullOperationSweep_AppliesEveryOpInOrder_FiveSucceedOneFails_OneLeaseWindow()
        {
            DispatchSetup("tag.create", JsonValue.NewObject().SetProperty("name", JsonValue.String(TestTagAlt)));

            var scenePath = SaveAdditiveScratchScene("BuildScene.unity");

            var texPath = ScratchDir + "/tex.png";
            var texture = new Texture2D(4, 4);
            var pngBytes = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);
            File.WriteAllBytes(AbsolutePath(texPath), pngBytes);
            AssetDatabase.ImportAsset(texPath);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("projectSettings.apply", Params(
                    Op("createTag", ("name", JsonValue.String(TestTag))),
                    Op("deleteTag", ("name", JsonValue.String(TestTagAlt))),
                    Op("createLayer", ("name", JsonValue.String(TestLayerName)), ("layerIndex", JsonValue.Integer(27))),
                    Op("setBuildScenes", ("scenes", JsonValue.NewArray().Add(
                        JsonValue.NewObject().SetProperty("path", JsonValue.String(scenePath)).SetProperty("enabled", JsonValue.Bool(true))))),
                    Op("setImportSettings", ("path", JsonValue.String(texPath)),
                        ("properties", JsonValue.NewObject().SetProperty("m_UserData", JsonValue.String("hades-marker")))),
                    Op("setClipImportSettings", ("path", JsonValue.String(ScratchDir + "/Ghost.fbx")),
                        ("clips", JsonValue.NewArray().Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("Take 001")))))
                    )));

                CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, AppliedIndices(result));
                var failed = FailedItems(result);
                Assert.AreEqual(1, failed.Items.Count);
                Assert.AreEqual(5L, IntVal(failed.Items[0], "index"));
                Assert.AreEqual("setClipImportSettings", Str(failed.Items[0], "op"));
                StringAssert.Contains("Ghost.fbx", Str(failed.Items[0], "error"));
                StringAssert.Contains("5", Str(result, "summary"));

                // Verified via real project state, not by trusting the response.
                Assert.IsTrue(TagExists(TestTag));
                Assert.IsFalse(TagExists(TestTagAlt));
                Assert.AreEqual(TestLayerName, LayerNameAt(27));
                Assert.AreEqual(1, EditorBuildSettings.scenes.Length);
                Assert.AreEqual(scenePath, EditorBuildSettings.scenes[0].path);
                var reloadedImporter = AssetImporter.GetAtPath(texPath);
                Assert.AreEqual("hades-marker", reloadedImporter.userData);

                // createLayer's assigned index rides along in 'results', not just a bare "applied".
                var results = ResultsItems(result);
                Assert.AreEqual(5, results.Items.Count);
                var layerResult = results.Items[2];
                Assert.AreEqual("createLayer", Str(layerResult, "op"));
                var layerData = layerResult.TryGetProperty("result", out var r) ? r : null;
                Assert.IsNotNull(layerData);
                Assert.AreEqual(27L, IntVal(layerData, "index"));

                AssertExactlyOneLeaseWindow(fake, gate);
            }
        }

        // ---------------------------------------------------------------- unknown op

        [Test]
        public void UnknownOp_RecordedAsPerOperationFailure_BatchContinues_OneLeaseWindowStill()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("projectSettings.apply", Params(
                    Op("frobnicate", ("name", JsonValue.String(TestTag))),
                    Op("createTag", ("name", JsonValue.String(TestTag))))));

                Assert.IsTrue(TagExists(TestTag), "a later, valid op must still apply");
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
            SetLayerSlotDirect(28, "AlreadyThere");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("projectSettings.apply", Params(
                    Op("createTag", ("name", JsonValue.String(TestTag))),
                    Op("createLayer", ("name", JsonValue.String(TestLayerName)), ("layerIndex", JsonValue.Integer(28))),
                    Op("deleteTag", ("name", JsonValue.String(TestTag))))));

                Assert.IsFalse(TagExists(TestTag), "op 0 must have applied, then op 2 deleted it again - neither rolled back by op 1's failure");

                CollectionAssert.AreEqual(new[] { 0, 2 }, AppliedIndices(result));
                var failed = FailedItems(result);
                Assert.AreEqual(1, failed.Items.Count);
                Assert.AreEqual(1L, IntVal(failed.Items[0], "index"));
                Assert.AreEqual("createLayer", Str(failed.Items[0], "op"));
                StringAssert.Contains("AlreadyThere", Str(failed.Items[0], "error"));

                AssertExactlyOneLeaseWindow(fake, gate);
            }
        }

        // ---------------------------------------------------------------- one lease window even for an all-class-1 batch

        [Test]
        public void Apply_AcquiresLeaseExactlyOnce_EvenWhenBatchIsEntirelyClass1Ops()
        {
            // createTag/deleteTag/createLayer/setBuildScenes never individually touch the gate (see
            // TagLayerCommands/SceneManagementCommands' own doc comments) - this proves
            // projectSettings.apply still wraps the WHOLE batch in its own uniform lease window even
            // when nothing in THIS PARTICULAR call actually needed one, the same "does this specific
            // op happen to be lease-bound is not a caller's problem" property PrefabApplyCommands
            // establishes for Undo.
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("projectSettings.apply", Params(
                    Op("createTag", ("name", JsonValue.String(TestTag))))));

                CollectionAssert.AreEqual(new[] { 0 }, AppliedIndices(result));
                AssertExactlyOneLeaseWindow(fake, gate);
            }
        }

        // ---------------------------------------------------------------- disk flush (mutations must not depend on a later save)

        /// <summary>The defect this pair guards against: a batch that applies a tag mutation left
        /// ProjectSettings/TagManager.asset on disk byte-for-byte unchanged - verified empirically,
        /// in batchmode, with no scene save anywhere in the process - even though the in-memory
        /// SerializedObject WAS updated (a same-session duplicate createTag still correctly failed
        /// "already exists"). A disk-backed reader (the project_settings read tool, or any process
        /// that starts fresh before a scene save or Editor quit happens to flush it incidentally)
        /// saw the mutation as never having happened. TagExists() elsewhere in this file reads
        /// through AssetDatabase.LoadMainAssetAtPath, which reflects this process's own in-memory
        /// state regardless of whether anything was ever written to disk - it would pass even
        /// without a fix, so it cannot be the test that catches this. Reading the file directly
        /// with File.ReadAllText, bypassing every Unity API/cache, is what makes it one.</summary>
        [Test]
        public void CreateTag_FlushesTagManagerToDisk_WithoutAnySceneSave()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                Assert.IsFalse(ReadTagManagerFileFromDisk().Contains(TestTag), "fixture must start clean on disk");

                CommandTable.Dispatch(gate, Request("projectSettings.apply", Params(
                    Op("createTag", ("name", JsonValue.String(TestTag))))));

                Assert.IsTrue(ReadTagManagerFileFromDisk().Contains(TestTag),
                    "projectSettings.apply must flush ProjectSettings/TagManager.asset to disk before returning, with no scene save");
            }
        }

        [Test]
        public void DeleteTag_FlushesRemovalToDisk_WithoutAnySceneSave()
        {
            // Setup goes through projectSettings.apply itself (not the standalone tag.create command,
            // which TagLayerCommands still answers unchanged - this fix is scoped to the batch handler
            // only, see Apply's own doc comment), so this test's own precondition is guaranteed by the
            // fix under test rather than by another test happening to have left the same tag on disk.
            DispatchSetup("projectSettings.apply", Params(Op("createTag", ("name", JsonValue.String(TestTag)))));
            Assert.IsTrue(ReadTagManagerFileFromDisk().Contains(TestTag), "fixture setup must itself already be on disk");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("projectSettings.apply", Params(
                    Op("deleteTag", ("name", JsonValue.String(TestTag))))));

                Assert.IsFalse(ReadTagManagerFileFromDisk().Contains(TestTag),
                    "projectSettings.apply must flush the deletion to disk before returning, with no scene save");
            }
        }

        /// <summary>Same file AbsolutePath() above resolves for the setImportSettings fixture -
        /// reused here to reach ProjectSettings/TagManager.asset, read directly rather than through
        /// any Unity API, so this sees exactly what a disk-backed reader outside this Editor process
        /// would see.</summary>
        static string ReadTagManagerFileFromDisk() => File.ReadAllText(AbsolutePath("ProjectSettings/TagManager.asset"));
    }
}
