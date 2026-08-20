// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.IO;
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

        static string AbsolutePath(string projectRelativePath) =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));

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

        // -------------------------------------------------------------- scene.save - path guard (F16/F17/F20)

        [Test]
        public void SaveScene_WithTraversalPath_RefusedBeforeAnyWrite()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Assets/../EscapedSave.unity"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("scene.save", @params)));

            StringAssert.Contains("EscapedSave.unity", ex.Message);
            Assert.IsFalse(File.Exists(AbsolutePath("EscapedSave.unity")));
        }

        [Test]
        public void SaveScene_WithAbsolutePathIntoAssets_Refused()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            // A CORRECT absolute path landing inside Assets/ must still be refused - only a plain
            // project-relative form is accepted (see AssetPathGuard's own doc comment for why).
            var path = AbsolutePath(ScratchDir + "/AbsSave.unity");
            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(path));

            Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("scene.save", @params)));
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(ScratchDir + "/AbsSave.unity"));
        }

        [Test]
        public void SaveScene_WithNonNormalizedDotSlashPath_Refused()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(ScratchDir + "/./NotNormalizedSave.unity"));

            Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("scene.save", @params)));
            // Not even created at the normalized equivalent - a caller must resubmit the clean form itself.
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(ScratchDir + "/NotNormalizedSave.unity"));
        }

        [Test]
        public void SaveScene_WithPathComponentOverSafeByteLimit_Refused()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var tooLongName = new string('a', 245) + ".unity"; // 251 bytes, over the 240-byte safe bound
            var path = ScratchDir + "/" + tooLongName;
            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(path));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("scene.save", @params)));

            StringAssert.Contains("bytes", ex.Message);
            Assert.IsFalse(File.Exists(AbsolutePath(path)));
        }

        /// <summary>Unlike scene.create/scene.duplicate, scene.save's explicit-path branch is a
        /// SAVE-AS: it may legitimately land on a .unity file that already exists - including,
        /// unremarkably, the currently open scene's own path (the common case: a caller passes
        /// 'path' explicitly instead of omitting it). This is the reason scene.save routes through
        /// RequireWellFormedProjectPath rather than RequireNewAssetPath - this test pins that choice
        /// down so a future change cannot silently swap in the existence-refusing guard instead.</summary>
        [Test]
        public void SaveScene_WithPathOfAlreadyExistingUnrelatedScene_Overwrites()
        {
            var existingPath = SaveAdditiveScratchScene("AlreadyExists.unity");
            new GameObject("SaveAsMarker");

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(existingPath));
            var result = CommandTable.Dispatch(gate, Request("scene.save", @params));

            Assert.AreEqual(existingPath, StringProp(result, "saved"));
            Assert.AreEqual(existingPath, SceneManager.GetActiveScene().path);
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

        // -------------------------------------------------------- scene.create - path guard (F16/F17/F20)

        [Test]
        public void CreateScene_TraversalPath_RefusedBeforeAnyWrite()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Assets/../Escaped.unity"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("scene.create", @params)));

            StringAssert.Contains("Escaped.unity", ex.Message);
            Assert.IsFalse(File.Exists(AbsolutePath("Escaped.unity")));
        }

        [Test]
        public void CreateScene_ExistingFile_Refused()
        {
            var path = SaveAdditiveScratchScene("AlreadyThere.unity");

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(path));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("scene.create", @params)));

            StringAssert.Contains("already exists", ex.Message);
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

        // ---------------------------------------------------- scene.duplicate - path guard (F16/F17/F20/F21)

        [Test]
        public void DuplicateScene_TraversalDestPath_RefusedBeforeAnyWrite()
        {
            var sourcePath = SaveAdditiveScratchScene("TraversalDupSource.unity");

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("sourcePath", JsonValue.String(sourcePath))
                .SetProperty("destPath", JsonValue.String("Assets/../EscapedDup.unity"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("scene.duplicate", @params)));

            StringAssert.Contains("EscapedDup.unity", ex.Message);
            Assert.IsFalse(File.Exists(AbsolutePath("EscapedDup.unity")));
        }

        [Test]
        public void DuplicateScene_ExistingDestFile_Refused()
        {
            var sourcePath = SaveAdditiveScratchScene("ExistDupSource.unity");
            var destPath = SaveAdditiveScratchScene("ExistDupDest.unity");

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("sourcePath", JsonValue.String(sourcePath)).SetProperty("destPath", JsonValue.String(destPath));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("scene.duplicate", @params)));

            StringAssert.Contains("already exists", ex.Message);
        }

        /// <summary>F21: a scene duplicated onto itself was accepted. The existence check alone
        /// closes this - destPath already has a file at it (itself) - with no separate
        /// source-equals-dest special case needed.</summary>
        [Test]
        public void DuplicateScene_OntoItself_Refused()
        {
            var path = SaveAdditiveScratchScene("DupSelf.unity");

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("sourcePath", JsonValue.String(path)).SetProperty("destPath", JsonValue.String(path));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("scene.duplicate", @params)));

            StringAssert.Contains("already exists", ex.Message);
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
