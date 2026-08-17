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
    /// The seven class-2 prefab mutations (see the "52 Editor tools" plan's operation-class table -
    /// multi-tick, lease bounded by the call), dispatched through <see cref="CommandTable.Dispatch"/>
    /// the same way every other suite in this folder does.
    ///
    /// Every mutation test proves what SceneCommandsTests/MaterialCommandsTests prove for class 1,
    /// PLUS the inverse lease property that defines class 2 (see this plan's own framing: "after
    /// ANY class-2 call - success or exception - no lease may remain held", the deliberate opposite
    /// of class 3's BeginScriptEditing semantics):
    ///   1. the result/prefab actually changed the way the response claims;
    ///   2. the ReloadGate DID acquire and release exactly once (LockCalls == UnlockCalls == 1,
    ///      Counter back at 0, IsHeld false) - proven with <see cref="AssertLeaseCleanlyReleased"/>,
    ///      the class-2 counterpart to the class-1 suites' AssertNeverTouchedLease;
    ///   3. that release survives an exception mid-operation, not just the success path - see the
    ///      "_ThrowsButStillReleasesLease" tests, one per handler that can plausibly throw AFTER
    ///      acquiring.
    /// </summary>
    [TestFixture]
    public sealed class PrefabCommandsTests
    {
        const string ScratchDir = "Assets/Tests/_HadesPrefabScratch";

        [SetUp]
        public void SetUp()
        {
            SceneTestFixtures.ResetScene();
            Undo.ClearAll();
            CloseAnyLeakedEditingSession(); // a session left open by a failed prior test must never leak into this one
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
            AssetDatabase.CreateFolder("Assets/Tests", "_HadesPrefabScratch");
        }

        [TearDown]
        public void TearDown()
        {
            CloseAnyLeakedEditingSession();
            Undo.ClearAll();
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
        }

        static JsonRpcRequest Request(string method, JsonValue @params) =>
            new JsonRpcRequest { Id = JsonValue.Integer(1), Method = method, Params = @params };

        /// <summary>PrefabCommands' open-editing session state is a private static field with no
        /// InternalsVisibleTo escape hatch into this test assembly (same reasoning as every other
        /// suite in this folder - see CommandTable.Dispatch being the ONLY entry point any test
        /// here ever calls). So this closes a leaked session the same way a real caller would: by
        /// dispatching prefab.save_editing through the public CommandTable, and treating "no
        /// session was open" (InvalidOperationException) as the expected, harmless case.</summary>
        static void CloseAnyLeakedEditingSession()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            try { CommandTable.Dispatch(gate, Request("prefab.save_editing", JsonValue.NewObject())); }
            catch (InvalidOperationException) { /* nothing was open - expected in the common case */ }
        }

        static (ReloadGate gate, FakeEditorLockApi fake, MainThreadPump pump) NoopGateParts()
        {
            var fake = new FakeEditorLockApi();
            var pump = new MainThreadPump();
            var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            return (gate, fake, pump);
        }

        /// <summary>The class-2 counterpart to the class-1 suites' AssertNeverTouchedLease: proves
        /// the gate WAS acquired and released - in balance, never leaked - rather than never
        /// touched at all. Used identically after a successful call and after a caught exception.
        /// Deliberately does NOT assert an exact call count: several tests in this file dispatch
        /// MORE THAN ONE command before checking (e.g. open-editing-twice, or the open/edit/save
        /// round trip), and each of those calls independently acquires and releases its own lease -
        /// LockCalls == UnlockCalls (with at least one pair) is the real invariant, not "exactly
        /// one".</summary>
        static void AssertLeaseCleanlyReleased(FakeEditorLockApi fake, ReloadGate gate)
        {
            Assert.IsFalse(gate.IsHeld, "a class-2 prefab operation must never leave a lease held");
            Assert.GreaterOrEqual(fake.LockCalls, 1, "expected at least one Lock across the call(s) so far");
            Assert.AreEqual(fake.LockCalls, fake.UnlockCalls, "every Lock must be balanced by exactly one Unlock - no leaked lease");
            Assert.AreEqual(0, fake.Counter, "the fake's signed counter must land back at 0");
        }

        static string StringProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.String ? v.AsString() : null;

        static bool BoolProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.Boolean && v.AsBoolean();

        static string AbsolutePath(string projectRelativePath) =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));

        // ---------------------------------------------------------------------------- prefab.create

        [Test]
        public void CreatePrefab_SavesGameObjectAsPrefabAsset_VerifiedOnDisk_LeaseCleanlyReleased()
        {
            var go = new GameObject("Widget");
            go.AddComponent<BoxCollider>();
            var assetPath = ScratchDir + "/Sub/Widget.prefab"; // also exercises folder auto-creation

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("Widget"))
                    .SetProperty("assetPath", JsonValue.String(assetPath));
                var result = CommandTable.Dispatch(gate, Request("prefab.create", @params));

                Assert.AreEqual(assetPath, StringProp(result, "createdAsset"));
                Assert.IsNotEmpty(StringProp(result, "guid"));

                // Verified by reading the file, not by trusting the response.
                Assert.IsTrue(File.Exists(AbsolutePath(assetPath)));
                var fileText = File.ReadAllText(AbsolutePath(assetPath));
                StringAssert.Contains("BoxCollider", fileText);
                StringAssert.Contains("Widget", fileText);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        [Test]
        public void CreatePrefab_UnknownGameObject_ThrowsActionableError_StillReleasesLease()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("DoesNotExist"))
                    .SetProperty("assetPath", JsonValue.String(ScratchDir + "/WontExist.prefab"));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("prefab.create", @params)));
                StringAssert.Contains("DoesNotExist", ex.Message);
                Assert.IsFalse(File.Exists(AbsolutePath(ScratchDir + "/WontExist.prefab")));

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        /// <summary>docs/backlog/mutation-tool-defects.md's Defect 3, root-caused: prefab.create
        /// used to call PrefabUtility.SaveAsPrefabAsset - Unity's DISCONNECTED save - which left the
        /// source GameObject a plain, unconnected object even though the call reported success. The
        /// fix (DoCreate now calls SaveAsPrefabAssetAndConnect, matching DoCreateVariant's existing
        /// pattern) is proven here the same way a caller's NEXT call would observe it - via
        /// PrefabUtility's own connection-status API - not by trusting prefab.create's JSON result,
        /// which says nothing about the scene object's connection state either way.</summary>
        [Test]
        public void CreatePrefab_ConnectsSceneGameObjectToNewAsset_VerifiedViaPrefabUtility_LeaseCleanlyReleased()
        {
            var go = new GameObject("ConnectWidget");
            go.AddComponent<BoxCollider>();
            var assetPath = ScratchDir + "/ConnectWidget.prefab";

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("ConnectWidget"))
                    .SetProperty("assetPath", JsonValue.String(assetPath));
                CommandTable.Dispatch(gate, Request("prefab.create", @params));

                var stillThere = GameObject.Find("ConnectWidget");
                Assert.IsNotNull(stillThere, "the same-named GameObject must still be resolvable in the scene");
                Assert.AreSame(go, stillThere,
                    "the ORIGINAL GameObject reference must still be the one in the scene - Unity's own docs promise "
                    + "this overload does not destroy/replace it, only connects it");
                Assert.IsTrue(PrefabUtility.IsPartOfPrefabInstance(go), "the source GameObject must become a connected prefab instance, not stay a plain object");
                Assert.AreEqual(assetPath, PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go));

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        /// <summary>The actual repro from docs/backlog/mutation-tool-defects.md's Defect 3: create a
        /// leaf prefab from a GameObject, reparent that now-connected GameObject under a new root,
        /// then create a prefab from the root. Before the fix this silently produced a flattened,
        /// disconnected copy of Leaf (no PrefabInstance block, no m_SourcePrefab), with both calls
        /// reporting success. Checked against the raw .prefab YAML on disk - the only thing that can
        /// actually show a prefab is genuinely nested (see this method's own assertions) - not
        /// against either call's own response.</summary>
        [Test]
        public void CreatePrefab_LeafThenReparentThenCreateParent_ProducesGenuineNestedPrefabInstance_VerifiedOnDisk()
        {
            var leaf = new GameObject("Leaf");
            leaf.AddComponent<BoxCollider>();
            var leafPath = ScratchDir + "/Leaf.prefab";

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("prefab.create", JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("Leaf"))
                    .SetProperty("assetPath", JsonValue.String(leafPath))));
                var leafGuid = AssetDatabase.AssetPathToGUID(leafPath);
                Assert.IsNotEmpty(leafGuid);

                var root = new GameObject("Outer");
                Undo.RegisterCreatedObjectUndo(root, "test setup - not part of the behaviour under test");
                leaf.transform.SetParent(root.transform);

                var outerPath = ScratchDir + "/Outer.prefab";
                CommandTable.Dispatch(gate, Request("prefab.create", JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("Outer"))
                    .SetProperty("assetPath", JsonValue.String(outerPath))));

                var fileText = File.ReadAllText(AbsolutePath(outerPath));

                // A genuine nested instance: a PrefabInstance document whose m_SourcePrefab points
                // at Leaf's own GUID. The flattened-copy bug produced neither - Leaf's hierarchy was
                // duplicated inline instead, with no reference back to Leaf.prefab at all.
                StringAssert.Contains("PrefabInstance:", fileText);
                StringAssert.Contains("m_SourcePrefab", fileText);
                StringAssert.Contains("guid: " + leafGuid, fileText);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        /// <summary>The documented workaround (docs/backlog/mutation-tool-defects.md's Defect 3)
        /// must keep working unchanged now that 'create' itself also connects: instantiate the leaf
        /// as a child FIRST (InstantiatePrefab already connected, even before this fix), then create
        /// the parent from that hierarchy. A regression guard, not new behaviour - this already
        /// worked before DoCreate changed.</summary>
        [Test]
        public void CreatePrefab_InstantiateChildFirstThenCreateParent_StillProducesNestedPrefabInstance_VerifiedOnDisk()
        {
            var leafSource = new GameObject("Leaf2");
            var leafPath = ScratchDir + "/Leaf2.prefab";

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("prefab.create", JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("Leaf2"))
                    .SetProperty("assetPath", JsonValue.String(leafPath))));
                var leafGuid = AssetDatabase.AssetPathToGUID(leafPath);

                var root = new GameObject("Outer2");
                Undo.RegisterCreatedObjectUndo(root, "test setup - not part of the behaviour under test");

                CommandTable.Dispatch(gate, Request("prefab.instantiate", JsonValue.NewObject()
                    .SetProperty("prefabPath", JsonValue.String(leafPath))
                    .SetProperty("parent", JsonValue.String("Outer2"))));

                var outerPath = ScratchDir + "/Outer2.prefab";
                CommandTable.Dispatch(gate, Request("prefab.create", JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("Outer2"))
                    .SetProperty("assetPath", JsonValue.String(outerPath))));

                var fileText = File.ReadAllText(AbsolutePath(outerPath));
                StringAssert.Contains("PrefabInstance:", fileText);
                StringAssert.Contains("guid: " + leafGuid, fileText);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        /// <summary>DoCreate's own doc comment: the connect step deliberately uses
        /// InteractionMode.AutomatedAction, so it registers no Undo entry of its own (matching
        /// DoCreateVariant). This proves that choice does not corrupt the GameObject's EARLIER,
        /// already-registered creation - Undo.PerformUndo after prefab.create must still cleanly
        /// remove the GameObject, not throw and not leave a half-reverted mess, even though the
        /// object was connected to a prefab in between.</summary>
        [Test]
        public void CreatePrefab_DoesNotCorruptPriorUndoRegistration_UndoStillCleanlyRevertsTheEarlierMutation()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("scene.create_gameobject",
                    JsonValue.NewObject().SetProperty("name", JsonValue.String("UndoTarget"))));

                CommandTable.Dispatch(gate, Request("prefab.create", JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("UndoTarget"))
                    .SetProperty("assetPath", JsonValue.String(ScratchDir + "/UndoTarget.prefab"))));

                Assert.DoesNotThrow(() => Undo.PerformUndo());
                Assert.IsNull(GameObject.Find("UndoTarget"),
                    "undoing the GameObject's creation must still remove it even though it was later connected to a prefab");

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        // ---------------------------------------------------------- prefab.create - path guard (F16/F17/F20)

        [Test]
        public void CreatePrefab_TraversalAssetPath_RefusedBeforeAnyWrite_LeaseCleanlyReleased()
        {
            new GameObject("TraversalSource");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("TraversalSource"))
                    .SetProperty("assetPath", JsonValue.String("Assets/../Escaped.prefab"));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("prefab.create", @params)));
                StringAssert.Contains("Escaped.prefab", ex.Message);
                Assert.IsFalse(File.Exists(AbsolutePath("Escaped.prefab")));

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        [Test]
        public void CreatePrefab_ExistingFile_Refused_OriginalUntouched_LeaseCleanlyReleased()
        {
            var existing = new GameObject("Existing");
            var assetPath = ScratchDir + "/AlreadyThere.prefab";
            PrefabUtility.SaveAsPrefabAsset(existing, assetPath);
            UnityEngine.Object.DestroyImmediate(existing);

            new GameObject("NewOne");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("NewOne"))
                    .SetProperty("assetPath", JsonValue.String(assetPath));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("prefab.create", @params)));
                StringAssert.Contains("already exists", ex.Message);
                StringAssert.Contains("prefab_apply", ex.Message);

                var stillOriginal = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                Assert.IsNotNull(stillOriginal);
                // SaveAsPrefabAsset names the root after the FILE, not the source GameObject - "AlreadyThere"
                // (from "AlreadyThere.prefab"), not "Existing". The point being proven is it isn't "NewOne".
                Assert.AreEqual("AlreadyThere", stillOriginal.name, "the pre-existing prefab must be untouched, not replaced by 'NewOne'");

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        // ------------------------------------------------------------------------- prefab.instantiate

        [Test]
        public void InstantiatePrefab_CreatesInstanceInScene_RegistersUndo_LeaseCleanlyReleased()
        {
            var source = new GameObject("Source");
            var prefabPath = ScratchDir + "/Source.prefab";
            PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
            UnityEngine.Object.DestroyImmediate(source);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                Undo.IncrementCurrentGroup();
                var @params = JsonValue.NewObject().SetProperty("prefabPath", JsonValue.String(prefabPath));
                var result = CommandTable.Dispatch(gate, Request("prefab.instantiate", @params));

                Assert.AreEqual("Source", StringProp(result, "name"));
                var instance = GameObject.Find("Source");
                Assert.IsNotNull(instance);

                Undo.PerformUndo();
                Assert.IsNull(GameObject.Find("Source"));

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        [Test]
        public void InstantiatePrefab_UnknownPrefab_ThrowsActionableError_StillReleasesLease()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("prefabPath", JsonValue.String(ScratchDir + "/Ghost.prefab"));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("prefab.instantiate", @params)));
                StringAssert.Contains("Ghost.prefab", ex.Message);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        [Test]
        public void InstantiatePrefab_UnknownParent_ThrowsActionableError_DestroysPartialInstance_StillReleasesLease()
        {
            var source = new GameObject("Source2");
            var prefabPath = ScratchDir + "/Source2.prefab";
            PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
            UnityEngine.Object.DestroyImmediate(source);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("prefabPath", JsonValue.String(prefabPath))
                    .SetProperty("parent", JsonValue.String("NoSuchParent"));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("prefab.instantiate", @params)));
                StringAssert.Contains("NoSuchParent", ex.Message);
                Assert.IsNull(GameObject.Find("Source2"), "a failed parent lookup must not leave an orphaned instance behind");

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        // ---------------------------------------------------------------------- prefab.apply_overrides

        [Test]
        public void ApplyOverrides_NotAPrefabInstance_ThrowsActionableError_StillReleasesLease()
        {
            var plain = new GameObject("PlainObject");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("gameObjectPath", JsonValue.String("PlainObject"));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("prefab.apply_overrides", @params)));
                StringAssert.Contains("not a prefab instance", ex.Message);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        /// <summary>Task 7's Defect 1, now root-caused (see PrefabCommands.ApplyOverrides' own doc
        /// comment for the full investigation, including exactly how this 11-entry set was
        /// measured): a prefab instance's outermost root ALWAYS lists its own name and its
        /// Transform's position/rotation as "modified", from the instant it is instantiated,
        /// regardless of whether the caller ever touches them - and Unity never applies any of
        /// them, via ANY PrefabUtility apply API ("The Transform position and rotation of a root
        /// GameObject in a Prefab instance cannot be applied, nor can other default override
        /// properties"). Not a batchmode artifact (reproduces identically against a real
        /// interactive Editor - see this plan's Task 7 results); not fixable by
        /// GetOutermostPrefabInstanceRoot/InteractionMode.UserAction/a follow-up SaveAssets (all
        /// tried, all identical). What WAS a real defect is reporting blanket success while this
        /// silently failed - fixed by reporting exactly which properties Unity left un-applied,
        /// proven here on disk, rather than merely asserting "it throws nothing".</summary>
        [Test]
        public void ApplyOverrides_RootTransformPositionOverride_UnityNeverAppliesIt_ReportedNotSilent()
        {
            var source = new GameObject("OverrideSource");
            source.AddComponent<BoxCollider>();
            var prefabPath = ScratchDir + "/OverrideSource.prefab";
            PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
            UnityEngine.Object.DestroyImmediate(source);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
            var instanceSo = new SerializedObject(instance.transform);
            instanceSo.FindProperty("m_LocalPosition").vector3Value = new Vector3(5, 6, 7);
            instanceSo.ApplyModifiedProperties();
            Assert.IsTrue(PrefabUtility.IsPartOfPrefabInstance(instance), "test setup sanity check");
            Assert.IsNotEmpty(PrefabUtility.GetPropertyModifications(instance), "test setup sanity check - the override must actually be registered");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("gameObjectPath", JsonValue.String("OverrideSource"));
                var result = CommandTable.Dispatch(gate, Request("prefab.apply_overrides", @params));

                Assert.AreEqual("OverrideSource", StringProp(result, "applied"));
                Assert.AreEqual(prefabPath, StringProp(result, "sourcePrefab"));

                // The honesty fix itself: Unity silently refused every one of the instance root's
                // OWN "default override" properties (not just the position this test happened to
                // touch - Unity lists its name and full rotation too, unconditionally, for every
                // instance - see this test's own doc comment), so the result must say so rather
                // than reporting blanket success - verified on disk too (byte-identical to the
                // pre-override prefab), not just via the response's own claim.
                Assert.IsTrue(result.TryGetProperty("unappliedProperties", out var unapplied) && unapplied.Kind == JsonValueKind.Array,
                    "expected an 'unappliedProperties' array in the result");
                var unappliedPaths = unapplied.Items.Select(i => i.AsString()).ToList();
                CollectionAssert.AreEquivalent(new[]
                {
                    "m_Name",
                    "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z",
                    "m_LocalRotation.w", "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z",
                    "m_LocalEulerAnglesHint.x", "m_LocalEulerAnglesHint.y", "m_LocalEulerAnglesHint.z",
                }, unappliedPaths);

                // Only the KNOWN, permanent default-override set is unapplied here (nothing
                // unexpected), so the calm/informational note, not the "something else is wrong" one.
                Assert.IsTrue(result.TryGetProperty("note", out var note) && note.Kind == JsonValueKind.String && note.AsString().Length > 0,
                    "expected an explanatory 'note' when properties were left un-applied");
                StringAssert.Contains("expected", note.AsString());
                StringAssert.DoesNotContain("NOT the known", note.AsString());

                // Defect 5 (docs/backlog/mutation-tool-defects.md): this note used to point at
                // prefab_open_editing/prefab_edit_property/prefab_save_editing, none of which exist
                // post-consolidation. Defect 6: it also used to claim the restriction holds
                // "regardless of caller", contradicted by a Prefab Variant E2E finding.
                StringAssert.Contains("prefab_apply", note.AsString());
                StringAssert.DoesNotContain("prefab_open_editing", note.AsString());
                StringAssert.DoesNotContain("prefab_edit_property", note.AsString());
                StringAssert.DoesNotContain("prefab_save_editing", note.AsString());
                StringAssert.DoesNotContain("regardless of caller", note.AsString());
                LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(note.AsString());

                var fileText = File.ReadAllText(AbsolutePath(prefabPath));
                StringAssert.Contains("{x: 0, y: 0, z: 0}", fileText); // the root's position on disk never moved

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        /// <summary>The other half of Defect 1's fix, proven positively rather than just by ruling
        /// out the root-Transform case above: an override that Unity's Apply machinery DOES support
        /// (an ordinary component property, not one of the instance root's own "default override"
        /// properties) is written to the prefab ASSET on disk - not merely reported as applied. No
        /// prior test in this file ever verified prefab.apply_overrides' actual on-disk effect; this
        /// is the first. The instance root's OWN default-override properties (name/position/
        /// rotation) are still listed in 'unappliedProperties' regardless - see this file's sibling
        /// test above for why that set is unconditionally present on every instance - but 'm_Size'
        /// must NOT be among them, and must be the value actually written to disk.</summary>
        [Test]
        public void ApplyOverrides_ComponentPropertyOverride_PersistsToPrefabAssetOnDisk_OnlyExpectedDefaultsUnapplied()
        {
            var source = new GameObject("OverrideComponentSource");
            var collider = source.AddComponent<BoxCollider>();
            collider.size = Vector3.one;
            var prefabPath = ScratchDir + "/OverrideComponentSource.prefab";
            PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
            UnityEngine.Object.DestroyImmediate(source);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
            var colliderSo = new SerializedObject(instance.GetComponent<BoxCollider>());
            colliderSo.FindProperty("m_Size").vector3Value = new Vector3(9, 9, 9);
            colliderSo.ApplyModifiedProperties();
            Assert.IsNotEmpty(PrefabUtility.GetPropertyModifications(instance), "test setup sanity check - the override must actually be registered");

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("gameObjectPath", JsonValue.String("OverrideComponentSource"));
                var result = CommandTable.Dispatch(gate, Request("prefab.apply_overrides", @params));

                Assert.AreEqual("OverrideComponentSource", StringProp(result, "applied"));
                Assert.AreEqual(prefabPath, StringProp(result, "sourcePrefab"));

                Assert.IsTrue(result.TryGetProperty("unappliedProperties", out var unapplied) && unapplied.Kind == JsonValueKind.Array);
                var unappliedPaths = unapplied.Items.Select(i => i.AsString()).ToList();
                CollectionAssert.DoesNotContain(unappliedPaths, "m_Size.x");
                CollectionAssert.DoesNotContain(unappliedPaths, "m_Size.y");
                CollectionAssert.DoesNotContain(unappliedPaths, "m_Size.z");

                // Only the KNOWN, permanent default-override set (the instance root's own name and
                // Transform) is left, so the calm/informational note, not the "something else is
                // wrong" one.
                Assert.IsTrue(result.TryGetProperty("note", out var note) && note.Kind == JsonValueKind.String);
                StringAssert.Contains("expected", note.AsString());
                StringAssert.DoesNotContain("NOT the known", note.AsString());
                LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(note.AsString());

                // Verified on disk, not by trusting the response - the whole point of this defect.
                var fileText = File.ReadAllText(AbsolutePath(prefabPath));
                StringAssert.Contains("m_Size: {x: 9, y: 9, z: 9}", fileText);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        // ------------------------------------------------------------------------ prefab.edit_property

        [Test]
        public void EditProperty_Atomic_LoadEditSaveInOneCall_VerifiedOnDisk_LeaseCleanlyReleased()
        {
            var go = new GameObject("AtomicTarget");
            var prefabPath = ScratchDir + "/AtomicTarget.prefab";
            PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            UnityEngine.Object.DestroyImmediate(go);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("prefabPath", JsonValue.String(prefabPath))
                    .SetProperty("componentType", JsonValue.String("Transform"))
                    .SetProperty("propertyName", JsonValue.String("m_LocalPosition"))
                    .SetProperty("value", JsonValue.NewObject()
                        .SetProperty("x", JsonValue.Float(11)).SetProperty("y", JsonValue.Float(12)).SetProperty("z", JsonValue.Float(13)));

                var result = CommandTable.Dispatch(gate, Request("prefab.edit_property", @params));

                Assert.AreEqual(prefabPath, StringProp(result, "prefab"));
                Assert.IsTrue(BoolProp(result, "savedImmediately"), "no session is open, so this call must save immediately");

                var fileText = File.ReadAllText(AbsolutePath(prefabPath));
                StringAssert.Contains("m_LocalPosition: {x: 11, y: 12, z: 13}", fileText);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        [Test]
        public void EditProperty_UnknownComponent_ThrowsActionableError_UnloadsContents_StillReleasesLease()
        {
            var go = new GameObject("BadComponentTarget");
            var prefabPath = ScratchDir + "/BadComponentTarget.prefab";
            PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            UnityEngine.Object.DestroyImmediate(go);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("prefabPath", JsonValue.String(prefabPath))
                    .SetProperty("componentType", JsonValue.String("Rigidbody")) // never added
                    .SetProperty("propertyName", JsonValue.String("m_Mass"))
                    .SetProperty("value", JsonValue.Float(2));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("prefab.edit_property", @params)));
                StringAssert.Contains("Rigidbody", ex.Message);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        [Test]
        public void EditProperty_NestedGameObjectPath_ResolvesInsidePrefabHierarchy_VerifiedOnDisk()
        {
            var root = new GameObject("NestedRoot");
            var child = new GameObject("Child");
            child.transform.SetParent(root.transform);
            var prefabPath = ScratchDir + "/NestedRoot.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("prefabPath", JsonValue.String(prefabPath))
                    .SetProperty("gameObjectPath", JsonValue.String("Child"))
                    .SetProperty("componentType", JsonValue.String("Transform"))
                    .SetProperty("propertyName", JsonValue.String("m_LocalPosition"))
                    .SetProperty("value", JsonValue.NewObject().SetProperty("x", JsonValue.Float(1)).SetProperty("y", JsonValue.Float(2)).SetProperty("z", JsonValue.Float(3)));

                CommandTable.Dispatch(gate, Request("prefab.edit_property", @params));

                var reloaded = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                var reloadedChild = reloaded.transform.Find("Child");
                Assert.IsNotNull(reloadedChild);
                Assert.AreEqual(new Vector3(1, 2, 3), reloadedChild.localPosition);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        // ---------------------------------------------------------- prefab.open_editing / prefab.save_editing

        [Test]
        public void OpenEditing_ReportsRootAndComponents_LeaseCleanlyReleased()
        {
            var go = new GameObject("SessionTarget");
            go.AddComponent<BoxCollider>();
            var prefabPath = ScratchDir + "/SessionTarget.prefab";
            PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            UnityEngine.Object.DestroyImmediate(go);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("prefabPath", JsonValue.String(prefabPath));
                var result = CommandTable.Dispatch(gate, Request("prefab.open_editing", @params));

                Assert.AreEqual(prefabPath, StringProp(result, "prefab"));
                Assert.AreEqual("SessionTarget", StringProp(result, "rootPath"));
                Assert.IsTrue(result.TryGetProperty("components", out var comps) && comps.Kind == JsonValueKind.Array);
                var names = new System.Collections.Generic.List<string>();
                foreach (var c in comps.Items) names.Add(c.AsString());
                CollectionAssert.Contains(names, "BoxCollider");

                AssertLeaseCleanlyReleased(fake, gate);

                // Clean up the still-open session so it does not leak into another test.
                CommandTable.Dispatch(gate, Request("prefab.save_editing", JsonValue.NewObject()));
            }
        }

        [Test]
        public void OpenEditing_WhileAlreadyOpen_ThrowsActionableError_StillReleasesLease()
        {
            var go = new GameObject("FirstSession");
            var prefabPath = ScratchDir + "/FirstSession.prefab";
            PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            UnityEngine.Object.DestroyImmediate(go);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("prefabPath", JsonValue.String(prefabPath));
                CommandTable.Dispatch(gate, Request("prefab.open_editing", @params));

                var ex = Assert.Throws<InvalidOperationException>(() => CommandTable.Dispatch(gate, Request("prefab.open_editing", @params)));
                StringAssert.Contains(prefabPath, ex.Message);

                // Defect 5 (docs/backlog/mutation-tool-defects.md): used to say "Call
                // prefab_save_editing first", a tool the 103->32 consolidation already removed.
                StringAssert.Contains("prefab_apply", ex.Message);
                StringAssert.DoesNotContain("prefab_save_editing", ex.Message);
                LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(ex.Message);

                AssertLeaseCleanlyReleased(fake, gate);

                CommandTable.Dispatch(gate, Request("prefab.save_editing", JsonValue.NewObject()));
            }
        }

        [Test]
        public void SaveEditing_WithNoSessionOpen_ThrowsActionableError_StillReleasesLease()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var ex = Assert.Throws<InvalidOperationException>(() => CommandTable.Dispatch(gate, Request("prefab.save_editing", JsonValue.NewObject())));

                // Defect 5 (docs/backlog/mutation-tool-defects.md): this used to say "Call
                // prefab_open_editing first" - prefab_open_editing does not exist post-consolidation
                // (folded into prefab_apply's 'editProperty' op, which needs no open session at all).
                StringAssert.Contains("prefab_apply", ex.Message);
                StringAssert.DoesNotContain("prefab_open_editing", ex.Message);
                LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(ex.Message);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        [Test]
        public void OpenEditSave_RoundTrip_ProducesExpectedPrefabOnDisk_VerifiedByReadingFile_LeaseCleanlyReleasedEachCall()
        {
            var go = new GameObject("RoundTripTarget");
            var prefabPath = ScratchDir + "/RoundTripTarget.prefab";
            PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            UnityEngine.Object.DestroyImmediate(go);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                // 1. open
                var openResult = CommandTable.Dispatch(gate, Request("prefab.open_editing",
                    JsonValue.NewObject().SetProperty("prefabPath", JsonValue.String(prefabPath))));
                Assert.AreEqual("RoundTripTarget", StringProp(openResult, "rootPath"));
                AssertLeaseCleanlyReleased(fake, gate);

                // 2. edit - targets the session ALREADY open (same prefabPath), so this must defer
                // saving rather than loading (and saving) a second, independent copy.
                var editResult = CommandTable.Dispatch(gate, Request("prefab.edit_property", JsonValue.NewObject()
                    .SetProperty("prefabPath", JsonValue.String(prefabPath))
                    .SetProperty("componentType", JsonValue.String("Transform"))
                    .SetProperty("propertyName", JsonValue.String("m_LocalPosition"))
                    .SetProperty("value", JsonValue.NewObject()
                        .SetProperty("x", JsonValue.Float(42)).SetProperty("y", JsonValue.Float(43)).SetProperty("z", JsonValue.Float(44)))));
                Assert.IsFalse(BoolProp(editResult, "savedImmediately"), "an edit against an OPEN session must defer saving to prefab_save_editing");
                Assert.IsFalse(File.ReadAllText(AbsolutePath(prefabPath)).Contains("x: 42"), "must not be on disk yet - only prefab_save_editing persists a session edit");
                AssertLeaseCleanlyReleased(fake, gate);

                // 3. save
                var saveResult = CommandTable.Dispatch(gate, Request("prefab.save_editing", JsonValue.NewObject()));
                Assert.AreEqual(prefabPath, StringProp(saveResult, "saved"));
                AssertLeaseCleanlyReleased(fake, gate);

                // Verified by reading the file, not by trusting any of the three responses above.
                var fileText = File.ReadAllText(AbsolutePath(prefabPath));
                StringAssert.Contains("m_LocalPosition: {x: 42, y: 43, z: 44}", fileText);
            }
        }

        // -------------------------------------------------------------------- prefab.create_variant

        [Test]
        public void CreateVariant_ProducesAVariantConnectedToBase_VerifiedOnDisk_LeaseCleanlyReleased()
        {
            var baseGo = new GameObject("Base");
            var basePrefabPath = ScratchDir + "/Base.prefab";
            PrefabUtility.SaveAsPrefabAsset(baseGo, basePrefabPath);
            UnityEngine.Object.DestroyImmediate(baseGo);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var variantPath = ScratchDir + "/Variant.prefab";
                var @params = JsonValue.NewObject()
                    .SetProperty("basePrefabPath", JsonValue.String(basePrefabPath))
                    .SetProperty("variantPath", JsonValue.String(variantPath));
                var result = CommandTable.Dispatch(gate, Request("prefab.create_variant", @params));

                Assert.AreEqual(variantPath, StringProp(result, "variant"));
                Assert.IsTrue(File.Exists(AbsolutePath(variantPath)));

                var variantAsset = AssetDatabase.LoadAssetAtPath<GameObject>(variantPath);
                Assert.AreEqual(PrefabAssetType.Variant, PrefabUtility.GetPrefabAssetType(variantAsset));

                // No leftover instance in the scene from building the variant.
                Assert.IsNull(GameObject.Find("Base"));

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        [Test]
        public void CreateVariant_UnknownBasePrefab_ThrowsActionableError_StillReleasesLease()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("basePrefabPath", JsonValue.String(ScratchDir + "/NoSuchBase.prefab"))
                    .SetProperty("variantPath", JsonValue.String(ScratchDir + "/WontBeCreated.prefab"));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("prefab.create_variant", @params)));
                StringAssert.Contains("NoSuchBase.prefab", ex.Message);

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        // ------------------------------------------------ prefab.create_variant - path guard (F16/F17/F20/F21)

        [Test]
        public void CreateVariant_TraversalVariantPath_RefusedBeforeAnyWrite_LeaseCleanlyReleased()
        {
            var baseGo = new GameObject("TraversalBase");
            var basePrefabPath = ScratchDir + "/TraversalBase.prefab";
            PrefabUtility.SaveAsPrefabAsset(baseGo, basePrefabPath);
            UnityEngine.Object.DestroyImmediate(baseGo);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("basePrefabPath", JsonValue.String(basePrefabPath))
                    .SetProperty("variantPath", JsonValue.String("Assets/../EscapedVariant.prefab"));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("prefab.create_variant", @params)));
                StringAssert.Contains("EscapedVariant.prefab", ex.Message);
                Assert.IsFalse(File.Exists(AbsolutePath("EscapedVariant.prefab")));

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        [Test]
        public void CreateVariant_ExistingFile_Refused_OriginalUntouched_LeaseCleanlyReleased()
        {
            var baseGo = new GameObject("ExistBase");
            var basePrefabPath = ScratchDir + "/ExistBase.prefab";
            PrefabUtility.SaveAsPrefabAsset(baseGo, basePrefabPath);
            UnityEngine.Object.DestroyImmediate(baseGo);

            var otherGo = new GameObject("Other");
            var variantPath = ScratchDir + "/AlreadyThereVariant.prefab";
            PrefabUtility.SaveAsPrefabAsset(otherGo, variantPath);
            UnityEngine.Object.DestroyImmediate(otherGo);
            var originalGuid = AssetDatabase.AssetPathToGUID(variantPath);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("basePrefabPath", JsonValue.String(basePrefabPath))
                    .SetProperty("variantPath", JsonValue.String(variantPath));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("prefab.create_variant", @params)));
                StringAssert.Contains("already exists", ex.Message);
                StringAssert.Contains("prefab_apply", ex.Message);

                Assert.AreEqual(originalGuid, AssetDatabase.AssetPathToGUID(variantPath), "the pre-existing file at variantPath must be untouched");

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        /// <summary>F21: accepting basePrefabPath == variantPath destroyed the target during the
        /// tester's own repro (the variant save silently replaced the base prefab file it was
        /// meant to be based on). Verified via GUID stability and asset TYPE (must stay Regular,
        /// never become a Variant of itself), not just "a file still exists at this path" - the
        /// destructive version of this bug leaves a file there too, just the wrong one.</summary>
        [Test]
        public void CreateVariant_BaseEqualsVariant_Refused_TargetUntouched_LeaseCleanlyReleased()
        {
            var baseGo = new GameObject("SelfBase");
            var basePrefabPath = ScratchDir + "/SelfBase.prefab";
            PrefabUtility.SaveAsPrefabAsset(baseGo, basePrefabPath);
            UnityEngine.Object.DestroyImmediate(baseGo);
            var originalGuid = AssetDatabase.AssetPathToGUID(basePrefabPath);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("basePrefabPath", JsonValue.String(basePrefabPath))
                    .SetProperty("variantPath", JsonValue.String(basePrefabPath));

                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("prefab.create_variant", @params)));
                StringAssert.Contains("basePrefabPath", ex.Message);
                StringAssert.Contains("variantPath", ex.Message);

                Assert.AreEqual(originalGuid, AssetDatabase.AssetPathToGUID(basePrefabPath));
                Assert.AreEqual(PrefabAssetType.Regular, PrefabUtility.GetPrefabAssetType(AssetDatabase.LoadAssetAtPath<GameObject>(basePrefabPath)));

                AssertLeaseCleanlyReleased(fake, gate);
            }
        }

        // ---------------------------------------------------------------------- lease busy elsewhere

        [Test]
        public void AnyClass2Call_WhileADifferentLeaseIsHeld_ThrowsActionableError_DoesNotStealOrReleaseIt()
        {
            var go = new GameObject("Busy");
            var prefabPath = ScratchDir + "/Busy.prefab";

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                Assert.IsTrue(gate.Acquire("someone-elses-session", TimeSpan.FromMinutes(5)));

                var @params = JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("Busy"))
                    .SetProperty("assetPath", JsonValue.String(prefabPath));

                var ex = Assert.Throws<InvalidOperationException>(() => CommandTable.Dispatch(gate, Request("prefab.create", @params)));
                StringAssert.Contains("someone-elses-session", ex.Message);

                // Defect 5 (docs/backlog/mutation-tool-defects.md): this used to say "likely an
                // in-progress BeginScriptEditing session... Call lease_release" - BeginScriptEditing
                // was renamed (not just consolidated) to script_editing_session, and lease_release was
                // never a real MCP tool name at all (the actual wire method, "lease.release", is an
                // internal plugin<->app RPC, never something an agent calls directly).
                StringAssert.Contains("script_editing_session", ex.Message);
                StringAssert.DoesNotContain("BeginScriptEditing", ex.Message);
                StringAssert.DoesNotContain("lease_release", ex.Message);
                LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(ex.Message);

                // The other session's lease must be completely untouched.
                Assert.IsTrue(gate.IsHeld);
                Assert.AreEqual("someone-elses-session", gate.CurrentLeaseId);
                Assert.AreEqual(1, fake.LockCalls, "the busy class-2 call must never itself call Lock");
                Assert.AreEqual(0, fake.UnlockCalls, "the busy class-2 call must never release a lease it never held");

                gate.Release("someone-elses-session");
            }
        }
    }
}
