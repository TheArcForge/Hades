// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.IO;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// asset.move (class-1: single-tick, no reload lease) alongside the class-2 asset.import /
    /// asset.set_import_settings / asset.set_clip_import_settings handlers Plan 9 Task 3 adds to
    /// the same plugin file - see AssetCommands' own class doc comment. No PerformUndo-revert test
    /// for asset.move - see AssetCommands.MoveAsset's own doc comment: there is no Unity Undo
    /// primitive covering an asset's project-relative path the way
    /// <see cref="UnityEditor.Undo.RecordObject"/> covers a serialized field.
    ///
    /// The class-2 trio follows PrefabCommandsTests' own convention: every test proves the lease
    /// WAS acquired and released exactly once (<see cref="AssertLeaseCleanlyReleased"/>), including
    /// on a thrown exception - see that file's own class doc comment for the full "why" (the
    /// deliberate inverse of class 3's BeginScriptEditing semantics).
    /// </summary>
    [TestFixture]
    public sealed class AssetCommandsTests
    {
        const string ScratchDir = "Assets/Tests/_HadesAssetScratch";

        [SetUp]
        public void SetUp()
        {
            Undo.ClearAll();
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
            AssetDatabase.CreateFolder("Assets/Tests", "_HadesAssetScratch");
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
            Assert.AreEqual(0, fake.LockCalls, "a class-1 asset mutation must never call Lock");
            Assert.AreEqual(0, fake.UnlockCalls, "a class-1 asset mutation must never call Unlock");
            Assert.IsFalse(gate.IsHeld, "a class-1 asset mutation must never leave a lease held");
        }

        /// <summary>The class-2 counterpart - see PrefabCommandsTests' identical helper for the
        /// full rationale (balance, not an exact call count).</summary>
        static void AssertLeaseCleanlyReleased(FakeEditorLockApi fake, ReloadGate gate)
        {
            Assert.IsFalse(gate.IsHeld, "a class-2 asset operation must never leave a lease held");
            Assert.GreaterOrEqual(fake.LockCalls, 1, "expected at least one Lock across the call(s) so far");
            Assert.AreEqual(fake.LockCalls, fake.UnlockCalls, "every Lock must be balanced by exactly one Unlock - no leaked lease");
            Assert.AreEqual(0, fake.Counter, "the fake's signed counter must land back at 0");
        }

        static string AbsolutePath(string projectRelativePath) =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));

        static string StringProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.String ? v.AsString() : null;

        // ---------------------------------------------------------------------------------- asset.move

        [Test]
        public void MoveAsset_RenamesToDestination_NoLeaseTouched()
        {
            var sourcePath = ScratchDir + "/Source.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), sourcePath);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var destPath = ScratchDir + "/Renamed.mat";
                var @params = JsonValue.NewObject().SetProperty("sourcePath", JsonValue.String(sourcePath)).SetProperty("destPath", JsonValue.String(destPath));
                var result = CommandTable.Dispatch(gate, Request("asset.move", @params));

                Assert.AreEqual(sourcePath, StringProp(result, "source"));
                Assert.AreEqual(destPath, StringProp(result, "destination"));
                Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(sourcePath));
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(destPath));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void MoveAsset_ToNewSubfolder_CreatesFolderAndMoves()
        {
            var sourcePath = ScratchDir + "/ToMove.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), sourcePath);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var destPath = ScratchDir + "/Sub/Nested/Moved.mat";
            var @params = JsonValue.NewObject().SetProperty("sourcePath", JsonValue.String(sourcePath)).SetProperty("destPath", JsonValue.String(destPath));
            CommandTable.Dispatch(gate, Request("asset.move", @params));

            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(destPath));
        }

        [Test]
        public void MoveAsset_UnknownSource_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("sourcePath", JsonValue.String(ScratchDir + "/Ghost.mat"))
                .SetProperty("destPath", JsonValue.String(ScratchDir + "/WontHappen.mat"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("asset.move", @params)));

            StringAssert.Contains("Ghost.mat", ex.Message);
        }

        // -------------------------------------------------------- asset.move - destPath path guard (F16/F17)

        [Test]
        public void MoveAsset_TraversalDestPath_RefusedBeforeAnyWrite_NoLeaseTouched()
        {
            var sourcePath = ScratchDir + "/MoveSource.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), sourcePath);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("sourcePath", JsonValue.String(sourcePath))
                    .SetProperty("destPath", JsonValue.String("Assets/../EscapedMove.mat"));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("asset.move", @params)));
                StringAssert.Contains("EscapedMove.mat", ex.Message);
                Assert.IsFalse(File.Exists(AbsolutePath("EscapedMove.mat")));
                // A refused move must not have moved anything - the source stays exactly where it was.
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(sourcePath));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        // ------------------------------------------------- asset.move - self/descendant cycle guard

        /// <summary>Uneven-validation audit: the same cycle hazard
        /// SceneCommandsTests.ReparentGameObject_UnderItself_Refused_NoLeaseTouched (F21) pins for the
        /// scene hierarchy, here for a "move" onto the source's own exact path.</summary>
        [Test]
        public void MoveAsset_SourceEqualsDestination_RefusedBeforeAnyWrite_NoLeaseTouched()
        {
            var path = ScratchDir + "/SelfMove.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("sourcePath", JsonValue.String(path)).SetProperty("destPath", JsonValue.String(path));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("asset.move", @params)));
                StringAssert.Contains("cycle", ex.Message);

                // A refused self-move must not have touched the asset at all.
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(path));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        /// <summary>Uneven-validation audit: the folder-hierarchy analogue of
        /// SceneCommandsTests.ReparentGameObject_UnderOwnDescendant_Refused_NoLeaseTouched (F21) -
        /// moving a folder to a path INSIDE itself has no sensible outcome.</summary>
        [Test]
        public void MoveAsset_FolderIntoOwnDescendant_RefusedBeforeAnyWrite_OriginalFolderIntact()
        {
            var folderPath = ScratchDir + "/SelfNestFolder";
            AssetDatabase.CreateFolder(ScratchDir, "SelfNestFolder");
            var markerAssetPath = folderPath + "/Marker.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), markerAssetPath);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var destPath = folderPath + "/Nested/SelfNestFolder";
                var @params = JsonValue.NewObject().SetProperty("sourcePath", JsonValue.String(folderPath)).SetProperty("destPath", JsonValue.String(destPath));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("asset.move", @params)));
                StringAssert.Contains("cycle", ex.Message);

                // The original folder and its contents must still be exactly where they were - no
                // partial move, no orphaned intermediate folder created below it.
                Assert.IsTrue(AssetDatabase.IsValidFolder(folderPath));
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(markerAssetPath));
                Assert.IsFalse(AssetDatabase.IsValidFolder(folderPath + "/Nested"));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void MoveAsset_SiblingWithSimilarPrefix_NotTreatedAsDescendant_MovesNormally()
        {
            // Guards against an overly-broad string check: "Assets/.../FooBar" must not be mistaken
            // for a descendant of "Assets/.../Foo" just because it shares a text prefix - this rename
            // must succeed normally, exactly as it would have before the cycle guard was added.
            var sourcePath = ScratchDir + "/Foo";
            AssetDatabase.CreateFolder(ScratchDir, "Foo");

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var destPath = ScratchDir + "/FooBar";
            var @params = JsonValue.NewObject().SetProperty("sourcePath", JsonValue.String(sourcePath)).SetProperty("destPath", JsonValue.String(destPath));

            CommandTable.Dispatch(gate, Request("asset.move", @params));

            Assert.IsTrue(AssetDatabase.IsValidFolder(destPath));
            Assert.IsFalse(AssetDatabase.IsValidFolder(sourcePath));
        }

        // -------------------------------------------------------------------------- asset.import

        [Test]
        public void ImportAsset_FileExistsOnDisk_ImportsIt_VerifiedViaAssetDatabase_LeaseCleanlyReleased()
        {
            var relativePath = ScratchDir + "/dropped.txt";
            File.WriteAllText(AbsolutePath(relativePath), "hello");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(relativePath));
                var result = CommandTable.Dispatch(gate, Request("asset.import", @params));

                Assert.AreEqual(relativePath, StringProp(result, "path"));
                Assert.IsNotEmpty(StringProp(result, "guid"));
                Assert.AreEqual(AssetDatabase.AssetPathToGUID(relativePath), StringProp(result, "guid"));

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        [Test]
        public void ImportAsset_NothingOnDisk_ThrowsActionableError_StillReleasesLease()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(ScratchDir + "/Ghost.txt"));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("asset.import", @params)));
                StringAssert.Contains("Ghost.txt", ex.Message);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        /// <summary>Uneven-validation audit: unlike asset.move's destPath (guarded since the F16/F17
        /// round), asset.import's own 'path' was never routed through AssetPathGuard - its existence
        /// check is a RAW File.Exists/Directory.Exists against an unconfined ToAbsolutePath, reachable
        /// before anything refuses a traversal path. Asserts the SAME AssetPathGuard message shape
        /// every create-family tool already produces, not just "some ArgumentException" - a test
        /// asserting only Ghost.txt's own path substring would pass even against the OLD unguarded
        /// code (that path also appears inside the old "nothing exists on disk" message), so this
        /// pins the actual guard, not merely "an error happened".</summary>
        [Test]
        public void ImportAsset_TraversalPath_RefusedBeforeAnyFilesystemCheck_StillReleasesLease()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Assets/../EscapedImport.txt"));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("asset.import", @params)));
                StringAssert.Contains("not a plain project-relative path", ex.Message);
                Assert.IsFalse(File.Exists(AbsolutePath("EscapedImport.txt")));

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        // -------------------------------------------------------------- asset.set_import_settings

        [Test]
        public void SetImportSettings_SetsUserDataOnTexture_VerifiedViaFreshImporterLookup_LeaseCleanlyReleased()
        {
            // m_UserData (AssetImporter.userData) rather than a TextureImporter-specific field
            // like isReadable: it is declared on the base AssetImporter class itself and has been
            // a plain top-level serialized string there across Unity versions, so this test is not
            // exposed to any texture-importer-specific field layout this Unity version might have
            // changed (measured directly: an earlier attempt at "m_IsReadable" did not round-trip
            // as expected against this Unity version's TextureImporter).
            var relativePath = ScratchDir + "/tex.png";
            var texture = new Texture2D(4, 4);
            var pngBytes = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);
            File.WriteAllBytes(AbsolutePath(relativePath), pngBytes);
            AssetDatabase.ImportAsset(relativePath);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("path", JsonValue.String(relativePath))
                    .SetProperty("properties", JsonValue.NewObject().SetProperty("m_UserData", JsonValue.String("hades-marker")));
                var result = CommandTable.Dispatch(gate, Request("asset.set_import_settings", @params));

                Assert.IsTrue(result.TryGetProperty("applied", out var applied) && applied.Kind == JsonValueKind.Array && applied.Items.Count == 1);

                // Verified via a FRESH importer lookup, not by trusting the response.
                var reloadedImporter = AssetImporter.GetAtPath(relativePath);
                Assert.AreEqual("hades-marker", reloadedImporter.userData);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        [Test]
        public void SetImportSettings_UnknownProperty_ReportsFailureNotException_LeaseCleanlyReleased()
        {
            var relativePath = ScratchDir + "/tex2.png";
            var texture = new Texture2D(4, 4);
            var pngBytes = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);
            File.WriteAllBytes(AbsolutePath(relativePath), pngBytes);
            AssetDatabase.ImportAsset(relativePath);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("path", JsonValue.String(relativePath))
                    .SetProperty("properties", JsonValue.NewObject().SetProperty("not_a_real_property", JsonValue.Bool(true)));
                var result = CommandTable.Dispatch(gate, Request("asset.set_import_settings", @params));

                Assert.IsTrue(result.TryGetProperty("failed", out var failed) && failed.Kind == JsonValueKind.Array && failed.Items.Count == 1);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        [Test]
        public void SetImportSettings_NoImporterAtPath_ThrowsActionableError_StillReleasesLease()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("path", JsonValue.String(ScratchDir + "/Ghost.png"))
                    .SetProperty("properties", JsonValue.NewObject().SetProperty("m_IsReadable", JsonValue.Bool(true)));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("asset.set_import_settings", @params)));
                StringAssert.Contains("Ghost.png", ex.Message);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        /// <summary>Uneven-validation audit: same gap as ImportAsset_TraversalPath's own doc comment
        /// describes, for asset.set_import_settings' 'path' instead.</summary>
        [Test]
        public void SetImportSettings_TraversalPath_RefusedBeforeAnyWork_StillReleasesLease()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("path", JsonValue.String("Assets/../EscapedSettings.png"))
                    .SetProperty("properties", JsonValue.NewObject().SetProperty("m_IsReadable", JsonValue.Bool(true)));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("asset.set_import_settings", @params)));
                StringAssert.Contains("not a plain project-relative path", ex.Message);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        // --------------------------------------------------------- asset.set_clip_import_settings

        [Test]
        public void SetClipImportSettings_NonModelAsset_ThrowsActionableError_StillReleasesLease()
        {
            var relativePath = ScratchDir + "/NotAModel.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), relativePath);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("path", JsonValue.String(relativePath))
                    .SetProperty("clips", JsonValue.NewArray().Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("Take 001"))));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("asset.set_clip_import_settings", @params)));
                StringAssert.Contains("ModelImporter", ex.Message);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        [Test]
        public void SetClipImportSettings_ModelWithNoClips_ThrowsActionableError_StillReleasesLease()
        {
            var relativePath = ScratchDir + "/Static.obj";
            File.WriteAllText(AbsolutePath(relativePath), "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
            AssetDatabase.ImportAsset(relativePath);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("path", JsonValue.String(relativePath))
                    .SetProperty("clips", JsonValue.NewArray().Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("Take 001"))));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("asset.set_clip_import_settings", @params)));
                StringAssert.Contains("No animation clips found", ex.Message);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        [Test]
        public void SetClipImportSettings_UnknownAsset_ThrowsActionableError_StillReleasesLease()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("path", JsonValue.String(ScratchDir + "/Ghost.fbx"))
                    .SetProperty("clips", JsonValue.NewArray().Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("Take 001"))));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("asset.set_clip_import_settings", @params)));
                StringAssert.Contains("Ghost.fbx", ex.Message);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        /// <summary>Uneven-validation audit: same gap as ImportAsset_TraversalPath's own doc comment
        /// describes, for asset.set_clip_import_settings' 'path' instead.</summary>
        [Test]
        public void SetClipImportSettings_TraversalPath_RefusedBeforeAnyWork_StillReleasesLease()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("path", JsonValue.String("Assets/../EscapedClipSettings.fbx"))
                    .SetProperty("clips", JsonValue.NewArray().Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("Take 001"))));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("asset.set_clip_import_settings", @params)));
                StringAssert.Contains("not a plain project-relative path", ex.Message);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        // ---------------------------------------------------------------------- lease busy elsewhere

        [Test]
        public void ClassTwoAssetCall_WhileADifferentLeaseIsHeld_ThrowsActionableError_DoesNotStealOrReleaseIt()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                Assert.IsTrue(gate.Acquire("owner", TimeSpan.FromMinutes(5)));

                var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(ScratchDir + "/whatever.txt"));
                var ex = Assert.Throws<InvalidOperationException>(() => CommandTable.Dispatch(gate, Request("asset.import", @params)));
                StringAssert.Contains("owner", ex.Message);

                Assert.IsTrue(gate.IsHeld);
                Assert.AreEqual(1, fake.LockCalls);
                Assert.AreEqual(0, fake.UnlockCalls);

                gate.Release("owner");
            }
        }
    }
}
