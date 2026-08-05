// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// The four class-1 scene-as-a-file mutations, dispatched through
    /// <see cref="CommandTable.Dispatch"/> the same way every other class-1 suite does. Build
    /// Settings (<see cref="EditorBuildSettings.scenes"/>) is project-global state, like
    /// TagLayerCommandsTests' TagManager.asset - captured in SetUp and restored in TearDown so a
    /// test that replaces the build list can never leak into the next test or the scratch project
    /// itself.
    ///
    /// No PerformUndo-revert test for scene.save or scene.set_build - see SceneManagementCommands'
    /// own class doc comment for why (a pure filesystem write with nothing to revert; a static
    /// property with no serialized-object handle to snapshot, respectively). scene.create/
    /// scene.duplicate DO attempt one, since each produces a genuinely new SceneAsset.
    /// </summary>
    [TestFixture]
    public sealed class SceneManagementCommandsTests
    {
        const string ScratchDir = "Assets/Tests/_HadesSceneMgmtScratch";

        EditorBuildSettingsScene[] _originalBuildScenes;

        [SetUp]
        public void SetUp()
        {
            SceneTestFixtures.ResetScene();
            Undo.ClearAll();
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
            AssetDatabase.CreateFolder("Assets/Tests", "_HadesSceneMgmtScratch");
            _originalBuildScenes = EditorBuildSettings.scenes;
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            EditorBuildSettings.scenes = _originalBuildScenes;
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
            // scene.create/scene.duplicate/scene.save may have changed which scene is active -
            // reset once more so the NEXT test (in this file or another) starts from a known scene.
            SceneTestFixtures.ResetScene();
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
            Assert.AreEqual(0, fake.LockCalls, "a class-1 scene-management mutation must never call Lock");
            Assert.AreEqual(0, fake.UnlockCalls, "a class-1 scene-management mutation must never call Unlock");
            Assert.IsFalse(gate.IsHeld, "a class-1 scene-management mutation must never leave a lease held");
        }

        static string StringProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.String ? v.AsString() : null;

        static long IntProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.Integer ? v.AsInteger() : long.MinValue;

        static string SaveAdditiveScratchScene(string fileName)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var path = ScratchDir + "/" + fileName;
            EditorSceneManager.SaveScene(scene, path);
            EditorSceneManager.CloseScene(scene, true);
            return path;
        }

        // -------------------------------------------------------------------------------- scene.save

        [Test]
        public void SaveScene_NoPath_SavesInPlace_NoLeaseTouched()
        {
            var scenePath = SceneManager.GetActiveScene().path;
            Assert.IsFalse(string.IsNullOrEmpty(scenePath), "fixture scene must already have a path");
            new GameObject("Marker");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("scene.save", JsonValue.NewObject()));

                Assert.AreEqual(scenePath, StringProp(result, "saved"));
                Assert.IsFalse(SceneManager.GetActiveScene().isDirty);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void SaveScene_WithPath_SavesAsNewFile()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var path = ScratchDir + "/SavedAs.unity";
            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(path));
            var result = CommandTable.Dispatch(gate, Request("scene.save", @params));

            Assert.AreEqual(path, StringProp(result, "saved"));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(path));
        }

        [Test]
        public void SaveScene_NeverSavedAndNoPath_ThrowsActionableError()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); // deliberately never saved

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("scene.save", JsonValue.NewObject())));

            StringAssert.Contains("path", ex.Message);
        }

        // ------------------------------------------------------------------------------ scene.create

        [Test]
        public void CreateScene_NoTemplate_WritesNewSceneFile_WithoutDisturbingCurrentlyOpenScene_NoLeaseTouched()
        {
            var activeBefore = SceneManager.GetActiveScene().path;

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var path = ScratchDir + "/NewOne.unity";
                var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(path));
                var result = CommandTable.Dispatch(gate, Request("scene.create", @params));

                Assert.AreEqual(path, StringProp(result, "created"));
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(path));
                Assert.AreEqual(activeBefore, SceneManager.GetActiveScene().path,
                    "scene.create must not change which scene is currently open");

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void CreateScene_FromTemplate_CopiesTemplateFile()
        {
            var templatePath = SaveAdditiveScratchScene("Template.unity");

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var path = ScratchDir + "/FromTemplate.unity";
            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(path)).SetProperty("template", JsonValue.String(templatePath));
            var result = CommandTable.Dispatch(gate, Request("scene.create", @params));

            Assert.AreEqual(path, StringProp(result, "created"));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(path));
        }

        [Test]
        public void CreateScene_UnknownTemplate_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("path", JsonValue.String(ScratchDir + "/WontExist.unity"))
                .SetProperty("template", JsonValue.String(ScratchDir + "/NoSuchTemplate.unity"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("scene.create", @params)));

            StringAssert.Contains("NoSuchTemplate", ex.Message);
        }

        [Test]
        public void CreateScene_RegistersUndo_PerformUndoRemovesAsset()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var path = ScratchDir + "/UndoCreate.unity";
            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(path));
            CommandTable.Dispatch(gate, Request("scene.create", @params));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(path));

            Undo.PerformUndo();

            Assert.IsNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(path));
        }

        // --------------------------------------------------------------------------- scene.duplicate

        [Test]
        public void DuplicateScene_CopiesSceneAsset_NoLeaseTouched()
        {
            var sourcePath = SaveAdditiveScratchScene("DupSource.unity");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var destPath = ScratchDir + "/DupDest.unity";
                var @params = JsonValue.NewObject().SetProperty("sourcePath", JsonValue.String(sourcePath)).SetProperty("destPath", JsonValue.String(destPath));
                var result = CommandTable.Dispatch(gate, Request("scene.duplicate", @params));

                Assert.AreEqual(sourcePath, StringProp(result, "source"));
                Assert.AreEqual(destPath, StringProp(result, "destination"));
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(destPath));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void DuplicateScene_UnknownSource_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("sourcePath", JsonValue.String(ScratchDir + "/Ghost.unity"))
                .SetProperty("destPath", JsonValue.String(ScratchDir + "/DupDest2.unity"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("scene.duplicate", @params)));

            StringAssert.Contains("Ghost.unity", ex.Message);
        }

        [Test]
        public void DuplicateScene_RegistersUndo_PerformUndoRemovesAsset()
        {
            var sourcePath = SaveAdditiveScratchScene("DupUndoSource.unity");

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var destPath = ScratchDir + "/DupUndoDest.unity";
            var @params = JsonValue.NewObject().SetProperty("sourcePath", JsonValue.String(sourcePath)).SetProperty("destPath", JsonValue.String(destPath));
            CommandTable.Dispatch(gate, Request("scene.duplicate", @params));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(destPath));

            Undo.PerformUndo();

            Assert.IsNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(destPath));
        }

        // ------------------------------------------------------------------------------ scene.set_build

        [Test]
        public void SetBuildScenes_ReplacesBuildList_NoLeaseTouched()
        {
            var sceneA = SaveAdditiveScratchScene("BuildA.unity");
            var sceneB = SaveAdditiveScratchScene("BuildB.unity");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var entries = JsonValue.NewArray()
                    .Add(JsonValue.NewObject().SetProperty("path", JsonValue.String(sceneA)).SetProperty("enabled", JsonValue.Bool(true)))
                    .Add(JsonValue.NewObject().SetProperty("path", JsonValue.String(sceneB)).SetProperty("enabled", JsonValue.Bool(false)));
                var @params = JsonValue.NewObject().SetProperty("scenes", entries);
                var result = CommandTable.Dispatch(gate, Request("scene.set_build", @params));

                Assert.AreEqual(2L, IntProp(result, "count"));
                Assert.AreEqual(2, EditorBuildSettings.scenes.Length);
                Assert.AreEqual(sceneA, EditorBuildSettings.scenes[0].path);
                Assert.IsTrue(EditorBuildSettings.scenes[0].enabled);
                Assert.AreEqual(sceneB, EditorBuildSettings.scenes[1].path);
                Assert.IsFalse(EditorBuildSettings.scenes[1].enabled);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void SetBuildScenes_UnknownScene_ThrowsActionableError_LeavesBuildListUnchanged()
        {
            var originalCount = EditorBuildSettings.scenes.Length;

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var entries = JsonValue.NewArray().Add(JsonValue.NewObject().SetProperty("path", JsonValue.String(ScratchDir + "/Ghost.unity")));
            var @params = JsonValue.NewObject().SetProperty("scenes", entries);

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("scene.set_build", @params)));

            StringAssert.Contains("Ghost.unity", ex.Message);
            Assert.AreEqual(originalCount, EditorBuildSettings.scenes.Length);
        }

        [Test]
        public void SetBuildScenes_MissingScenesParam_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("scene.set_build", JsonValue.NewObject())));

            StringAssert.Contains("scenes", ex.Message);
        }
    }
}
