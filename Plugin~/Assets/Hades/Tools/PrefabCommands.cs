// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Linq;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;
using UnityEngine;

namespace Hades.Tools
{
    /// <summary>
    /// Class-2 (multi-tick, one call, lease bounded by the call - see the "52 Editor tools" plan's
    /// operation-class table) prefab mutations: create/instantiate/apply-overrides/edit-property/
    /// create-variant, plus the open/save editing-session bookend pair. Every handler here runs its
    /// Unity work through <see cref="LeaseScope.Run"/> - acquire, do the work, release in a
    /// finally, all inside this one synchronous <see cref="CommandTable.Dispatch"/> call - because
    /// PrefabUtility's load/save/instantiate operations can trigger asset import, which is real
    /// reload risk bounded by this call (unlike SceneCommands/ComponentCommands' class-1 contract,
    /// where reload risk does not exist at all - see those classes' own doc comments).
    ///
    /// UNLIKE class 3 (BeginScriptEditing, a later task), an exception here must never leave the
    /// lease held: <see cref="LeaseScope.Run"/>'s finally releases unconditionally, so a thrown
    /// ArgumentException/NullReferenceException/whatever mid-operation still leaves gate.IsHeld
    /// false by the time this handler returns to CommandTable.Dispatch. This is deliberately the
    /// OPPOSITE of class 3's semantics, where an exception intentionally leaves the lease owned
    /// because an exception is not evidence the editing session finished - see
    /// PrefabCommandsTests for the exception-safety proof, and LeaseScope's own doc comment.
    ///
    /// prefab.open_editing/prefab.save_editing hold their SESSION state (the loaded prefab
    /// contents' root GameObject and its source path) in plain static fields, exactly like the old
    /// package's PrefabTools - but, unlike that in-memory session, the RELOAD LEASE itself never
    /// spans the gap between the two calls: each acquires and releases independently, so a domain
    /// reload landing between prefab.open_editing and prefab.save_editing is possible (nothing
    /// blocks it) and would destroy the session's held root. CloseEditingSessionBeforeReload (hooked
    /// to AssemblyReloadEvents.beforeAssemblyReload, same as the old package) cleans up for that
    /// case; prefab.save_editing then reports an actionable "no session open" error rather than
    /// crashing on a stale reference, and the caller simply calls prefab.open_editing again.
    ///
    /// <para><b>Plan 10 Task 2.</b> CreatePrefab/InstantiatePrefab/ApplyOverrides/EditProperty/
    /// CreateVariant are each split into a thin <c>LeaseScope.Run(gate, "prefab.xxx", () =&gt;
    /// DoXxx(@params))</c> wrapper plus a lease-free <c>internal static JsonValue DoXxx(JsonValue
    /// @params)</c> core - so <see cref="PrefabApplyCommands"/> (prefab_apply's plugin-side batch
    /// handler) can call the SAME core logic directly, inside the ONE LeaseScope.Run that wraps its
    /// WHOLE batch, rather than each op re-acquiring its own nested lease (which would fail outright -
    /// ReloadGate.Acquire rejects a second id while one is already held, and even if it did not,
    /// N separate Lock/Unlock windows inside one call is exactly the "one call, one reload window"
    /// property Plan 10 requires prefab_apply NOT to violate). Parameter parsing now happens INSIDE
    /// the lease-acquired region for the five refactored methods (previously outside, before
    /// LeaseScope.Run was even called) - a missing/blank parameter still throws the identical
    /// ArgumentException either way, and LeaseScope.Run's finally releases unconditionally regardless
    /// of where inside it an exception comes from, so no existing PrefabCommandsTests assertion
    /// (which only ever checks the lease balances, never that Lock was skipped entirely) observes the
    /// difference. OpenEditing/SaveEditing are NOT split - prefab_apply deliberately does not expose
    /// "open"/"save" as ops (that is the very footgun it exists to remove - see PrefabApplyCommands'
    /// own doc comment), so nothing ever needs to call their core logic without a lease of its own.
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class PrefabCommands
    {
        static PrefabCommands()
        {
            AssemblyReloadEvents.beforeAssemblyReload += CloseEditingSessionBeforeReload;
        }

        // ---------------------------------------------------------------- prefab.create

        internal static JsonValue CreatePrefab(ReloadGate gate, JsonValue @params) =>
            LeaseScope.Run(gate, "prefab.create", () => DoCreate(@params));

        /// <summary>Lease-free core - see this class's own doc comment ("Plan 10 Task 2").
        ///
        /// docs/backlog/mutation-tool-defects.md's Defect 3: this used to call
        /// PrefabUtility.SaveAsPrefabAsset - Unity's DISCONNECTED save - which left the
        /// 'gameObjectPath' GameObject as a plain, unconnected object in the scene. The obvious way
        /// to build a nested prefab - create a leaf, reparent it under a new root, create the parent
        /// from that root - then silently produced a flattened, disconnected copy (no PrefabInstance
        /// block, no m_SourcePrefab) instead of a real nested instance, with every step reporting
        /// success and nothing warning. <see cref="DoCreateVariant"/> already got this right for
        /// variants (SaveAsPrefabAssetAndConnect); this now follows the identical pattern.
        ///
        /// The connect side effect is real, not cosmetic, and is exactly what a caller means by
        /// "create a prefab from this object": 'go' itself becomes a connected Prefab instance in
        /// the scene - per Unity's own documented behaviour for this overload, "the original object
        /// remains in the scene but becomes linked to the newly created Prefab Asset" (it is not
        /// destroyed/replaced with a new instance, so an existing reference to 'go' stays valid).
        ///
        /// InteractionMode.AutomatedAction, not UserAction - matching DoCreateVariant exactly, for
        /// two reasons specific to this call site: (1) prefab.* is class 2, deliberately outside
        /// Unity's interactive Undo model (see CommandTable's MutatingMethods doc comment), and
        /// prefab.create is not a MutatingMethods entry, so - unlike a class-1 mutation - nothing
        /// pre-increments its Undo group before this runs. Recording the connect step under
        /// UserAction would land it in whatever group happens to already be current (possibly a
        /// stale group left by an unrelated prior call), exactly the cross-call group-bleeding
        /// Task 7's Defect 3 fixed once already for class 1 (see CommandTableUndoGroupingTests) -
        /// AutomatedAction sidesteps this by recording nothing here, the same tradeoff
        /// DoCreateVariant already made for its own (temporary, destroyed-immediately) instance.
        /// (2) AutomatedAction never shows a confirmation dialog; UserAction can, which would hang
        /// a headless/batchmode Editor with nobody able to click it.</summary>
        internal static JsonValue DoCreate(JsonValue @params)
        {
            var goPath = JsonParams.RequireString(@params, "gameObjectPath", "prefab.create");
            var assetPath = JsonParams.RequireString(@params, "assetPath", "prefab.create");

            var go = GameObjectPaths.FindByPath(goPath) ?? throw GameObjectPaths.NotFoundError(goPath);

            AssetFolders.EnsureExists(AssetFolders.DirectoryName(assetPath));

            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(go, assetPath, InteractionMode.AutomatedAction, out var success);
            if (!success || prefab == null)
            {
                throw new ArgumentException(
                    "Failed to save prefab at '" + assetPath + "'. Ensure the path ends with '.prefab' and is under 'Assets/'.");
            }

            return JsonValue.NewObject()
                .SetProperty("createdAsset", JsonValue.String(assetPath))
                .SetProperty("guid", JsonValue.String(AssetDatabase.AssetPathToGUID(assetPath)));
        }

        // ---------------------------------------------------------------- prefab.instantiate

        internal static JsonValue InstantiatePrefab(ReloadGate gate, JsonValue @params) =>
            LeaseScope.Run(gate, "prefab.instantiate", () => DoInstantiate(@params));

        /// <summary>Lease-free core - see this class's own doc comment ("Plan 10 Task 2").</summary>
        internal static JsonValue DoInstantiate(JsonValue @params)
        {
            var prefabPath = JsonParams.RequireString(@params, "prefabPath", "prefab.instantiate");
            var parentPath = JsonParams.OptionalString(@params, "parent");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) ?? throw PrefabNotFoundError(prefabPath);

            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (instance == null) throw new ArgumentException("Failed to instantiate prefab at '" + prefabPath + "'.");

            if (!string.IsNullOrEmpty(parentPath))
            {
                var parentGo = GameObjectPaths.FindByPath(parentPath);
                if (parentGo == null)
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                    throw GameObjectPaths.NotFoundError(parentPath);
                }
                instance.transform.SetParent(parentGo.transform);
            }

            Undo.RegisterCreatedObjectUndo(instance, "Hades Instantiate " + prefab.name);

            return JsonValue.NewObject()
                .SetProperty("name", JsonValue.String(instance.name))
                .SetProperty("path", JsonValue.String(GameObjectPaths.GetPath(instance)))
                .SetProperty("fileId", JsonValue.Integer(GameObjectPaths.FileId(instance)));
        }

        // ---------------------------------------------------------------- prefab.apply_overrides

        /// <summary>Task 7's Defect 1, resolved by measurement rather than by guessing: this call
        /// used to report success while leaving the prefab asset on disk byte-identical, with no
        /// exception and every precondition (IsPartOfPrefabInstance true, GetPropertyModifications
        /// listing every expected entry) satisfied.
        ///
        /// RULED OUT, each checked directly against a real Unity Editor, not assumed:
        ///  - the instance not being the OUTERMOST prefab instance root (GetOutermostPrefabInstanceRoot(go)
        ///    resolved to the exact same GameObject already being passed, in every reproduction -
        ///    still resolved explicitly below anyway, since ApplyPrefabInstance documents this as a
        ///    hard precondition and a caller COULD point 'gameObjectPath' at a nested descendant);
        ///  - InteractionMode.AutomatedAction vs UserAction (identical outcome either way);
        ///  - a missing AssetDatabase.SaveAssets()/SaveAssetIfDirty() afterward (the modification
        ///    never reaches even the in-memory cached asset AssetDatabase.LoadAssetAtPath returns,
        ///    which a missing flush-to-disk could not explain by itself);
        ///  - a `-batchmode -nographics` limitation (identical failure against a full interactive
        ///    Editor - see this plan's Task 7 results).
        ///
        /// ACTUAL CAUSE (Unity's own documented behaviour for PrefabUtility.ApplyPrefabInstance):
        /// "The Transform position and rotation of a root GameObject in a Prefab instance cannot be
        /// applied, nor can other default override properties" - measured directly, with a plain
        /// GetPropertyModifications dump immediately after InstantiatePrefab, before touching
        /// anything: EVERY prefab instance's outermost root ALWAYS lists exactly 11 entries the
        /// instant it is instantiated, before any user action at all - the GameObject's own
        /// 'm_Name' plus its Transform's 'm_LocalPosition.{x,y,z}', 'm_LocalRotation.{w,x,y,z}',
        /// and 'm_LocalEulerAnglesHint.{x,y,z}' - regardless of whether their values actually
        /// differ from the prefab asset. These are Unity's own
        /// "default override" properties: always instance-specific, never prefab-asset content, in
        /// EVERY apply API Unity ships (ApplyPrefabInstance, ApplyPropertyOverride, the Inspector's
        /// own "Apply All") - there is no alternative call that applies them. Every reproduction of
        /// this defect happened to override one of these (root position). GetPropertyModifications
        /// still lists them as raw diffs (why every OTHER precondition looked satisfied) even though
        /// Unity's higher-level apply machinery silently excludes them - exactly why this looked
        /// like nothing was happening rather than a known, permanent limitation. An ordinary
        /// component property (e.g. a BoxCollider's size) is NOT in this always-present set and DOES
        /// apply correctly - confirmed positively, not just by ruling out the negative.
        ///
        /// So: this is a genuine, permanent Unity limitation, not a bug in this wrapper, and not
        /// fixable by calling ApplyPrefabInstance differently. What WAS fixable is the dishonesty -
        /// reporting blanket success while silently dropping some (not necessarily all) of the
        /// requested overrides. Comparing GetPropertyModifications before and after the call -
        /// rather than only hardcoding the known 11 - is what catches this AND any other property a
        /// future Unity version excludes the same way, in 'unappliedProperties' below; the known-11
        /// classification (DefaultOverridePropertyPaths) exists only to keep this call's 'note' from
        /// firing on every single invocation for a reason nothing can be done about, while still
        /// listing them honestly in 'unappliedProperties' rather than hiding them.</summary>
        internal static JsonValue ApplyOverrides(ReloadGate gate, JsonValue @params) =>
            LeaseScope.Run(gate, "prefab.apply_overrides", () => DoApplyOverrides(@params));

        /// <summary>Lease-free core - see this class's own doc comment ("Plan 10 Task 2"). Every
        /// property of THIS method's own doc comment above (the unappliedProperties investigation)
        /// still applies unchanged - prefab_apply's 'applyOverrides' op reports the identical
        /// 'unappliedProperties'/'note' shape, never blanket success, because it calls this SAME
        /// code, not a reimplementation.</summary>
        internal static JsonValue DoApplyOverrides(JsonValue @params)
        {
            var goPath = JsonParams.RequireString(@params, "gameObjectPath", "prefab.apply_overrides");

            var go = GameObjectPaths.FindByPath(goPath) ?? throw GameObjectPaths.NotFoundError(goPath);

            if (!PrefabUtility.IsPartOfPrefabInstance(go))
            {
                throw new ArgumentException(
                    "GameObject '" + GameObjectPaths.GetPath(go) + "' is not a prefab instance. "
                    + "Only prefab instances in the scene can have overrides applied.");
            }

            // ApplyPrefabInstance's own documented precondition: the OUTERMOST instance root,
            // never a nested descendant - see this method's own doc comment.
            var instanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(go) ?? go;

            var before = PrefabUtility.GetPropertyModifications(go) ?? Array.Empty<PropertyModification>();

            PrefabUtility.ApplyPrefabInstance(instanceRoot, InteractionMode.AutomatedAction);
            var sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);

            // Still present after the call (same target, same propertyPath) means Unity did NOT
            // apply it - see this method's own doc comment for the known, permanent reason (the
            // instance root's own "default override" properties) this can legitimately happen
            // for even though the call throws nothing.
            var after = PrefabUtility.GetPropertyModifications(go) ?? Array.Empty<PropertyModification>();
            var unapplied = before
                .Where(b => after.Any(a => a.target == b.target && a.propertyPath == b.propertyPath))
                .ToList();

            // PropertyModification.target is documented to be the SOURCE (prefab asset) object
            // a modification overrides - obtained via GetCorrespondingObjectFromSource - never
            // the scene instance itself, so classifying "is this the instance root's own default
            // override" must compare against the asset-side root/transform, not instanceRoot
            // directly (measured directly: comparing against instanceRoot itself never matched,
            // which is what first surfaced this).
            var assetRoot = PrefabUtility.GetCorrespondingObjectFromSource(instanceRoot) as GameObject;
            var unexpected = unapplied.Where(m => !IsKnownDefaultOverride(m, assetRoot)).ToList();

            var unappliedJson = JsonValue.NewArray();
            foreach (var m in unapplied) unappliedJson.Add(JsonValue.String(m.propertyPath));

            var result = JsonValue.NewObject()
                .SetProperty("applied", JsonValue.String(GameObjectPaths.GetPath(go)))
                .SetProperty("sourcePrefab", JsonValue.String(sourcePath))
                .SetProperty("unappliedProperties", unappliedJson);

            if (unexpected.Count > 0)
            {
                result.SetProperty("note", JsonValue.String(
                    "Unity did not write " + unexpected.Count + " unexpected propert" + (unexpected.Count == 1 ? "y" : "ies")
                    + " (" + string.Join(", ", unexpected.Select(m => m.propertyPath)) + ", among those listed in "
                    + "'unappliedProperties') to the prefab asset, even though this call reported no error. This is "
                    + "NOT the known root-Transform/name limitation (see below) - something else prevented it, and "
                    + "retrying is unlikely to help without changing what is being overridden."));
            }
            else if (unapplied.Count > 0)
            {
                result.SetProperty("note", JsonValue.String(
                    "'unappliedProperties' lists only this prefab instance's own root-level 'default override' "
                    + "properties (its name and/or Transform position/rotation) - for a base prefab, Unity does "
                    + "not write these back via any apply API, so this is expected and not a sign anything went "
                    + "wrong with this call. (Observed to behave differently for Prefab Variants, where a root "
                    + "position change has reached the variant's own modification list on disk - this "
                    + "restriction is not confirmed universal.) If you specifically need to change the prefab "
                    + "asset's own default name, position, or rotation, edit the prefab asset directly instead "
                    + "(prefab_apply with one or more 'editProperty' operations), rather than moving an instance "
                    + "in a scene and applying it."));
            }

            return result;
        }

        /// <summary>Unity's own "default override" properties (see ApplyOverrides' own doc comment
        /// for how this exact set was measured): a prefab instance's OUTERMOST root always lists its
        /// own name and its Transform's position/rotation as "modified" - regardless of whether the
        /// values actually differ from the prefab asset - and never actually applies any of them, in
        /// any Unity apply API. Deliberately does NOT include scale: unlike position/rotation, scale
        /// was measured absent from a freshly-instantiated, untouched instance's own modification
        /// list, matching Unity's own manual page naming only "position and rotation" - so an
        /// overridden root scale is expected to apply normally and must not be swallowed here.</summary>
        static readonly string[] DefaultOverridePropertyPaths =
        {
            "m_Name",
            "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z",
            "m_LocalRotation.w", "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z",
            "m_LocalEulerAnglesHint.x", "m_LocalEulerAnglesHint.y", "m_LocalEulerAnglesHint.z",
        };

        /// <summary><paramref name="assetRoot"/> is the SOURCE (prefab asset) GameObject
        /// corresponding to the instance root - i.e. PrefabUtility.GetCorrespondingObjectFromSource
        /// of the instance root, NOT the instance root itself. <paramref name="m"/>.target is
        /// documented to always be an asset-side reference (see ApplyOverrides' own doc comment for
        /// where this was confirmed the hard way: comparing against the INSTANCE side never
        /// matched), so this must compare against <paramref name="assetRoot"/>/its transform, not
        /// any instance-side object - a null <paramref name="assetRoot"/> (should not happen once
        /// IsPartOfPrefabInstance is true, but defensively) simply never matches, same as any other
        /// property this classification does not recognise.</summary>
        static bool IsKnownDefaultOverride(PropertyModification m, GameObject assetRoot) =>
            assetRoot != null && (m.target == assetRoot || m.target == assetRoot.transform)
            && Array.IndexOf(DefaultOverridePropertyPaths, m.propertyPath) >= 0;

        // ---------------------------------------------------------------- prefab.edit_property

        /// <summary>Two modes, chosen automatically:
        ///
        /// 1. A prefab.open_editing session is currently open for THIS SAME prefabPath: mutates
        /// its already-loaded root directly and leaves saving to prefab_save_editing (result's
        /// 'savedImmediately' is false). Detected by path equality against the open session, not
        /// merely "a session is open" - loading a SECOND, independent copy of the SAME prefab via
        /// PrefabUtility.LoadPrefabContents and saving it here would race prefab_save_editing's
        /// eventual save of the FIRST copy, silently clobbering whichever one saves last, so this
        /// is the one case that must reuse the open root instead of loading its own.
        ///
        /// 2. Otherwise (no session open, or the open session is for a DIFFERENT prefab): atomic,
        /// self-contained load/edit/save, exactly like the old package's EditPrefabProperty -
        /// 'savedImmediately' is true.
        ///
        /// Either way, targets the prefab's root GameObject by default, or a nested child via the
        /// optional 'gameObjectPath' (resolved relative to the prefab's own root - see
        /// <see cref="FindDescendant"/> - NOT the scene-rooted resolution GameObjectPaths.FindByPath
        /// does, since a loaded prefab's contents are not part of any scene).</summary>
        internal static JsonValue EditProperty(ReloadGate gate, JsonValue @params) =>
            LeaseScope.Run(gate, "prefab.edit_property", () => DoEditProperty(@params));

        /// <summary>Lease-free core - see this class's own doc comment ("Plan 10 Task 2"). Still
        /// detects and defers to an already-open prefab_open_editing session for the SAME
        /// prefabPath exactly as before - prefab_apply's 'editProperty' op does NOT bypass that
        /// check, so mixing an in-flight prefab_open_editing session with a prefab_apply batch
        /// targeting the same prefab stays well-defined rather than silently racing it.</summary>
        internal static JsonValue DoEditProperty(JsonValue @params)
        {
            var prefabPath = JsonParams.RequireString(@params, "prefabPath", "prefab.edit_property");
            var componentType = JsonParams.RequireString(@params, "componentType", "prefab.edit_property");
            var propertyName = JsonParams.RequireString(@params, "propertyName", "prefab.edit_property");
            var value = JsonParams.OptionalValue(@params, "value") ?? JsonValue.Null;
            var targetPath = JsonParams.OptionalString(@params, "gameObjectPath");

            var usingOpenSession = _editingRoot != null && string.Equals(_editingPrefabPath, prefabPath, StringComparison.Ordinal);
            var root = usingOpenSession ? _editingRoot : null;

            try
            {
                if (!usingOpenSession)
                {
                    if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
                        throw new ArgumentException("Prefab not found at '" + prefabPath + "'.");
                    root = PrefabUtility.LoadPrefabContents(prefabPath);
                }

                var target = string.IsNullOrEmpty(targetPath) ? root : FindDescendant(root, targetPath);
                if (target == null)
                {
                    throw new ArgumentException(
                        "GameObject not found inside prefab: '" + targetPath + "'. Prefab root is '" + root.name + "'.");
                }

                var type = ComponentTypes.Find(componentType) ?? throw ComponentTypes.NotFoundError(componentType);
                var component = GameObjectPaths.RequireComponent(target, type, componentType);

                var so = new SerializedObject(component);
                var resolved = SerializedPropertyJson.ResolvePropertyName(so, propertyName, out var resolveError)
                    ?? throw new ArgumentException(resolveError);

                SerializedPropertyJson.Set(so.FindProperty(resolved), value);
                so.ApplyModifiedProperties();

                // Only this handler's OWN, self-loaded copy is saved here - a mutation against
                // an already-open session is persisted later, exactly once, by
                // prefab_save_editing.
                if (!usingOpenSession) PrefabUtility.SaveAsPrefabAsset(root, prefabPath);

                return JsonValue.NewObject()
                    .SetProperty("prefab", JsonValue.String(prefabPath))
                    .SetProperty("component", JsonValue.String(type.Name))
                    .SetProperty("property", JsonValue.String(propertyName))
                    .SetProperty("newValue", value)
                    .SetProperty("savedImmediately", JsonValue.Bool(!usingOpenSession));
            }
            finally
            {
                if (!usingOpenSession && root != null) PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>Resolves a '/'-separated path of child names starting from
        /// <paramref name="root"/> itself (root's own name is NOT part of
        /// <paramref name="relativePath"/>, unlike GameObjectPaths.FindByPath, which matches its
        /// first segment against scene ROOT OBJECTS - inside a loaded prefab there is exactly one
        /// root, already known, so the whole path is children of it). Returns null (not root) when
        /// not found, matching FindByPath's own null-for-"not found" convention.</summary>
        static GameObject FindDescendant(GameObject root, string relativePath)
        {
            var current = root.transform;
            foreach (var segment in relativePath.Split('/'))
            {
                current = current.Find(segment);
                if (current == null) return null;
            }
            return current.gameObject;
        }

        // ---------------------------------------------------------------- prefab.open_editing / prefab.save_editing

        static GameObject _editingRoot;
        static string _editingPrefabPath;

        internal static JsonValue OpenEditing(ReloadGate gate, JsonValue @params)
        {
            var prefabPath = JsonParams.RequireString(@params, "prefabPath", "prefab.open_editing");

            return LeaseScope.Run(gate, "prefab.open_editing", () =>
            {
                if (_editingRoot != null)
                {
                    throw new InvalidOperationException(
                        "A prefab is already open for editing: '" + _editingPrefabPath + "'. Finish that session "
                        + "first, or use prefab_apply with one or more 'editProperty' operations instead, which "
                        + "needs no open/close session at all.");
                }

                if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
                    throw new ArgumentException("Prefab not found at '" + prefabPath + "'.");

                _editingRoot = PrefabUtility.LoadPrefabContents(prefabPath);
                _editingPrefabPath = prefabPath;

                var componentsJson = JsonValue.NewArray();
                foreach (var c in _editingRoot.GetComponents<Component>().Where(c => c != null))
                    componentsJson.Add(JsonValue.String(c.GetType().Name));

                return JsonValue.NewObject()
                    .SetProperty("prefab", JsonValue.String(prefabPath))
                    .SetProperty("rootPath", JsonValue.String(_editingRoot.name))
                    .SetProperty("components", componentsJson);
            });
        }

        internal static JsonValue SaveEditing(ReloadGate gate, JsonValue @params)
        {
            return LeaseScope.Run(gate, "prefab.save_editing", () =>
            {
                if (_editingRoot == null)
                    throw new InvalidOperationException(
                        "No prefab is currently open for editing. Use prefab_apply with one or more "
                        + "'editProperty' operations instead, which needs no open/close session at all.");

                var path = _editingPrefabPath;
                try
                {
                    PrefabUtility.SaveAsPrefabAsset(_editingRoot, _editingPrefabPath);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(_editingRoot);
                    _editingRoot = null;
                    _editingPrefabPath = null;
                }

                return JsonValue.NewObject().SetProperty("saved", JsonValue.String(path));
            });
        }

        /// <summary>Mirrors the old package's PrefabTools.ClosePrefabEditingSession exactly: a
        /// domain reload is about to wipe every static field in this class anyway, but
        /// PrefabUtility.UnloadPrefabContents is still called first so Unity's own prefab-stage
        /// bookkeeping (not just this class's fields) does not leak a preview scene across the
        /// reload.</summary>
        internal static void CloseEditingSessionBeforeReload()
        {
            if (_editingRoot != null)
            {
                PrefabUtility.UnloadPrefabContents(_editingRoot);
                _editingRoot = null;
                _editingPrefabPath = null;
            }
        }

        // ---------------------------------------------------------------- prefab.create_variant

        internal static JsonValue CreateVariant(ReloadGate gate, JsonValue @params) =>
            LeaseScope.Run(gate, "prefab.create_variant", () => DoCreateVariant(@params));

        /// <summary>Lease-free core - see this class's own doc comment ("Plan 10 Task 2").</summary>
        internal static JsonValue DoCreateVariant(JsonValue @params)
        {
            var basePrefabPath = JsonParams.RequireString(@params, "basePrefabPath", "prefab.create_variant");
            var variantPath = JsonParams.RequireString(@params, "variantPath", "prefab.create_variant");

            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(basePrefabPath) ?? throw PrefabNotFoundError(basePrefabPath);

            var instance = PrefabUtility.InstantiatePrefab(basePrefab) as GameObject;
            if (instance == null) throw new ArgumentException("Failed to instantiate base prefab '" + basePrefabPath + "'.");

            try
            {
                AssetFolders.EnsureExists(AssetFolders.DirectoryName(variantPath));

                PrefabUtility.SaveAsPrefabAssetAndConnect(instance, variantPath, InteractionMode.AutomatedAction, out var success);
                if (!success) throw new ArgumentException("Failed to save prefab variant at '" + variantPath + "'.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }

            return JsonValue.NewObject()
                .SetProperty("basePrefab", JsonValue.String(basePrefabPath))
                .SetProperty("variant", JsonValue.String(variantPath));
        }

        // ---------------------------------------------------------------- shared helpers

        static ArgumentException PrefabNotFoundError(string path) =>
            new ArgumentException(
                "Prefab not found at '" + path + "'. Ensure the path is a valid project-relative asset path, e.g. 'Assets/Prefabs/MyPrefab.prefab'.");
    }

    /// <summary>
    /// Shared "acquire, do bounded work, release" wrapper for every class-2 handler in this plan -
    /// PrefabCommands, the class-2 half of AssetCommands, and ProjectCommands all go through this
    /// rather than each hand-rolling its own try/finally, which is exactly how a future handler
    /// could forget the release-on-exception half of the contract (see this plan's "the property
    /// that matters most" framing: after ANY class-2 call - success or exception - no lease may
    /// remain held). The lock never spans an await (spec rule 3): everything inside
    /// <paramref name="work"/> runs synchronously on the main thread inside one
    /// CommandTable.Dispatch call, so there is no path where control returns to a caller with the
    /// lease still held - a thrown exception unwinds straight through the finally below before
    /// this method's own call frame is gone.
    ///
    /// A fresh, single-use lease id per call (a GUID, not a fixed constant) - two class-2 calls
    /// must never appear to be "the same session" to ReloadGate, which is exactly what a shared
    /// fixed id would do (ReloadGate.Acquire treats re-acquiring the SAME id as a renewal, not a
    /// fresh hold).
    ///
    /// If Acquire returns false, the gate is currently held by a DIFFERENT lease - in practice,
    /// almost always an in-progress BeginScriptEditing (class 3) session - so this throws an
    /// actionable error rather than silently proceeding unlocked, which would defeat the entire
    /// point of the gate. The whole operation is skipped and Release is never called for an id
    /// that was never actually acquired (ReloadGate.Release's own contract only promises
    /// correctness for an id it recognises).
    /// </summary>
    internal static class LeaseScope
    {
        public static JsonValue Run(ReloadGate gate, string operationName, Func<JsonValue> work, TimeSpan? ttl = null)
        {
            if (gate == null) throw new ArgumentNullException(nameof(gate));
            if (work == null) throw new ArgumentNullException(nameof(work));

            var leaseId = "hades-" + operationName + "-" + Guid.NewGuid().ToString("N");
            if (!gate.Acquire(leaseId, ttl))
            {
                var holder = gate.CurrentLeaseId;
                throw new InvalidOperationException(
                    "'" + operationName + "' needs Unity's reload lock, but it is currently held by lease '" + holder
                    + "' (likely an in-progress script_editing_session). Call script_editing_session with "
                    + "action 'end', or wait for it to finish, then retry.");
            }

            try
            {
                return work();
            }
            finally
            {
                gate.Release(leaseId);
            }
        }
    }
}
