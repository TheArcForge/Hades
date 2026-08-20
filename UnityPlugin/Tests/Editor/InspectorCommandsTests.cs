// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// inspector.select (class 1) and inspector.inspect (class 4, live-state read - see the "52
    /// Editor tools" plan's operation-class table, Task 5) - both defined in InspectorCommands.cs,
    /// see that file's own class doc comment for why they sit together despite the different
    /// classes. Neither ever touches the ReloadGate passed in; <see cref="AssertNeverTouchedLease"/>
    /// is shared by both halves of this fixture.
    /// </summary>
    [TestFixture]
    public sealed class InspectorCommandsTests
    {
        [SetUp]
        public void SetUp()
        {
            SceneTestFixtures.ResetScene();
            Undo.ClearAll();
            Selection.activeGameObject = null;
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            Selection.activeGameObject = null;
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
            Assert.AreEqual(0, fake.LockCalls, "neither inspector.select nor inspector.inspect may ever call Lock");
            Assert.AreEqual(0, fake.UnlockCalls, "neither inspector.select nor inspector.inspect may ever call Unlock");
            Assert.IsFalse(gate.IsHeld, "neither inspector.select nor inspector.inspect may ever leave a lease held");
        }

        static string StringProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.String ? v.AsString() : null;

        /// <summary>Just asserts the field is present as a JSON integer - NOT that it is nonzero.
        /// Same caveat as SceneCommandsTests' own AssertHasFileId: Unsupported.GetLocalIdentifierInFile
        /// returns 0 for a same-session, never-reloaded-from-disk object, which is exactly what an
        /// EditMode test constructs.</summary>
        static void AssertHasFileId(JsonValue result) =>
            Assert.IsTrue(result.TryGetProperty("fileId", out var v) && v.Kind == JsonValueKind.Integer, "result must report a 'fileId'");

        // ------------------------------------------------------------------------------- inspector.select

        [Test]
        public void SelectGameObject_SetsActiveSelection_NoLeaseTouched()
        {
            var go = new GameObject("Target");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Target"));
                var result = CommandTable.Dispatch(gate, Request("inspector.select", @params));

                Assert.AreEqual("Target", StringProp(result, "selected"));
                Assert.AreSame(go, Selection.activeGameObject);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void SelectGameObject_UnknownPath_ThrowsActionableError()
        {
            new GameObject("ExistingRoot");
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Ghost"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("inspector.select", @params)));

            StringAssert.Contains("Ghost", ex.Message);
            StringAssert.Contains("ExistingRoot", ex.Message);
        }

        /// <summary>Not a claim this plugin implements - Unity's OWN Editor tracks selection
        /// changes on its undo stack automatically (see InspectorCommands' own doc comment). This
        /// verifies that built-in behaviour still holds when the selection is changed through this
        /// tool rather than through the Hierarchy window.</summary>
        [Test]
        public void SelectGameObject_ChangeIsRevertedByUnitysOwnSelectionUndoHistory()
        {
            var first = new GameObject("First");
            Selection.activeGameObject = first;
            var second = new GameObject("Second");

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Second"));
            CommandTable.Dispatch(gate, Request("inspector.select", @params));
            Assert.AreSame(second, Selection.activeGameObject);

            Undo.PerformUndo();

            Assert.AreSame(first, Selection.activeGameObject);
        }

        // ------------------------------------------------------------------------------- inspector.inspect

        [Test]
        public void InspectGameObject_ReturnsNameTransformTagAndFileId_NoLeaseTouched()
        {
            var go = new GameObject("Widget");
            go.transform.position = new Vector3(1f, 2f, 3f);
            go.transform.localScale = new Vector3(2f, 2f, 2f);
            go.tag = "Untagged";

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Widget"));
                var result = CommandTable.Dispatch(gate, Request("inspector.inspect", @params));

                Assert.AreEqual("Widget", StringProp(result, "name"));
                Assert.AreEqual("Widget", StringProp(result, "path"));
                AssertHasFileId(result); // 0 is legitimate here - see AssertHasFileId's doc comment
                Assert.IsTrue(result.TryGetProperty("active", out var active) && active.Kind == JsonValueKind.Boolean && active.AsBoolean());
                Assert.AreEqual("Untagged", StringProp(result, "tag"));
                Assert.IsTrue(result.TryGetProperty("isStatic", out var isStatic) && isStatic.Kind == JsonValueKind.Boolean && !isStatic.AsBoolean());

                Assert.IsTrue(result.TryGetProperty("position", out var position) && position.Kind == JsonValueKind.Object);
                Assert.AreEqual(1.0, position.TryGetProperty("x", out var px) ? px.AsDouble() : double.NaN, 0.0001);
                Assert.AreEqual(2.0, position.TryGetProperty("y", out var py) ? py.AsDouble() : double.NaN, 0.0001);
                Assert.AreEqual(3.0, position.TryGetProperty("z", out var pz) ? pz.AsDouble() : double.NaN, 0.0001);

                Assert.IsTrue(result.TryGetProperty("scale", out var scale) && scale.Kind == JsonValueKind.Object);
                Assert.AreEqual(2.0, scale.TryGetProperty("x", out var sx) ? sx.AsDouble() : double.NaN, 0.0001);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void InspectGameObject_ListsChildrenByNameAndCount_NoLeaseTouched()
        {
            var parent = new GameObject("Parent");
            var childA = new GameObject("ChildA");
            var childB = new GameObject("ChildB");
            childA.transform.SetParent(parent.transform);
            childB.transform.SetParent(parent.transform);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Parent"));
                var result = CommandTable.Dispatch(gate, Request("inspector.inspect", @params));

                Assert.AreEqual(2, result.TryGetProperty("childCount", out var cc) ? cc.AsInteger() : -1);
                Assert.IsTrue(result.TryGetProperty("children", out var children) && children.Kind == JsonValueKind.Array);
                Assert.AreEqual(2, children.Items.Count);
                Assert.AreEqual("ChildA", children.Items[0].AsString());
                Assert.AreEqual("ChildB", children.Items[1].AsString());

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void InspectGameObject_ReturnsComponentsWithStructuredSerializedProperties_NoLeaseTouched()
        {
            var go = new GameObject("Boxy");
            var collider = go.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.enabled = false;

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Boxy"));
                var result = CommandTable.Dispatch(gate, Request("inspector.inspect", @params));

                Assert.IsTrue(result.TryGetProperty("components", out var components) && components.Kind == JsonValueKind.Array);
                // At least Transform + BoxCollider.
                Assert.GreaterOrEqual(components.Items.Count, 2);

                JsonValue boxColliderDump = null;
                foreach (var component in components.Items)
                    if (StringProp(component, "type") == "BoxCollider") boxColliderDump = component;

                Assert.IsNotNull(boxColliderDump, "expected a BoxCollider entry in 'components'");
                AssertHasFileId(boxColliderDump);
                StringAssert.Contains("UnityEngine", StringProp(boxColliderDump, "fullType"));
                Assert.IsTrue(boxColliderDump.TryGetProperty("enabled", out var enabledProp) && enabledProp.Kind == JsonValueKind.Boolean);
                Assert.IsFalse(enabledProp.AsBoolean(), "collider.enabled was set to false");

                Assert.IsTrue(boxColliderDump.TryGetProperty("properties", out var properties)
                    && properties.Kind == JsonValueKind.Array && properties.Items.Count > 0);

                JsonValue isTriggerProp = null;
                foreach (var property in properties.Items)
                    if (StringProp(property, "name") == "m_IsTrigger") isTriggerProp = property;

                Assert.IsNotNull(isTriggerProp, "expected an 'm_IsTrigger' property entry - same SerializedObject iteration component_get_property reads");
                Assert.AreEqual("Boolean", StringProp(isTriggerProp, "type"));
                Assert.IsTrue(isTriggerProp.TryGetProperty("value", out var v) && v.Kind == JsonValueKind.Boolean && v.AsBoolean(),
                    "structured value, not a stringified one - same SerializedPropertyJson.Get every component mutation already uses");

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void InspectGameObject_UnknownPath_ThrowsActionableError()
        {
            new GameObject("ExistingRoot");
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Ghost"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("inspector.inspect", @params)));

            StringAssert.Contains("Ghost", ex.Message);
            StringAssert.Contains("ExistingRoot", ex.Message);
        }

        [Test]
        public void InspectGameObject_IndependentOfSelection_DoesNotRequireOrChangeIt()
        {
            var selected = new GameObject("Selected");
            var other = new GameObject("Other");
            Selection.activeGameObject = selected;

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Other"));
                var result = CommandTable.Dispatch(gate, Request("inspector.inspect", @params));

                Assert.AreEqual("Other", StringProp(result, "name"));
                Assert.AreSame(selected, Selection.activeGameObject, "inspecting a GameObject must not change the Editor's own selection");

                AssertNeverTouchedLease(fake, gate);
            }
        }
    }
}
