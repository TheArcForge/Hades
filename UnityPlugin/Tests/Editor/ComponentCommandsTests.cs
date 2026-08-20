// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// The seven remaining class-1 mutations this task covers: component add/remove/set_property/
    /// set_properties, plus the wiring trio reference.set / event.add_listener / event.remove_listener
    /// - grouped with component mutations because they are fine-grained SerializedProperty writes
    /// on an existing component, the same shape as component.set_property. See SceneCommandsTests'
    /// own doc comment for why every test proves result + Undo-revert + never-touched-lease
    /// together, and for why <see cref="SceneTestFixtures.ResetScene"/> is reused from there rather
    /// than duplicated.
    /// </summary>
    [TestFixture]
    public sealed class ComponentCommandsTests
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
            Assert.AreEqual(0, fake.LockCalls, "a class-1 component mutation must never call Lock");
            Assert.AreEqual(0, fake.UnlockCalls, "a class-1 component mutation must never call Unlock");
            Assert.IsFalse(gate.IsHeld, "a class-1 component mutation must never leave a lease held");
        }

        static string StringProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.String ? v.AsString() : null;

        static long IntProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.Integer ? v.AsInteger() : long.MinValue;

        /// <summary>Just asserts the field is present as a JSON integer - NOT that it is nonzero.
        /// See SceneCommandsTests.AssertHasFileId's doc comment: Unsupported.GetLocalIdentifierInFile
        /// only reports a nonzero id for an object that was deserialized from an on-disk file, not
        /// merely one that lives in a scene which has since been saved - a distinction no test in
        /// this file (all same-session, never-reloaded) can satisfy.</summary>
        static void AssertHasFileId(JsonValue result) =>
            Assert.IsTrue(result.TryGetProperty("fileId", out var v) && v.Kind == JsonValueKind.Integer, "result must report a 'fileId'");

        // ---------------------------------------------------------------- component.add

        [Test]
        public void AddComponent_AttachesTypeAndReportsFileId_NoLeaseTouched()
        {
            new GameObject("Target");
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("Target"))
                    .SetProperty("componentType", JsonValue.String("Rigidbody"));
                var result = CommandTable.Dispatch(gate, Request("component.add", @params));

                Assert.AreEqual("Rigidbody", StringProp(result, "added"));
                AssertHasFileId(result); // 0 is legitimate here - see AssertHasFileId's doc comment
                Assert.IsNotNull(GameObject.Find("Target").GetComponent<Rigidbody>());

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void AddComponent_UnknownGameObject_ThrowsActionableError()
        {
            new GameObject("ExistingRoot");
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Ghost"))
                .SetProperty("componentType", JsonValue.String("Rigidbody"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("component.add", @params)));

            StringAssert.Contains("Ghost", ex.Message);
            StringAssert.Contains("ExistingRoot", ex.Message);
        }

        [Test]
        public void AddComponent_UnknownType_ThrowsActionableError()
        {
            new GameObject("Target");
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Target"))
                .SetProperty("componentType", JsonValue.String("NoSuchComponent"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("component.add", @params)));

            StringAssert.Contains("NoSuchComponent", ex.Message);
        }

        [Test]
        public void AddComponent_RegistersUndo_PerformUndoRemovesIt()
        {
            new GameObject("Target");
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Target"))
                .SetProperty("componentType", JsonValue.String("Rigidbody"));
            CommandTable.Dispatch(gate, Request("component.add", @params));
            Assert.IsNotNull(GameObject.Find("Target").GetComponent<Rigidbody>());

            Undo.PerformUndo();

            Assert.IsNull(GameObject.Find("Target").GetComponent<Rigidbody>());
        }

        // ---------------------------------------------------------------- component.remove

        [Test]
        public void RemoveComponent_DetachesItAndReportsFileId_NoLeaseTouched()
        {
            var go = new GameObject("Target");
            go.AddComponent<Rigidbody>();
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("Target"))
                    .SetProperty("componentType", JsonValue.String("Rigidbody"));
                var result = CommandTable.Dispatch(gate, Request("component.remove", @params));

                Assert.AreEqual("Rigidbody", StringProp(result, "removed"));
                AssertHasFileId(result); // 0 is legitimate here - see AssertHasFileId's doc comment
                Assert.IsNull(go.GetComponent<Rigidbody>());

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void RemoveComponent_NotPresent_ThrowsActionableErrorListingExisting()
        {
            new GameObject("Target");
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Target"))
                .SetProperty("componentType", JsonValue.String("Rigidbody"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("component.remove", @params)));

            StringAssert.Contains("Rigidbody", ex.Message);
            StringAssert.Contains("Transform", ex.Message); // every GameObject has one - proves the listing is real
        }

        [Test]
        public void RemoveComponent_RegistersUndo_PerformUndoRestoresIt()
        {
            var go = new GameObject("Target");
            go.AddComponent<Rigidbody>();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Target"))
                .SetProperty("componentType", JsonValue.String("Rigidbody"));
            CommandTable.Dispatch(gate, Request("component.remove", @params));
            Assert.IsNull(go.GetComponent<Rigidbody>());

            Undo.PerformUndo();

            Assert.IsNotNull(go.GetComponent<Rigidbody>());
        }

        // ---------------------------------------------------------------- component.set_property

        [Test]
        public void SetProperty_Float_AppliesValue_NoLeaseTouched()
        {
            var go = new GameObject("Target");
            var rb = go.AddComponent<Rigidbody>();
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("Target"))
                    .SetProperty("componentType", JsonValue.String("Rigidbody"))
                    .SetProperty("propertyName", JsonValue.String("m_Mass"))
                    .SetProperty("value", JsonValue.Float(12.5));
                var result = CommandTable.Dispatch(gate, Request("component.set_property", @params));

                Assert.AreEqual("m_Mass", StringProp(result, "property"));
                Assert.AreEqual(12.5f, rb.mass);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void SetProperty_Vector3ByDisplayName_ResolvesFuzzyAndApplies()
        {
            var go = new GameObject("Target");
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var vector = JsonValue.NewObject().SetProperty("x", JsonValue.Float(1)).SetProperty("y", JsonValue.Float(2)).SetProperty("z", JsonValue.Float(3));
            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Target"))
                .SetProperty("componentType", JsonValue.String("Transform"))
                .SetProperty("propertyName", JsonValue.String("Local Position")) // display name, not m_LocalPosition
                .SetProperty("value", vector);
            CommandTable.Dispatch(gate, Request("component.set_property", @params));

            Assert.AreEqual(new Vector3(1, 2, 3), go.transform.localPosition);
        }

        [Test]
        public void SetProperty_UnknownProperty_ThrowsActionableErrorListingValidOnes()
        {
            var go = new GameObject("Target");
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Target"))
                .SetProperty("componentType", JsonValue.String("Transform"))
                .SetProperty("propertyName", JsonValue.String("NoSuchProperty"))
                .SetProperty("value", JsonValue.Float(1));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("component.set_property", @params)));

            StringAssert.Contains("NoSuchProperty", ex.Message);
            StringAssert.Contains("m_LocalPosition", ex.Message);
        }

        [Test]
        public void SetProperty_RegistersUndo_PerformUndoRestoresPriorValue()
        {
            var go = new GameObject("Target");
            var rb = go.AddComponent<Rigidbody>();
            var originalMass = rb.mass;
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Target"))
                .SetProperty("componentType", JsonValue.String("Rigidbody"))
                .SetProperty("propertyName", JsonValue.String("m_Mass"))
                .SetProperty("value", JsonValue.Float(999));
            CommandTable.Dispatch(gate, Request("component.set_property", @params));
            Assert.AreEqual(999f, rb.mass);

            Undo.PerformUndo();

            Assert.AreEqual(originalMass, rb.mass);
        }

        // ---------------------------------------------------------------- component.set_properties

        [Test]
        public void SetProperties_BatchAcrossOperations_ReportsAppliedAndFailedPerOperation_NoLeaseTouched()
        {
            var a = new GameObject("A");
            a.AddComponent<Rigidbody>();
            var b = new GameObject("B");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var opA = JsonValue.NewObject()
                    .SetProperty("gameObject", JsonValue.String("A"))
                    .SetProperty("component", JsonValue.String("Rigidbody"))
                    .SetProperty("properties", JsonValue.NewObject()
                        .SetProperty("m_Mass", JsonValue.Float(3))
                        .SetProperty("NoSuchProperty", JsonValue.Float(1)));

                var opB = JsonValue.NewObject()
                    .SetProperty("gameObject", JsonValue.String("B"))
                    .SetProperty("component", JsonValue.String("Transform"))
                    .SetProperty("properties", JsonValue.NewObject().SetProperty("m_LocalScale",
                        JsonValue.NewObject().SetProperty("x", JsonValue.Float(2)).SetProperty("y", JsonValue.Float(2)).SetProperty("z", JsonValue.Float(2))));

                var @params = JsonValue.NewObject().SetProperty("operations", JsonValue.NewArray().Add(opA).Add(opB));
                var result = CommandTable.Dispatch(gate, Request("component.set_properties", @params));

                Assert.IsTrue(result.TryGetProperty("results", out var results));
                Assert.AreEqual(2, results.Items.Count);

                var resultA = results.Items[0];
                Assert.IsTrue(resultA.TryGetProperty("applied", out var appliedA));
                Assert.AreEqual(1, appliedA.Items.Count);
                Assert.IsTrue(resultA.TryGetProperty("failed", out var failedA));
                Assert.AreEqual(1, failedA.Items.Count);

                Assert.AreEqual(3f, a.GetComponent<Rigidbody>().mass);
                Assert.AreEqual(new Vector3(2, 2, 2), b.transform.localScale);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void SetProperties_UnknownGameObject_RecordedAsTopLevelError_OtherOperationsStillApply()
        {
            var b = new GameObject("B");
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var opGhost = JsonValue.NewObject().SetProperty("gameObject", JsonValue.String("Ghost")).SetProperty("component", JsonValue.String("Transform"));
            var opB = JsonValue.NewObject()
                .SetProperty("gameObject", JsonValue.String("B"))
                .SetProperty("component", JsonValue.String("Transform"))
                .SetProperty("properties", JsonValue.NewObject().SetProperty("m_LocalScale",
                    JsonValue.NewObject().SetProperty("x", JsonValue.Float(3)).SetProperty("y", JsonValue.Float(3)).SetProperty("z", JsonValue.Float(3))));

            var @params = JsonValue.NewObject().SetProperty("operations", JsonValue.NewArray().Add(opGhost).Add(opB));
            var result = CommandTable.Dispatch(gate, Request("component.set_properties", @params));

            Assert.IsTrue(result.TryGetProperty("errors", out var errors));
            Assert.AreEqual(1, errors.Items.Count);
            Assert.AreEqual(new Vector3(3, 3, 3), b.transform.localScale);
        }

        [Test]
        public void SetProperties_RegistersUndoAsOneGroup_PerformUndoRestoresBoth()
        {
            var a = new GameObject("A");
            a.AddComponent<Rigidbody>();
            var originalMass = a.GetComponent<Rigidbody>().mass;
            var b = new GameObject("B");
            var originalScale = b.transform.localScale;

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var opA = JsonValue.NewObject().SetProperty("gameObject", JsonValue.String("A")).SetProperty("component", JsonValue.String("Rigidbody"))
                .SetProperty("properties", JsonValue.NewObject().SetProperty("m_Mass", JsonValue.Float(50)));
            var opB = JsonValue.NewObject().SetProperty("gameObject", JsonValue.String("B")).SetProperty("component", JsonValue.String("Transform"))
                .SetProperty("properties", JsonValue.NewObject().SetProperty("m_LocalScale",
                    JsonValue.NewObject().SetProperty("x", JsonValue.Float(9)).SetProperty("y", JsonValue.Float(9)).SetProperty("z", JsonValue.Float(9))));
            var @params = JsonValue.NewObject().SetProperty("operations", JsonValue.NewArray().Add(opA).Add(opB));
            CommandTable.Dispatch(gate, Request("component.set_properties", @params));
            Assert.AreEqual(50f, a.GetComponent<Rigidbody>().mass);

            Undo.PerformUndo();

            Assert.AreEqual(originalMass, a.GetComponent<Rigidbody>().mass);
            Assert.AreEqual(originalScale, b.transform.localScale);
        }

        // ---------------------------------------------------------------- reference.set

        [Test]
        public void ReferenceSet_ScenePathTarget_WiresComponentReference_NoLeaseTouched()
        {
            var source = new GameObject("Source");
            var button = source.AddComponent<Button>(); // Button.targetGraphic is a public ObjectReference field on Selectable
            var imageGo = new GameObject("TargetImage");
            var image = imageGo.AddComponent<UnityEngine.UI.Image>(); // Image implements Graphic - a type-correct target

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("Source"))
                    .SetProperty("componentType", JsonValue.String("Button"))
                    .SetProperty("propertyName", JsonValue.String("m_TargetGraphic"))
                    .SetProperty("targetPath", JsonValue.String("TargetImage"))
                    .SetProperty("targetComponentType", JsonValue.String("Image"));

                var result = CommandTable.Dispatch(gate, Request("reference.set", @params));

                Assert.AreEqual("Image", StringProp(result, "targetType"));
                Assert.AreSame(image, button.targetGraphic);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void ReferenceSet_TypeMismatch_ThrowsActionableError()
        {
            var source = new GameObject("Source");
            source.AddComponent<Button>();
            var canvasGo = new GameObject("TargetCanvas");
            canvasGo.AddComponent<Canvas>(); // Canvas is not a Graphic - deliberate mismatch

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Source"))
                .SetProperty("componentType", JsonValue.String("Button"))
                .SetProperty("propertyName", JsonValue.String("m_TargetGraphic"))
                .SetProperty("targetPath", JsonValue.String("TargetCanvas"))
                .SetProperty("targetComponentType", JsonValue.String("Canvas"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("reference.set", @params)));

            StringAssert.Contains("Canvas", ex.Message);
        }

        [Test]
        public void ReferenceSet_NeitherTargetProvided_ThrowsActionableError()
        {
            var source = new GameObject("Source");
            source.AddComponent<Button>();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Source"))
                .SetProperty("componentType", JsonValue.String("Button"))
                .SetProperty("propertyName", JsonValue.String("m_TargetGraphic"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("reference.set", @params)));

            StringAssert.Contains("targetPath", ex.Message);
            StringAssert.Contains("targetAssetPath", ex.Message);
        }

        [Test]
        public void ReferenceSet_RegistersUndo_PerformUndoClearsReference()
        {
            var source = new GameObject("Source");
            var button = source.AddComponent<Button>();
            var imageGo = new GameObject("TargetImage");
            imageGo.AddComponent<UnityEngine.UI.Image>();

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Source"))
                .SetProperty("componentType", JsonValue.String("Button"))
                .SetProperty("propertyName", JsonValue.String("m_TargetGraphic"))
                .SetProperty("targetPath", JsonValue.String("TargetImage"))
                .SetProperty("targetComponentType", JsonValue.String("Image"));
            CommandTable.Dispatch(gate, Request("reference.set", @params));
            Assert.IsNotNull(button.targetGraphic);

            Undo.PerformUndo();

            Assert.IsNull(button.targetGraphic);
        }

        // ---------------------------------------------------------------- event.add_listener / event.remove_listener

        [Test]
        public void EventAddListener_VoidMethod_WiresPersistentListener_NoLeaseTouched()
        {
            var source = new GameObject("Source");
            var button = source.AddComponent<Button>();
            var target = new GameObject("Target");
            target.AddComponent<ListenerTarget>();

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("Source"))
                    .SetProperty("componentType", JsonValue.String("Button"))
                    .SetProperty("eventName", JsonValue.String("m_OnClick"))
                    .SetProperty("targetPath", JsonValue.String("Target"))
                    .SetProperty("targetMethod", JsonValue.String("DoThing"));
                var result = CommandTable.Dispatch(gate, Request("event.add_listener", @params));

                Assert.AreEqual(1L, IntProp(result, "listenerCount"));
                Assert.AreEqual(1, button.onClick.GetPersistentEventCount());

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void EventAddListener_UnknownEventField_ThrowsActionableError()
        {
            var source = new GameObject("Source");
            source.AddComponent<Button>();
            var target = new GameObject("Target");
            target.AddComponent<ListenerTarget>();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Source"))
                .SetProperty("componentType", JsonValue.String("Button"))
                .SetProperty("eventName", JsonValue.String("m_OnNope"))
                .SetProperty("targetPath", JsonValue.String("Target"))
                .SetProperty("targetMethod", JsonValue.String("DoThing"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("event.add_listener", @params)));

            StringAssert.Contains("m_OnNope", ex.Message);
            StringAssert.Contains("m_OnClick", ex.Message);
        }

        [Test]
        public void EventAddListener_RegistersUndo_PerformUndoRemovesListener()
        {
            var source = new GameObject("Source");
            var button = source.AddComponent<Button>();
            var target = new GameObject("Target");
            target.AddComponent<ListenerTarget>();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Source"))
                .SetProperty("componentType", JsonValue.String("Button"))
                .SetProperty("eventName", JsonValue.String("m_OnClick"))
                .SetProperty("targetPath", JsonValue.String("Target"))
                .SetProperty("targetMethod", JsonValue.String("DoThing"));
            CommandTable.Dispatch(gate, Request("event.add_listener", @params));
            Assert.AreEqual(1, button.onClick.GetPersistentEventCount());

            Undo.PerformUndo();

            Assert.AreEqual(0, button.onClick.GetPersistentEventCount());
        }

        [Test]
        public void EventRemoveListener_RemovesByIndex_NoLeaseTouched()
        {
            var source = new GameObject("Source");
            var button = source.AddComponent<Button>();
            var target = new GameObject("Target");
            target.AddComponent<ListenerTarget>();
            UnityEditor.Events.UnityEventTools.AddVoidPersistentListener(button.onClick, target.GetComponent<ListenerTarget>().DoThing);
            Assert.AreEqual(1, button.onClick.GetPersistentEventCount());

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("Source"))
                    .SetProperty("componentType", JsonValue.String("Button"))
                    .SetProperty("eventName", JsonValue.String("m_OnClick"))
                    .SetProperty("index", JsonValue.Integer(0));
                var result = CommandTable.Dispatch(gate, Request("event.remove_listener", @params));

                Assert.AreEqual(0L, IntProp(result, "remainingListeners"));
                Assert.AreEqual(0, button.onClick.GetPersistentEventCount());

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void EventRemoveListener_IndexOutOfRange_ThrowsActionableError()
        {
            var source = new GameObject("Source");
            source.AddComponent<Button>();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Source"))
                .SetProperty("componentType", JsonValue.String("Button"))
                .SetProperty("eventName", JsonValue.String("m_OnClick"))
                .SetProperty("index", JsonValue.Integer(5));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("event.remove_listener", @params)));

            StringAssert.Contains("5", ex.Message);
            StringAssert.Contains("0", ex.Message);
        }

        [Test]
        public void EventRemoveListener_RegistersUndo_PerformUndoRestoresListener()
        {
            var source = new GameObject("Source");
            var button = source.AddComponent<Button>();
            var target = new GameObject("Target");
            target.AddComponent<ListenerTarget>();
            UnityEditor.Events.UnityEventTools.AddVoidPersistentListener(button.onClick, target.GetComponent<ListenerTarget>().DoThing);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Source"))
                .SetProperty("componentType", JsonValue.String("Button"))
                .SetProperty("eventName", JsonValue.String("m_OnClick"))
                .SetProperty("index", JsonValue.Integer(0));
            CommandTable.Dispatch(gate, Request("event.remove_listener", @params));
            Assert.AreEqual(0, button.onClick.GetPersistentEventCount());

            Undo.PerformUndo();

            Assert.AreEqual(1, button.onClick.GetPersistentEventCount());
        }

        /// <summary>Listener-method host for event.add_listener / event.remove_listener tests -
        /// needs at least one public void, zero-arg method a persistent UnityEvent call can bind to.</summary>
        sealed class ListenerTarget : MonoBehaviour
        {
            public void DoThing() { }
        }
    }
}
