// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;
using UnityEditor;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// The three class-1 tag/layer mutations, dispatched through <see cref="CommandTable.Dispatch"/>
    /// the same way every other class-1 suite does. Unlike scene/component/material mutations,
    /// these write ProjectSettings/TagManager.asset - a project-level singleton, not scene-local
    /// state a fresh scene reset would isolate - so every test cleans up its OWN tag/layer names
    /// directly (bypassing the tools under test) in SetUp AND TearDown, so a failing assertion
    /// mid-test can never leak a stray tag/layer into the next test or leave the scratch project
    /// permanently altered.
    ///
    /// No PerformUndo-revert test here, deliberately - see TagLayerCommands' own class doc comment:
    /// this plan's own guidance calls out tag.create/tag.delete/layer.create as project-settings
    /// mutations whose Undo semantics were not assumed to match a scene GameObject's.
    /// </summary>
    [TestFixture]
    public sealed class TagLayerCommandsTests
    {
        const string TestTag = "HadesTestTag";
        const string TestTagAlt = "HadesTestTagAlt";
        const string TestLayerName = "HadesTestLayer";

        // Slots any test in this file might occupy, directly or via auto-assign - cleared
        // unconditionally in SetUp/TearDown regardless of which test ran or whether it passed.
        static readonly int[] ScratchLayerSlots = { 8, 9, 30, 31 };

        [SetUp]
        public void SetUp()
        {
            Undo.ClearAll();
            RemoveTagDirect(TestTag);
            RemoveTagDirect(TestTagAlt);
            foreach (var slot in ScratchLayerSlots) SetLayerSlotDirect(slot, "");
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            RemoveTagDirect(TestTag);
            RemoveTagDirect(TestTagAlt);
            foreach (var slot in ScratchLayerSlots) SetLayerSlotDirect(slot, "");
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
            Assert.AreEqual(0, fake.LockCalls, "a class-1 tag/layer mutation must never call Lock");
            Assert.AreEqual(0, fake.UnlockCalls, "a class-1 tag/layer mutation must never call Unlock");
            Assert.IsFalse(gate.IsHeld, "a class-1 tag/layer mutation must never leave a lease held");
        }

        static string StringProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.String ? v.AsString() : null;

        static long IntProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.Integer ? v.AsInteger() : long.MinValue;

        /// <summary>Dispatches once through a fresh, throwaway gate - for building fixture state
        /// (e.g. an existing tag before testing tag.delete) via the SAME code path a real caller
        /// would use, without that setup call polluting the assertions a test makes about its own
        /// no-lease behaviour.</summary>
        static void DispatchSetup(string method, JsonValue @params)
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            CommandTable.Dispatch(gate, Request(method, @params));
        }

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

        // ---------------------------------------------------------------------------------- tag.create

        [Test]
        public void CreateTag_AddsNewTag_NoLeaseTouched()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("name", JsonValue.String(TestTag));
                var result = CommandTable.Dispatch(gate, Request("tag.create", @params));

                Assert.AreEqual(TestTag, StringProp(result, "created"));
                Assert.IsTrue(TagExists(TestTag));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void CreateTag_BuiltIn_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("name", JsonValue.String("Player"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("tag.create", @params)));

            StringAssert.Contains("built-in", ex.Message);
        }

        [Test]
        public void CreateTag_AlreadyExists_ThrowsActionableError()
        {
            DispatchSetup("tag.create", JsonValue.NewObject().SetProperty("name", JsonValue.String(TestTag)));

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("name", JsonValue.String(TestTag));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("tag.create", @params)));

            StringAssert.Contains("already exists", ex.Message);
        }

        // ---------------------------------------------------------------------------------- tag.delete

        [Test]
        public void DeleteTag_RemovesExistingTag_NoLeaseTouched()
        {
            DispatchSetup("tag.create", JsonValue.NewObject().SetProperty("name", JsonValue.String(TestTag)));

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("name", JsonValue.String(TestTag));
                var result = CommandTable.Dispatch(gate, Request("tag.delete", @params));

                Assert.AreEqual(TestTag, StringProp(result, "deleted"));
                Assert.IsFalse(TagExists(TestTag));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void DeleteTag_BuiltIn_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("name", JsonValue.String("MainCamera"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("tag.delete", @params)));

            StringAssert.Contains("built-in", ex.Message);
        }

        [Test]
        public void DeleteTag_NotFound_ThrowsActionableErrorListingCustomTags()
        {
            DispatchSetup("tag.create", JsonValue.NewObject().SetProperty("name", JsonValue.String(TestTag)));

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("name", JsonValue.String("NoSuchTag"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("tag.delete", @params)));

            StringAssert.Contains("NoSuchTag", ex.Message);
            StringAssert.Contains(TestTag, ex.Message);
        }

        // -------------------------------------------------------------------------------- layer.create

        [Test]
        public void CreateLayer_AutoAssignsFirstEmptySlot_SkippingOccupiedOnes_NoLeaseTouched()
        {
            // Slot 8 occupied directly, bypassing the tool under test, so auto-assign must skip it
            // and land on 9 - "index 8 is free" and "layers end at 7" are NOT the same fact, which
            // is exactly what this test must catch if the implementation assumed the latter.
            SetLayerSlotDirect(8, "PreOccupied");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("name", JsonValue.String(TestLayerName));
                var result = CommandTable.Dispatch(gate, Request("layer.create", @params));

                Assert.AreEqual(TestLayerName, StringProp(result, "created"));
                Assert.AreEqual(9L, IntProp(result, "index"));
                Assert.AreEqual(TestLayerName, LayerNameAt(9));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void CreateLayer_ExplicitIndex_AssignsThatSlot()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("name", JsonValue.String(TestLayerName)).SetProperty("layerIndex", JsonValue.Integer(30));
            var result = CommandTable.Dispatch(gate, Request("layer.create", @params));

            Assert.AreEqual(30L, IntProp(result, "index"));
            Assert.AreEqual(TestLayerName, LayerNameAt(30));
        }

        [Test]
        public void CreateLayer_ExplicitIndexOccupied_ThrowsActionableErrorNamingOccupant()
        {
            SetLayerSlotDirect(30, "AlreadyThere");

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("name", JsonValue.String(TestLayerName)).SetProperty("layerIndex", JsonValue.Integer(30));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("layer.create", @params)));

            StringAssert.Contains("AlreadyThere", ex.Message);
        }

        [Test]
        public void CreateLayer_ExplicitIndexBelow8_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("name", JsonValue.String(TestLayerName)).SetProperty("layerIndex", JsonValue.Integer(3));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("layer.create", @params)));

            StringAssert.Contains("8-31", ex.Message);
        }

        [Test]
        public void CreateLayer_AllSlotsOccupied_ThrowsActionableError()
        {
            for (var i = 8; i < 32; i++) SetLayerSlotDirect(i, "Occupied" + i);
            try
            {
                using var pump = new MainThreadPump();
                using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

                var @params = JsonValue.NewObject().SetProperty("name", JsonValue.String(TestLayerName));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("layer.create", @params)));

                StringAssert.Contains("8-31", ex.Message);
            }
            finally
            {
                for (var i = 8; i < 32; i++) SetLayerSlotDirect(i, "");
            }
        }
    }
}
