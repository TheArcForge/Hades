// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Linq;
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
    /// The six class-1 scene/hierarchy mutations (see the "52 Editor tools" plan's operation-class
    /// table - single-tick, no reload lease) dispatched exactly the way HadesBoot really dispatches
    /// them: through <see cref="CommandTable.Dispatch"/>, JSON in and JSON out, same convention
    /// LeaseCommandTests already established for this dispatch table.
    ///
    /// Every mutation test proves THREE things, because the plan calls out Undo as "the easiest
    /// property to forget" and a lease here as "a bug, not an optimisation":
    ///   1. the scene actually changed the way the result claims;
    ///   2. <see cref="UnityEditor.Undo.PerformUndo"/> reverts it (Undo.PerformUndo is Unity's own
    ///      sanctioned way to test custom Undo registration - see its own API doc);
    ///   3. the <see cref="ReloadGate"/> passed in was never touched - built on a
    ///      <see cref="FakeEditorLockApi"/> so "never touched" is directly observable (LockCalls/
    ///      UnlockCalls stay zero, IsHeld stays false), the same fake LeaseCommandTests uses to
    ///      prove the opposite property (that lease.* commands DO touch it).
    /// </summary>
    [TestFixture]
    public sealed class SceneCommandsTests
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
            Assert.AreEqual(0, fake.LockCalls, "a class-1 scene mutation must never call Lock");
            Assert.AreEqual(0, fake.UnlockCalls, "a class-1 scene mutation must never call Unlock");
            Assert.IsFalse(gate.IsHeld, "a class-1 scene mutation must never leave a lease held");
        }

        static string StringProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.String ? v.AsString() : null;

        static long IntProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.Integer ? v.AsInteger() : long.MinValue;

        /// <summary>Just asserts the field is present as a JSON integer - NOT that it is nonzero.
        /// Measured directly, across several attempts: Unsupported.GetLocalIdentifierInFile
        /// returns 0 for any object that is not "persistent" in Unity's sense, and that turns out
        /// to mean specifically "was deserialized from an on-disk file" - NOT merely "the scene
        /// has since been saved while this same in-memory instance is still alive". A plain
        /// `new GameObject()` still reports fileId 0 even after EditorSceneManager.SaveScene runs
        /// against the scene containing it; only re-opening the scene from disk (so the object is
        /// a genuinely deserialized instance) would change that, and no test in this file goes
        /// that far. A real Editor session's fileId IS meaningful for content that was already in
        /// the scene when the user opened it - this constraint is specific to same-session,
        /// never-reloaded objects, which is exactly what an EditMode test constructs.</summary>
        static void AssertHasFileId(JsonValue result) =>
            Assert.IsTrue(result.TryGetProperty("fileId", out var v) && v.Kind == JsonValueKind.Integer, "result must report a 'fileId'");

        // ---------------------------------------------------------------- scene.create_gameobject

        [Test]
        public void CreateGameObject_AtRoot_ReportsNameFileIdAndPath_NoLeaseTouched()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("name", JsonValue.String("Foo"));
                var result = CommandTable.Dispatch(gate, Request("scene.create_gameobject", @params));

                Assert.AreEqual("Foo", StringProp(result, "name"));
                Assert.AreEqual("Foo", StringProp(result, "path"));
                AssertHasFileId(result); // 0 is legitimate here - see AssertHasFileId's doc comment
                Assert.IsNull(StringProp(result, "parent"));

                var go = GameObject.Find("Foo");
                Assert.IsNotNull(go);
                Assert.IsNull(go.transform.parent);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void CreateGameObject_WithParent_NestsUnderIt()
        {
            var parent = new GameObject("Parent");
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("name", JsonValue.String("Child"))
                    .SetProperty("parent", JsonValue.String("Parent"));
                var result = CommandTable.Dispatch(gate, Request("scene.create_gameobject", @params));

                Assert.AreEqual("Parent/Child", StringProp(result, "path"));
                Assert.AreEqual("Parent", StringProp(result, "parent"));
                Assert.IsNotNull(parent.transform.Find("Child"));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void CreateGameObject_UnknownParent_ThrowsActionableError_NamingRootsAndNoPartialCreation()
        {
            new GameObject("ExistingRoot");
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("name", JsonValue.String("Child"))
                    .SetProperty("parent", JsonValue.String("DoesNotExist"));

                var ex = Assert.Throws<ArgumentException>(() =>
                    CommandTable.Dispatch(gate, Request("scene.create_gameobject", @params)));

                StringAssert.Contains("DoesNotExist", ex.Message);
                StringAssert.Contains("ExistingRoot", ex.Message);
                Assert.IsNull(GameObject.Find("Child"), "must not leave a half-created GameObject behind");

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void CreateGameObject_MissingName_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var ex = Assert.Throws<ArgumentException>(() =>
                CommandTable.Dispatch(gate, Request("scene.create_gameobject", JsonValue.NewObject())));

            StringAssert.Contains("name", ex.Message);
        }

        [Test]
        public void CreateGameObject_RegistersUndo_PerformUndoDestroysIt()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var @params = JsonValue.NewObject().SetProperty("name", JsonValue.String("Undoable"));
            CommandTable.Dispatch(gate, Request("scene.create_gameobject", @params));
            Assert.IsNotNull(GameObject.Find("Undoable"));

            Undo.PerformUndo();

            Assert.IsNull(GameObject.Find("Undoable"));
        }

        // ---------------------------------------------------------------- scene.create_primitive

        [Test]
        public void CreatePrimitive_BuildsRequestedTypeWithTransform_NoLeaseTouched()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("type", JsonValue.String("Cube"))
                    .SetProperty("name", JsonValue.String("MyCube"))
                    .SetProperty("position", JsonValue.NewArray().Add(JsonValue.Float(1)).Add(JsonValue.Float(2)).Add(JsonValue.Float(3)));

                var result = CommandTable.Dispatch(gate, Request("scene.create_primitive", @params));

                Assert.AreEqual("MyCube", StringProp(result, "name"));
                Assert.AreEqual("Cube", StringProp(result, "type"));
                AssertHasFileId(result); // 0 is legitimate here - see AssertHasFileId's doc comment

                var go = GameObject.Find("MyCube");
                Assert.IsNotNull(go);
                Assert.IsNotNull(go.GetComponent<MeshRenderer>());
                Assert.IsNotNull(go.GetComponent<BoxCollider>());
                Assert.AreEqual(new Vector3(1, 2, 3), go.transform.localPosition);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void CreatePrimitive_InvalidType_ThrowsActionableErrorListingValidTypes()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("type", JsonValue.String("NotAShape"));

            var ex = Assert.Throws<ArgumentException>(() =>
                CommandTable.Dispatch(gate, Request("scene.create_primitive", @params)));

            StringAssert.Contains("NotAShape", ex.Message);
            StringAssert.Contains("Cube", ex.Message);
        }

        [Test]
        public void CreatePrimitive_RegistersUndo_PerformUndoDestroysIt()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var @params = JsonValue.NewObject().SetProperty("type", JsonValue.String("Sphere")).SetProperty("name", JsonValue.String("Undoable"));
            CommandTable.Dispatch(gate, Request("scene.create_primitive", @params));
            Assert.IsNotNull(GameObject.Find("Undoable"));

            Undo.PerformUndo();

            Assert.IsNull(GameObject.Find("Undoable"));
        }

        // ---------------------------------------------------------------- scene.delete_gameobject

        [Test]
        public void DeleteGameObject_RemovesItAndReportsWhatWasDeleted_NoLeaseTouched()
        {
            var go = new GameObject("Doomed");
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Doomed"));
                var result = CommandTable.Dispatch(gate, Request("scene.delete_gameobject", @params));

                Assert.AreEqual("Doomed", StringProp(result, "deletedPath"));
                Assert.AreEqual("Doomed", StringProp(result, "deletedName"));
                AssertHasFileId(result); // 0 is legitimate here - see AssertHasFileId's doc comment
                Assert.IsTrue(go == null); // Unity's overridden == treats a destroyed object as null

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void DeleteGameObject_UnknownPath_ThrowsActionableError()
        {
            new GameObject("ExistingRoot");
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Ghost"));

            var ex = Assert.Throws<ArgumentException>(() =>
                CommandTable.Dispatch(gate, Request("scene.delete_gameobject", @params)));

            StringAssert.Contains("Ghost", ex.Message);
            StringAssert.Contains("ExistingRoot", ex.Message);

            // Defect 5 (docs/backlog/mutation-tool-defects.md): this used to say "Call
            // scene_get_hierarchy to see the full tree" - scene_get_hierarchy does not exist
            // post-consolidation (folded into inspect_asset).
            StringAssert.Contains("inspect_asset", ex.Message);
            StringAssert.DoesNotContain("scene_get_hierarchy", ex.Message);
            LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(ex.Message);
        }

        [Test]
        public void DeleteGameObject_RegistersUndo_PerformUndoRecreatesIt()
        {
            new GameObject("Doomed");
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Doomed"));
            CommandTable.Dispatch(gate, Request("scene.delete_gameobject", @params));
            Assert.IsNull(GameObject.Find("Doomed"));

            Undo.PerformUndo();

            Assert.IsNotNull(GameObject.Find("Doomed"));
        }

        // ---------------------------------------------------------------- scene.reparent_gameobject

        [Test]
        public void ReparentGameObject_MovesUnderNewParent_NoLeaseTouched()
        {
            var child = new GameObject("Child");
            var newParent = new GameObject("NewParent");
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("path", JsonValue.String("Child"))
                    .SetProperty("newParent", JsonValue.String("NewParent"));
                var result = CommandTable.Dispatch(gate, Request("scene.reparent_gameobject", @params));

                Assert.AreEqual("NewParent/Child", StringProp(result, "path"));
                Assert.AreSame(newParent.transform, child.transform.parent);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void ReparentGameObject_EmptyNewParent_MovesToRoot()
        {
            var parent = new GameObject("Parent");
            var child = new GameObject("Child");
            child.transform.SetParent(parent.transform);
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Parent/Child"));
            var result = CommandTable.Dispatch(gate, Request("scene.reparent_gameobject", @params));

            Assert.AreEqual("Child", StringProp(result, "path"));
            Assert.IsNull(child.transform.parent);
        }

        [Test]
        public void ReparentGameObject_UnknownSource_ThrowsActionableError()
        {
            new GameObject("ExistingRoot");
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Ghost"));

            var ex = Assert.Throws<ArgumentException>(() =>
                CommandTable.Dispatch(gate, Request("scene.reparent_gameobject", @params)));

            StringAssert.Contains("Ghost", ex.Message);
            StringAssert.Contains("ExistingRoot", ex.Message);
        }

        [Test]
        public void ReparentGameObject_RegistersUndo_PerformUndoRestoresPriorParent()
        {
            var originalParent = new GameObject("Original");
            var child = new GameObject("Child");
            child.transform.SetParent(originalParent.transform);
            var newParent = new GameObject("NewParent");

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var @params = JsonValue.NewObject()
                .SetProperty("path", JsonValue.String("Original/Child"))
                .SetProperty("newParent", JsonValue.String("NewParent"));
            CommandTable.Dispatch(gate, Request("scene.reparent_gameobject", @params));
            Assert.AreSame(newParent.transform, child.transform.parent);

            Undo.PerformUndo();

            Assert.AreSame(originalParent.transform, child.transform.parent);
        }

        // -------------------------------------------- scene.reparent_gameobject - cycle guard (F21)

        [Test]
        public void ReparentGameObject_UnderItself_Refused_NoLeaseTouched()
        {
            var go = new GameObject("SelfParent");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("path", JsonValue.String("SelfParent"))
                    .SetProperty("newParent", JsonValue.String("SelfParent"));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("scene.reparent_gameobject", @params)));

                StringAssert.Contains("SelfParent", ex.Message);
                Assert.IsNull(go.transform.parent);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void ReparentGameObject_UnderOwnDescendant_Refused_NoLeaseTouched()
        {
            var grandparent = new GameObject("Grandparent");
            var parent = new GameObject("Parent");
            parent.transform.SetParent(grandparent.transform);
            var child = new GameObject("Child");
            child.transform.SetParent(parent.transform);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("path", JsonValue.String("Grandparent"))
                    .SetProperty("newParent", JsonValue.String("Grandparent/Parent/Child"));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("scene.reparent_gameobject", @params)));

                StringAssert.Contains("Grandparent", ex.Message);
                Assert.IsNull(grandparent.transform.parent, "the cycle must be refused, leaving the hierarchy exactly as it was");

                AssertNeverTouchedLease(fake, gate);
            }
        }

        // ---------------------------------------------------------------- scene.rename_gameobject

        [Test]
        public void RenameGameObject_ChangesName_NoLeaseTouched()
        {
            var go = new GameObject("OldName");
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("path", JsonValue.String("OldName"))
                    .SetProperty("newName", JsonValue.String("NewName"));
                var result = CommandTable.Dispatch(gate, Request("scene.rename_gameobject", @params));

                Assert.AreEqual("NewName", StringProp(result, "name"));
                Assert.AreEqual("NewName", go.name);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void RenameGameObject_UnknownPath_ThrowsActionableError()
        {
            new GameObject("ExistingRoot");
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Ghost")).SetProperty("newName", JsonValue.String("X"));

            var ex = Assert.Throws<ArgumentException>(() =>
                CommandTable.Dispatch(gate, Request("scene.rename_gameobject", @params)));

            StringAssert.Contains("Ghost", ex.Message);
        }

        [Test]
        public void RenameGameObject_RegistersUndo_PerformUndoRestoresOldName()
        {
            var go = new GameObject("OldName");
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("OldName")).SetProperty("newName", JsonValue.String("NewName"));
            CommandTable.Dispatch(gate, Request("scene.rename_gameobject", @params));
            Assert.AreEqual("NewName", go.name);

            Undo.PerformUndo();

            Assert.AreEqual("OldName", go.name);
        }

        // ---------------------------------------------------------------- scene.setup

        [Test]
        public void SceneSetup_CreatesNestedHierarchyWithComponentsAndProperties_NoLeaseTouched()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var childDef = JsonValue.NewObject()
                    .SetProperty("name", JsonValue.String("Child"))
                    .SetProperty("components", JsonValue.NewArray().Add(
                        JsonValue.NewObject()
                            .SetProperty("type", JsonValue.String("Rigidbody"))
                            .SetProperty("properties", JsonValue.NewObject().SetProperty("mass", JsonValue.Float(5)))));

                var rootDef = JsonValue.NewObject()
                    .SetProperty("name", JsonValue.String("Root"))
                    .SetProperty("children", JsonValue.NewArray().Add(childDef));

                var @params = JsonValue.NewObject().SetProperty("gameObjects", JsonValue.NewArray().Add(rootDef));
                var result = CommandTable.Dispatch(gate, Request("scene.setup", @params));

                Assert.IsTrue(result.TryGetProperty("results", out var results));
                Assert.AreEqual(2, results.Items.Count);
                Assert.IsTrue(result.TryGetProperty("errors", out var errs));
                Assert.AreEqual(0, errs.Items.Count);

                var root = GameObject.Find("Root");
                Assert.IsNotNull(root);
                var child = root.transform.Find("Child");
                Assert.IsNotNull(child);
                var rb = child.GetComponent<Rigidbody>();
                Assert.IsNotNull(rb);
                Assert.AreEqual(5f, rb.mass);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void SceneSetup_UnknownComponentType_ReportsPerEntryErrorButKeepsGoing()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var def = JsonValue.NewObject()
                .SetProperty("name", JsonValue.String("Root"))
                .SetProperty("components", JsonValue.NewArray().Add(
                    JsonValue.NewObject().SetProperty("type", JsonValue.String("NoSuchComponent"))));

            var @params = JsonValue.NewObject().SetProperty("gameObjects", JsonValue.NewArray().Add(def));
            var result = CommandTable.Dispatch(gate, Request("scene.setup", @params));

            Assert.IsNotNull(GameObject.Find("Root"), "the GameObject itself must still be created");
            Assert.IsTrue(result.TryGetProperty("errors", out var errs));
            Assert.AreEqual(1, errs.Items.Count);
            StringAssert.Contains("NoSuchComponent", errs.Items[0].TryGetProperty("error", out var e) ? e.AsString() : "");
        }

        [Test]
        public void SceneSetup_RegistersUndoAsOneGroup_PerformUndoRemovesEverything()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var def = JsonValue.NewObject().SetProperty("name", JsonValue.String("Root"))
                .SetProperty("children", JsonValue.NewArray().Add(JsonValue.NewObject().SetProperty("name", JsonValue.String("Child"))));
            var @params = JsonValue.NewObject().SetProperty("gameObjects", JsonValue.NewArray().Add(def));
            CommandTable.Dispatch(gate, Request("scene.setup", @params));
            Assert.IsNotNull(GameObject.Find("Root"));

            Undo.PerformUndo();

            Assert.IsNull(GameObject.Find("Root"));
            Assert.IsNull(GameObject.Find("Child"));
        }
    }

    /// <summary>Shared scene hygiene for every command-mutation test file - a fresh, empty scene
    /// so one test's GameObjects never leak into the next. Internal (not private) so
    /// ComponentCommandsTests can reuse it instead of a second copy.</summary>
    internal static class SceneTestFixtures
    {
        const string ScratchScenePath = "Assets/Tests/_HadesCommandTestsScratchScene.unity";

        /// <summary>A fresh, empty, saved scene (saved so the scene itself at least has a path -
        /// see AssertHasFileId's doc comment for why that alone still is not enough to make a
        /// same-session object's fileId nonzero).</summary>
        public static void ResetScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScratchScenePath);
        }
    }
}
