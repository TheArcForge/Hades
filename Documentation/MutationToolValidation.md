# Mutation Tool Validation Matrix

## Why this document exists

An external tester's single recurring structural criticism, rediscovered every review round, was
that validation and reporting in `UnityPlugin/Assets/Hades/Tools/` are implemented **per tool** rather
than in a shared layer: `prefab_apply` refused a bad target type while `material_apply` overwrote a
tracked prefab; `animation_apply` refused a duplicate create while the others silently replaced;
`asset_manage` refused a self-move while `scene_apply` accepted a self-parent. Each instance was a
small, fast fix - which is exactly why the *family* kept regenerating: fixing the instance a tester
happened to find never audited the other tools built the same way.

This document is the systematic sweep that was missing: **one table, per mutation tool, per
validation dimension** - path normalisation, target type, existence, self-reference, lease/undo
behaviour, and failure-reporting shape - so a gap is something this document is wrong about, not
something nobody looked for. It converts "we fixed what the tester found" into "we know the
surface."

**How to keep this true.** Every cell below cites a `file:line`. When you add a new mutating
operation (a new `case` in an existing `*ApplyCommands`/`*ManageCommands` batch, or a new wire
method), add a row here in the same pass - not as a follow-up. When you change a guard's behaviour,
fix the citation, not just the prose. If a cell says "guarded", the guard it names must still exist
at that citation; if you delete or bypass it, this document is now lying and must be updated in the
same change. Treat a stale citation here the same as a stale code comment: a bug, not a nit.

**Scope.** This covers every mutating command registered in `UnityPlugin/Assets/Hades/Tools/`
(`CommandTable.cs`'s own dispatch table - see Table 0). It does not cover `Mac/*`, `Core/src/
Hades.Core/*` (the server-side knowledge graph - `find_references_to`/`trace_dependencies` and
similar read-side inconsistencies belong to a separate audit of that layer), or `scripts/
regression/*`.

**As of:** 2026-08-17. Baseline before this pass: a shared `AssetPathGuard` already existed
(`UnityPlugin/Assets/Hades/Tools/AssetPathGuard.cs`) and several individual findings from an earlier
internal test round (tagged `F16`/`F17`/`F20`/`F21` in code comments) were already fixed - this
document does not re-litigate those; it records where the SAME class of check is still missing on a
sibling tool that was never revisited.

---

## Table 0 - Inventory: every mutating command family

Verified from `CommandTable.cs`'s own `Handlers`/`MutatingMethods` tables (`CommandTable.cs:29-182`),
cross-checked against each file's actual code (not the file's own doc-comment claims).

| Family (wire prefix) | File | Class | Ops | Lease model |
|---|---|---|---|---|
| `scene.*` (hierarchy) | `SceneCommands.cs` | 1 | create_gameobject, create_primitive, delete_gameobject, reparent_gameobject, rename_gameobject, setup | none (`CommandTable` pre-increments Undo group) |
| `scene.apply` | `SceneApplyCommands.cs` | 1 | create, addComponent, removeComponent, setProperties, setReference, addListener, removeListener, delete, reparent, rename, select | none; self-manages one Undo group for the whole batch |
| `component.*` / `reference.set` / `event.*` | `ComponentCommands.cs` | 1 | add, remove, set_property, set_properties, reference.set, event.add_listener, event.remove_listener | none |
| `material.*` | `MaterialCommands.cs` | 1 | create, set_property, assign, duplicate, swap_shader | none |
| `material.apply` | `MaterialApplyCommands.cs` | 1 | create, setProperty, assign, duplicate, swapShader | none; self-manages one Undo group |
| `animation.*` | `AnimationCommands.cs` | 1 | assign_controller, assign_clip, create_controller, edit_controller | none |
| `animation.apply` | `AnimationApplyCommands.cs` | 1 | assignController, assignClip, createController, editController | none; self-manages one Undo group |
| `tag.*` / `layer.create` | `TagLayerCommands.cs` | 1 | tag.create, tag.delete, layer.create | none |
| `scene.save` / `scene.create` / `scene.duplicate` / `scene.set_build` | `SceneManagementCommands.cs` | 1 | save, create, duplicate, set_build | none |
| `asset.move` | `AssetCommands.cs` | 1 | move | none |
| `inspector.select` | `InspectorCommands.cs` | 1 | select | none (selection is not Undo-tracked by this plugin) |
| `prefab.*` | `PrefabCommands.cs` | 2 | create, instantiate, apply_overrides, edit_property, open_editing, save_editing, create_variant | own `LeaseScope.Run` per call |
| `prefab.apply` | `PrefabApplyCommands.cs` | 2 | create, instantiate, applyOverrides, editProperty, createVariant | own `LeaseScope.Run` for whole batch |
| `asset.import` / `asset.set_import_settings` / `asset.set_clip_import_settings` | `AssetCommands.cs` | 2 | import, set_import_settings, set_clip_import_settings | own `LeaseScope.Run` per call |
| `asset.manage` | `AssetManageCommands.cs` | 2 | move, import, refresh | own `LeaseScope.Run` for whole batch |
| `scene.manage` | `SceneManageCommands.cs` | 2 | save, create, open, duplicate | own `LeaseScope.Run` for whole batch |
| `projectSettings.apply` | `ProjectSettingsApplyCommands.cs` | 2 (mixed) | createTag, deleteTag, createLayer, setBuildScenes, setImportSettings, setClipImportSettings | own `LeaseScope.Run` for whole batch |
| `scene.open` | `ProjectCommands.cs` | 2 | (single op) | own `LeaseScope.Run` |

Not covered below as "mutation" rows: `assets.refresh`/`lease.*` (bookkeeping, not user content -
`CommandTable.cs:31-34`), `project.recompile_scripts`/`project.run_tests`/`hades.regression_replay`
(trigger/observe, do not themselves write project content), `project.begin_script_editing`/
`project.end_script_editing`/`hades.regression_record_start`/`stop` (session bookkeeping around
out-of-band script edits, not a validated wire payload), `inspector.inspect`/`project.get_console_log`/
`project.get_test_results` (pure reads).

**Reuse map** - the batch tools are overwhelmingly thin dispatchers over the per-subject files, which
is exactly why a guard fixed once in the right place closes several rows at once (see Table 6):

- `scene.apply`'s ops call `ComponentCommands`/`SceneCommands`/`InspectorCommands` directly for 9 of
  11 ops; only `create` and `setProperties` are reimplemented (`SceneApplyCommands.cs:35-52` explains
  why: `scene.setup` opens its own Undo group and is unsafe to call mid-batch).
- `prefab.apply`, `material.apply`, `animation.apply`, `asset.manage`, `scene.manage`,
  `projectSettings.apply` all call the underlying per-subject method (or its lease-free `DoXxx` core)
  directly - zero reimplemented validation logic in any of the six.

---

## Table 1 - Path normalisation

Is a caller-supplied **asset path** canonicalised and confined to `Assets/...` (traversal, absolute
paths, non-normalised `./`, doubled slashes, per-component length)? "N/A" = operation takes no asset
path (a scene GameObject path, a tag/layer name, etc. - not this table's concern).

| Op | Guard | Citation |
|---|---|---|
| `material.create` (path) | `AssetPathGuard.RequireNewAssetPath` | `MaterialCommands.cs:39-40` |
| `material.duplicate` (destPath) | `AssetPathGuard.RequireNewAssetPath` | `MaterialCommands.cs:160-161` |
| `material.set_property`/`assign`/`swap_shader` (materialPath) | N/A - existing-asset lookup only, see Table 2 | - |
| `animation.create_controller` (path) | `AssetPathGuard.RequireNewAssetPath` | `AnimationCommands.cs:117` |
| `animation.assign_controller`/`assign_clip`/`edit_controller` (path) | N/A - existing-asset lookup only | - |
| `scene.create` (path) | `AssetPathGuard.RequireNewAssetPath` | `SceneManagementCommands.cs:65` |
| `scene.duplicate` (destPath) | `AssetPathGuard.RequireNewAssetPath` | `SceneManagementCommands.cs:102` |
| `scene.save` (path, optional Save-As) | `AssetPathGuard.RequireWellFormedProjectPath` (existence deliberately NOT required - see Table 3) | `SceneManagementCommands.cs:54` |
| `prefab.create` (assetPath) | `AssetPathGuard.RequireNewAssetPath` | `PrefabCommands.cs:104` |
| `prefab.create_variant` (variantPath) | `AssetPathGuard.RequireNewAssetPath` | `PrefabCommands.cs:511` |
| `prefab.instantiate`/`edit_property`/`apply_overrides`/`open_editing` (prefabPath) | N/A - existing-asset lookup only | - |
| `asset.move` (destPath) | `AssetPathGuard.RequireWellFormedProjectPath` | `AssetCommands.cs:51` |
| `asset.move` (sourcePath) | N/A by design - resolved through `AssetDatabase.GetMainAssetTypeAtPath`, an AssetDatabase-internal lookup that cannot resolve outside `Assets/` regardless (never a raw filesystem check) | `AssetCommands.cs:53-54` |
| `asset.import` (path) | **WAS UNGUARDED - FIXED THIS PASS.** `AssetPathGuard.RequireWellFormedProjectPath` | `AssetCommands.cs:108` |
| `asset.set_import_settings` (path) | **WAS UNGUARDED - FIXED THIS PASS.** `AssetPathGuard.RequireWellFormedProjectPath` | `AssetCommands.cs:166` |
| `asset.set_clip_import_settings` (path) | **WAS UNGUARDED - FIXED THIS PASS.** `AssetPathGuard.RequireWellFormedProjectPath` | `AssetCommands.cs:239` |
| `component.*`/`reference.set`/`event.*` (gameObjectPath/targetPath) | N/A - scene-hierarchy path, not an asset path; resolved via `GameObjectPaths.FindByPath` (scene-graph walk, cannot escape the scene) | `SceneCommands.cs:327-348` |
| `tag.create`/`tag.delete`/`layer.create` (name) | N/A - a name, not a path | - |
| `scene.set_build` (scenes[].path) | N/A - `AssetDatabase.LoadAssetAtPath` lookup only, same reasoning as asset.move's sourcePath | `SceneManagementCommands.cs:142` |

**Finding (closed this pass):** `asset.import`, `asset.set_import_settings`, and
`asset.set_clip_import_settings` each read a caller-supplied `path` and, before this pass, resolved
it straight through `AssetImporter.GetAtPath` (importer lookups) or - for `asset.import` specifically
- a **raw, unconfined `File.Exists`/`Directory.Exists` filesystem check** (`ToAbsolutePath`, itself
just a `Path.Combine` with no traversal defence). This is the exact hazard class
`AssetPathGuard`'s own doc comment describes as `F16`/`F17`, closed for the create-family in an
earlier round but never extended to these three siblings - discovered by reading
`AssetPathGuard.cs`'s own "which tools" list against the real call sites (9 call sites across 5
files before this pass; these three were the only class-2 write-path tools absent from it - now 12
across 5 files). See Table 8, gap #2.

---

## Table 2 - Target type

Does the operation verify the target is the kind of thing it expects (e.g. refusing to write a
material over a prefab, or edit a GameObject that isn't a prefab instance)?

| Op | Mechanism | Citation |
|---|---|---|
| `material.create`/`duplicate` | `AssetPathGuard.RequireNewAssetPath`'s existence check refuses ANY existing asset regardless of type (material, prefab, anything) | `MaterialCommands.cs:39,160`; guard: `AssetPathGuard.cs:113-125` |
| `material.set_property`/`assign`/`swap_shader` | Typed `AssetDatabase.LoadAssetAtPath<Material>` - wrong type resolves null, refused | `MaterialCommands.cs:73,133,192` |
| `material.assign` (Renderer target) | `GameObjectPaths.RequireComponent(go, typeof(Renderer), ...)` | `MaterialCommands.cs:132` |
| `animation.assign_controller` | Typed `LoadAssetAtPath<RuntimeAnimatorController>` (deliberately the base type - accepts an override controller too) | `AnimationCommands.cs:43-45` |
| `animation.assign_clip`/`edit_controller` | Typed `LoadAssetAtPath<AnimatorController>` (concrete type - state editing needs it) | `AnimationCommands.cs:75-76,212-213` |
| `animation.create_controller` | `RequireNewAssetPath`'s existence check, same as material | `AnimationCommands.cs:117` |
| `scene.create`/`duplicate` | Typed `LoadAssetAtPath<SceneAsset>` + `RequireNewAssetPath` | `SceneManagementCommands.cs:65,72,102,104` |
| `scene.open`/`scene.set_build` | Typed `LoadAssetAtPath<SceneAsset>` | `ProjectCommands.cs:86`; `SceneManagementCommands.cs:142` |
| `prefab.create`/`instantiate`/`edit_property` | Typed `LoadAssetAtPath<GameObject>` | `PrefabCommands.cs:106,133,358` |
| `prefab.apply_overrides` | `PrefabUtility.IsPartOfPrefabInstance(go)` - explicit, not merely a typed load | `PrefabCommands.cs:218-223` |
| `prefab.create_variant` | Typed `LoadAssetAtPath<GameObject>` on base + `RequireNewAssetPath` on variant | `PrefabCommands.cs:511,513` |
| `component.add`/`remove`/`set_property`/`set_properties` | `ComponentTypes.Find` + `GameObjectPaths.RequireComponent` (fake-null-safe) | `ComponentCommands.cs:39,57,79,140-155`; helper: `SceneCommands.cs:414-419` |
| `reference.set` | Explicit `fieldType.IsInstanceOfType(targetObj)` check with an actionable "expects X, target is Y" message - the MOST thorough target-type check in the codebase | `ComponentCommands.cs:272-281` |
| `event.add_listener`/`remove_listener` | `FindUnityEventField` requires the field to be `UnityEventBase`-assignable | `ComponentCommands.cs:431-434` |
| `asset.set_clip_import_settings` | Explicit `as ModelImporter` cast with a "uses X, not ModelImporter" message naming the actual type found | `AssetCommands.cs:244-250` |
| `asset.set_import_settings` | Generic over any `AssetImporter` subtype by design (works via `SerializedObject`, no importer-specific handling needed) - not a gap, a deliberate scope choice | `AssetCommands.cs:213-214` |
| `asset.move` | `AssetDatabase.GetMainAssetTypeAtPath(sourcePath)` only checks *something* exists, not a specific type - correct, since move is type-agnostic by design | `AssetCommands.cs:53-54` |
| `tag.create`/`delete`/`layer.create` | N/A - operates on ProjectSettings string arrays, no "target type" concept applies | - |

No gap found in this dimension beyond what's already listed as fixed in Table 1 (existence doubles
as target-type protection for every create-family tool, since `RequireNewAssetPath` refuses
overwriting an asset of ANY type, not just a same-type collision).

---

## Table 3 - Existence (create-over-existing / edit-of-missing)

| Op | Create-over-existing | Edit-of-missing | Citation |
|---|---|---|---|
| `material.create` | Refused (`RequireNewAssetPath`) | N/A (create op) | `MaterialCommands.cs:39` |
| `material.duplicate` | Refused (`RequireNewAssetPath`); source-missing also refused | N/A | `MaterialCommands.cs:160,163-164` |
| `material.set_property`/`assign`/`swap_shader` | N/A (edit ops) | Clear `ArgumentException` via `MaterialNotFoundError` | `MaterialCommands.cs:303-305` |
| `animation.create_controller` | Refused (`RequireNewAssetPath`) | N/A | `AnimationCommands.cs:117` |
| `animation.assign_controller`/`assign_clip`/`edit_controller` | N/A | Clear `ArgumentException` naming the path | `AnimationCommands.cs:43-45,75-78,212-213` |
| `scene.create`/`duplicate` | Refused (`RequireNewAssetPath`) | N/A | `SceneManagementCommands.cs:65,102` |
| `scene.save` (path, Save-As) | **Deliberately allowed** - Save-As legitimately overwrites, including the currently-open scene's own path; documented exception to the create-family rule | N/A | `SceneManagementCommands.cs:50-53` |
| `prefab.create` | Refused (`RequireNewAssetPath`) | N/A | `PrefabCommands.cs:104` |
| `prefab.create_variant` | Refused (`RequireNewAssetPath`) | N/A | `PrefabCommands.cs:511` |
| `prefab.instantiate`/`edit_property`/`apply_overrides`/`open_editing` | N/A | Clear `ArgumentException` (`PrefabNotFoundError` / inline) | `PrefabCommands.cs:133,358-359,216,434-435` |
| `component.add` | N/A (Unity itself refuses a second `[DisallowMultipleComponent]` instance; ordinary components may have several by design - not this plugin's concern) | - | - |
| `component.remove`/`set_property` | N/A | Clear `ArgumentException` via `RequireComponent`/`GameObjectPaths.NotFoundError` | `ComponentCommands.cs:56-58,78-80` |
| `asset.move` | N/A (rename/move, not create) - destination collision is refused by `AssetDatabase.MoveAsset` itself (returns a non-empty error string, converted to `ArgumentException`) | source-missing refused | `AssetCommands.cs:53-54,72-74` |
| `asset.import` | N/A (re-import, not create) | Clear `ArgumentException`, now reached only after the path guard (Table 1) | `AssetCommands.cs:113-118` |
| `asset.set_import_settings` | N/A | Clear `ArgumentException` ("No importer found") | `AssetCommands.cs:171-172` |
| `asset.set_clip_import_settings` | N/A | Clear `ArgumentException` (unknown asset / wrong importer type / no clips) | `AssetCommands.cs:244-259` |
| `tag.create` | Refused - explicit duplicate-name scan | N/A | `TagLayerCommands.cs:44-48` |
| `tag.delete` | N/A | Clear `ArgumentException` listing existing tags | `TagLayerCommands.cs:83-89` |
| `layer.create` (slot collision) | Refused - explicit occupied-slot check (pre-existing) | N/A | `TagLayerCommands.cs:124-129` |
| `layer.create` (**name** collision) | **WAS UNGUARDED - FIXED THIS PASS.** Refused - explicit duplicate-name scan across all 32 slots, mirroring `tag.create`'s own check | N/A | `TagLayerCommands.cs:103-113` |
| `scene.set_build` | N/A (replaces the whole list, not a per-item create) | Missing scenes collected and refused together | `SceneManagementCommands.cs:148-152` |

**Finding (closed this pass):** `layer.create` refused a collision on its target **slot**
(`layerIndex`) but never checked whether `name` already labelled a **different** slot - so two
layers could silently share a name, leaving `LayerMask.NameToLayer(name)` to resolve ambiguously
between them. `tag.create`, right above it in the same file, already had the analogous duplicate-
*name* check. See Table 8, gap #1.

---

## Table 4 - Self-reference / cycles

| Op | Hazard | Guard | Citation |
|---|---|---|---|
| `scene.reparent_gameobject` (and `scene.apply`'s `reparent` op, which calls it directly) | Reparent under self or own descendant | `IsSelfOrDescendant` walk up the candidate's parent chain (tagged `F21`) | `SceneCommands.cs:109-117,307-312` |
| `prefab.create_variant` (and `prefab.apply`'s `createVariant` op, which calls it directly) | `basePrefabPath == variantPath` would silently overwrite the base with its own variant | Explicit equality check (tagged `F21`), checked BEFORE the existence guard so it gets its own precise message | `PrefabCommands.cs:502-510` |
| `asset.move` (and `asset.manage`'s `move` op) | Move onto own exact path (no-op self-move); move a **folder** to a path inside itself (a hierarchy cycle with no sensible outcome - the exact same hazard class as the scene-reparent case above, never previously considered for the AssetDatabase path hierarchy) | **WAS UNGUARDED - FIXED THIS PASS.** Explicit `destPath == sourcePath \|\| destPath.StartsWith(sourcePath + "/")` check | `AssetCommands.cs:56-68` |
| `material.duplicate`/`scene.duplicate` (source == dest) | Refused transitively - the source (by definition, already loaded successfully) already exists at that exact path | `AssetPathGuard.RequireNewAssetPath`'s existence check, not a dedicated self-check | `MaterialCommands.cs:160-164`; `SceneManagementCommands.cs:100-104` |
| `component.set_property`/`reference.set` (a field referencing its own GameObject/component) | Not a structural hazard - a component legitimately referencing itself (e.g. a script holding its own `Transform`) is valid Unity content, not a cycle to refuse | N/A - reviewed, not a gap | - |
| `prefab.apply_overrides` (instance whose own source prefab is itself, transitively) | Not reachable - `PrefabUtility` construction guarantees a prefab instance's source is never itself | N/A - reviewed, not a gap | - |

**Finding (closed this pass):** `asset.move`'s destination guard checked path well-formedness (Table
1) but never checked whether the destination was the source itself or nested inside it. This is
structurally the identical hazard the `F21` scene-reparent fix closed for the GameObject hierarchy,
just never revisited for the AssetDatabase path hierarchy - exactly the "look wherever a check
currently exists in exactly one place" pattern this audit was commissioned to find. See Table 8, gap
#3.

**Coverage note (no code change, closed a blind spot in test coverage only):** `scene.apply`'s
`reparent` op and `prefab.apply`'s `createVariant` op both inherit their guard correctly by
DELEGATING to the already-guarded method (see Table 0's reuse map) - verified by reading
`SceneApplyCommands.DoReparent` (`SceneApplyCommands.cs:361-368`) and
`PrefabApplyCommands.DispatchOne`'s `createVariant` case (`PrefabApplyCommands.cs:167-168`). Neither
was previously pinned by a regression test AT the batch-tool layer, only one level down against the
underlying method directly - so a future refactor that "inlined" either op for performance could
silently reintroduce the exact regression the external tester's report described, with nothing in
this repository's test suite noticing. Regression tests were added this pass (see Table 8, gap #5).

---

## Table 5 - Lease/undo behaviour: claim vs. code

Does the operation open its own Undo group, and does its doc comment's claim match what the code
does? ("Self-manages" = wraps itself in `LeaseScope.Run` + `Undo.IncrementCurrentGroup` rather than
relying on `CommandTable.Dispatch`'s pre-increment.)

| Family | Claim | Verified against code | Citation |
|---|---|---|---|
| `scene.apply`/`material.apply`/`animation.apply` | Registered `MutatingMethods` entry; own leading increment collapses harmlessly with `Dispatch`'s pre-increment | Matches - each opens exactly one group at the top of `Apply`, names it at the bottom | `CommandTable.cs:170-182`; `SceneApplyCommands.cs:93,117`; `MaterialApplyCommands.cs:70,94`; `AnimationApplyCommands.cs:63,87` |
| `prefab.apply`/`asset.manage`/`scene.manage`/`projectSettings.apply` | NOT a `MutatingMethods` entry; self-manages one lease + one Undo group for the whole batch | Matches - each wraps its loop in exactly one `LeaseScope.Run`, increments once immediately after | `PrefabApplyCommands.cs:109,115`; `AssetManageCommands.cs:73,80`; `SceneManageCommands.cs:77,84`; `ProjectSettingsApplyCommands.cs:90,97` |
| `prefab.apply`'s Undo coverage | "Most of the five ops mutate an asset on disk, not part of the Undo stack at all... the group still opens uniformly" | Matches - `instantiate` is the only op that touches the scene; the doc comment does not overclaim revert coverage for the other four | `PrefabApplyCommands.cs:39-53` |
| `scene.manage`/`projectSettings.apply`'s Undo coverage | Explicitly documented as UNEVEN (some ops Undo-tracked, some are static-property/project-config writes with no Undo primitive at all) | Matches - `SceneManagementCommands.SetBuildScenes` and `AssetCommands`' two import-settings cores use `ApplyModifiedPropertiesWithoutUndo`/no Undo call at all, exactly as claimed | `SceneManageCommands.cs:43-54`; `ProjectSettingsApplyCommands.cs:48-64`; `SceneManagementCommands.cs:158-161`; `AssetCommands.cs:200` (`ApplyModifiedPropertiesWithoutUndo`), `AssetCommands.cs:291` (`clipAnimations` write, no Undo call of any kind) |
| `tag.create`/`delete`/`layer.create` | "Best-effort, not a tested claim" - deliberately does NOT assert Undo reliably reverts a ProjectSettings/TagManager.asset write | Matches - `Undo.RecordObject` is called, but no `PerformUndo`-revert test exists for this family (by design, per its own doc comment) | `TagLayerCommands.cs:14-22`; confirmed absent in `TagLayerCommandsTests.cs` |
| `asset.move` | "No Undo here - deliberately" (a path is not a serialized field `Undo.RecordObject` can snapshot) | Matches - no Undo call anywhere in `MoveAsset` | `AssetCommands.cs:37-42` |
| `PrefabCommands` class-2 handlers | An exception mid-operation must never leave the lease held (opposite of class-3 `BeginScriptEditing`) | Matches - `LeaseScope.Run`'s `finally` releases unconditionally | `PrefabCommands.cs:583-590` |
| `CommandTable.Dispatch`'s pre-increment | "Every mutating call now starts its own fresh group... a batch tool's own internal increment... produces one harmless, empty leading group" | Matches - proven directly by `CommandTableUndoGroupingTests.cs` (includes 2 new regression tests added this session by a sibling agent for the 3-object/two-consecutive-batch cases) | `CommandTable.cs:184-220` |

No claim-vs-code mismatch found in this dimension. This is the one dimension where the codebase's
own doc-comment discipline ("verified against a real Editor rather than assumed", repeated
throughout) already holds up under a literal line-by-line check.

---

## Table 6 - Failure reporting shape

Per-op error entries vs. whole-batch throw; does a partial batch report which ops landed?

| Family | Shape | Vocabulary | Citation |
|---|---|---|---|
| `scene.apply` | Per-op try/catch, continue | `applied` (indices) / `failed` (array of `{index,op,error}`) / `summary` - no `results` array (ops are fire-and-forget mutations with nothing to report beyond success/failure) | `SceneApplyCommands.cs:84-124` |
| `prefab.apply`/`material.apply`/`animation.apply`/`asset.manage`/`scene.manage`/`projectSettings.apply` | Per-op try/catch, continue | `applied` / `failed` / **`results`** (array of `{index,op,result}` - each op's own success payload rides along) / `summary` | `PrefabApplyCommands.cs:117-150`; `MaterialApplyCommands.cs:63-101`; `AnimationApplyCommands.cs:56-94`; `AssetManageCommands.cs:82-115`; `SceneManageCommands.cs:86-119`; `ProjectSettingsApplyCommands.cs:99-157` |
| `scene.setup` | Per-entry try/catch, continue | `results` (array of PER-ENTRY SUCCESS detail) / **`errors`** (not `failed`) / `summary` | `SceneCommands.cs:153-169` |
| `component.set_properties` | Per-op try/catch, continue | `results` (nested per-op `applied`/`failed`) / **`errors`** (top-level only) / `summary` | `ComponentCommands.cs:117-174` |
| `asset.set_import_settings` | Per-property try/catch, continue | `applied` (array of PROPERTY NAMES, not indices) / `failed` (array of `{property,error}`) | `AssetCommands.cs:159-208` |
| `asset.set_clip_import_settings` | Per-clip try/catch, continue | `applied` (array of CLIP NAMES, not indices) / `failed` (array of `{clip,error}`) - **CONVERGED THIS PASS** onto `asset.set_import_settings`'s own vocabulary, one row up (was `updatedClips`/`errors`, a pair of names found nowhere else in this codebase) | `AssetCommands.cs:234-299` |
| `hades.regression_replay` | Per-call try/catch, continue | `results` / `passed` / `failed` (counts, not arrays) / `total` | `ProjectCommands.cs:359-404` |

**Finding (measured this pass - narrowed and partially closed; see Table 8, gap #4):** the original
framing - "four genuinely different vocabularies... side by side" - overstated how much of this a
live caller can actually reach. Two things were true and are now disambiguated:

**The outer batch envelope was already uniform (no fix needed).** All seven `*.apply`/`*.manage`
families answer with `applied`/`failed`/`summary`, six of seven also with `results`; `scene.apply`
correctly omits `results` (`DispatchOne` is `static void` - no per-op payload exists to echo). See
the two rows above.

**Only ONE of the "four vocabularies" was reachable through a live, purpose-built MCP tool - and it
is now converged.** `scene.setup`, `component.set_properties`, `asset.set_import_settings`, and
`asset.set_clip_import_settings` are invoked by none of the 32 tools `Program.cs:123-138` registers -
confirmed by `grep -rl '"scene.setup"' Core/src/` (and the same grep for the other three), each
returning zero files, versus one file each for a reachable command like `"scene.apply"`. Both
`asset_set_import_settings`/`asset_set_clip_import_settings` and `scene_setup`/
`component_set_properties` were deliberately removed as standalone MCP tools - the first pair
confirmed by `EditorAssetTools.cs:6-12`'s own doc comment ("Plan 10 Task 6 removed this file's four
MCP tools... No `[McpServerToolType]` class is left in this file for `Program.cs` to register"), the
second pair superseded by `scene_apply` (`SceneApplyTool.cs:88-92`). The ONLY place a live,
documented caller still observes `asset.set_import_settings`'s or `asset.set_clip_import_settings`'s
own return shape is nested one level down, inside `project_settings_apply`'s `results[].result`
(`ProjectSettingsApplyCommands.cs:179-182` calls their lease-free cores directly) - and THERE the two
disagreed with each other for no reason tied to properties vs. clips: `asset.set_import_settings`
answered `{path, applied, failed}` (`failed`: `{property,error}` objects), while
`asset.set_clip_import_settings` answered `{path, updatedClips, errors}` (`errors`: bare strings with
no clip identifier a caller could act on without parsing prose). Nothing about clips vs. properties
requires that difference - both are "one op's own list of named sub-outcomes" - so it was incidental,
not intrinsic, and is now CONVERGED: `asset.set_clip_import_settings` answers `{path, applied,
failed}` too, `failed` now `{clip,error}` objects (`clip` is JSON `null`, never omitted, when an
entry never named one - the convention `ProjectSettingsApplyCommands.cs:120`'s own per-op `failed`
entries and `ComponentCommands.cs:212-218`'s `TopLevelError` already use). Fixed in
`AssetCommands.cs:234-299` (plugin emitter) and `ProjectSettingsApplyTool.cs:53-62` (doc only - the
app side already treats each op's `result` as an opaque passthrough via `WireJsonBridge.ToClr`, so no
parsing logic needed to change); `ProjectSettingsApplyTests.cs:102-105`'s simulated wire payload was
updated to match so the fixture does not silently document a shape that no longer exists.

**`scene.setup`/`component.set_properties`'s own vocabulary is the residue left open - narrower than
originally scoped, and not literally dead code.** Neither is wrapped by any of the 32 registered
tools, and neither is folded as a nested payload into a live batch's `results[]` the way the two
import-settings ops were (`scene.apply` REIMPLEMENTS `create`/`setProperties` rather than delegating
to `scene.setup`/`component.set_properties` - see Table 0's reuse map, `SceneApplyCommands.cs:35-52`).
But they are not unreachable at the wire-protocol level: both remain registered
`CommandTable.Handlers` entries (`CommandTable.cs:46,52`), and `hades_regression`
(`EditorProjectTools.cs:457`, one of the 32 registered tools) accepts a `replay` `calls` array where
any entry with no `'format'` field is forwarded as a UnityPlugin wire method name with NO allowlist
restricting it to a currently-documented command (`EditorProjectTools.cs:520-536` routes exactly this
shape to `ReplayLegacyBatchAsync`, `EditorProjectTools.cs:580-598`, which sends whatever `Method`
string the caller supplied straight into one `hades.regression_replay` wire call -
`ProjectCommands.RegressionReplay`, `ProjectCommands.cs:353-398`, dispatches it via
`CommandTable.Dispatch` unchanged). Concretely: `hades_regression(action:'replay', calls:[{"method":
"scene.setup", "params": {...}}])` (no `format` key) reaches `SceneCommands.SceneSetup` today, live -
this needs no pre-existing recording, only the wire method name (documented in this very source
tree's own comments). The shipped fixture does not currently do this - verified directly
(`scripts/regression/fixtures/editor-routed.json` contains only `"projectSettings.apply"` and
`"scene.apply"` as `method` values) - but an empty fixture is not the same claim as a closed path.
So: no purpose-built caller-facing surface exposes `scene.setup`/`component.set_properties`'s
vocabulary, but the generic regression-replay side channel still can. And that side channel is
precisely why converging them would be actively WRONG, not merely unnecessary: replay verifies a
recorded call by comparing its response to the recorded `expected` with **exact member-count
equality** (`JsonValueEquals`' object case opens `if (a.Members.Count != b.Members.Count) return
false;` - `ProjectCommands.cs:432`), so renaming `errors`->`failed` or adding an `applied` key to
either command would fail replay of every existing recording that captured the old shape. These
two commands are kept alive BY the replay path; changing their vocabulary would break the one
thing that still reaches them. Converging those two onto the
newer `applied`/`failed`/`results`/`summary` shape remains **out of scope for this pass** and is, on
the measured evidence above, optional busywork on internal-only surface unless/until that surface is
either formally deprecated (removing the `CommandTable` entries too - a product-timing call, not an
engineering one) or deliberately kept and documented as reachable. Recorded here so the next person
who touches either side has the map.

One structural note, not a numbered gap: `scene.apply`'s partial-failure model can report an
operation as FAILED after it partially mutated state (documented explicitly:
`SceneApplyCommands.cs:54-64` - e.g. `setProperties` applies every property that DID resolve, then
still throws if a sibling property failed, landing the whole op in `failed`). This is the mirror
image of "reports success for work that didn't happen" - here it is "reports failure for work that
DID happen" - and it is consistent across `scene.apply`/`scene.setup`/`component.set_properties`
(all three share the same `ApplyModifiedProperties`-if-anything-succeeded-then-throw pattern), so it
is even, not uneven. Listed here only so a reader does not mistake the pattern for a new finding.

---

## Table 7 - Undo/lease dimension cross-check: per-op micro-table

For the three registered `CommandTable.MutatingMethods` batch families, confirming the "one call, one
reload window, one Undo group" property holds per OP, not just per FAMILY (i.e. no op inside a batch
independently opens a second lease/group):

| Family | Op | Opens its own lease/group independently? | Citation |
|---|---|---|---|
| `scene.apply` | `create` | No - reimplemented inline, never calls `scene.setup` | `SceneApplyCommands.cs:157-211` |
| `scene.apply` | `setProperties` | No - reimplemented inline | `SceneApplyCommands.cs:247-280` |
| `scene.apply` | every other op | No - calls the underlying class-1 handler directly, which never touches Undo grouping itself | `SceneApplyCommands.cs:215-386` |
| `animation.apply` | `editController` | No - calls `AnimationCommands.DoEditController` (the group-free core), never `EditController` (which would open a second group) | `AnimationApplyCommands.cs:129-134`; core split at `AnimationCommands.cs:184-194` |
| `prefab.apply` | every op | No - calls each `PrefabCommands.DoXxx` lease-free core directly, never the self-leasing wrapper | `PrefabApplyCommands.cs:154-173` |

No gap found - this is the dimension Plan 10's own design most directly targeted, and it holds.

---

## Table 8 - Ranked gap list

Ranked by risk, per the audit's own rubric: **Destructive** (could lose/overwrite user data) >
**Correctness** (accepts input producing a broken/cyclic asset, or reports success for work that
didn't happen) > **Consistency/messaging** (safe but differs from a sibling for no reason).

| # | Tier | Gap | Tool(s) affected | Status |
|---|---|---|---|---|
| 1 | Correctness | `layer.create` checked its target SLOT for a name collision but never the NAME itself across other slots - two layers could silently share a name (sibling `tag.create`, same file, already refuses this for tags) | `layer.create` (+ `projectSettings.apply`'s `createLayer` op, which calls it directly) | **FIXED** - `TagLayerCommands.cs:103-113`; test `CreateLayer_DuplicateNameAtDifferentIndex_ThrowsActionableError` in `TagLayerCommandsTests.cs` |
| 2 | Destructive (defense-in-depth) | `asset.import`/`asset.set_import_settings`/`asset.set_clip_import_settings`'s `path` was never routed through `AssetPathGuard` - `asset.import` specifically reached a raw, unconfined `File.Exists`/`Directory.Exists` filesystem check before anything refused a traversal path | `asset.import`, `asset.set_import_settings`, `asset.set_clip_import_settings` (+ `asset.manage`'s `import` op and `projectSettings.apply`'s `setImportSettings`/`setClipImportSettings` ops, all three of which call the same now-fixed core) | **FIXED** - `AssetCommands.cs:108,166,239`; tests `ImportAsset_TraversalPath_RefusedBeforeAnyFilesystemCheck_StillReleasesLease`, `SetImportSettings_TraversalPath_RefusedBeforeAnyWork_StillReleasesLease`, `SetClipImportSettings_TraversalPath_RefusedBeforeAnyWork_StillReleasesLease` in `AssetCommandsTests.cs` |
| 3 | Destructive | `asset.move` never checked whether `destPath` was the source itself or a path nested inside it - the exact same hierarchy-cycle hazard the `F21` scene-reparent fix closed for GameObjects, never extended to the AssetDatabase folder hierarchy | `asset.move` (+ `asset.manage`'s `move` op, which calls it directly) | **FIXED** - `AssetCommands.cs:56-68`; tests `MoveAsset_SourceEqualsDestination_RefusedBeforeAnyWrite_NoLeaseTouched`, `MoveAsset_FolderIntoOwnDescendant_RefusedBeforeAnyWrite_OriginalFolderIntact`, `MoveAsset_SiblingWithSimilarPrefix_NotTreatedAsDescendant_MovesNormally` (false-positive guard) in `AssetCommandsTests.cs` |
| 4 | Consistency/messaging | Partial-batch-success vocabulary diverges across mutating tools (see Table 6) - originally scoped as "four genuinely different vocabularies" reachable side by side; measured this pass as ONE reachable divergence (now fixed) plus one orphaned, not-purpose-built-but-not-dead residue | `asset.set_import_settings`/`asset.set_clip_import_settings` - now converged, both as `project_settings_apply`'s nested `results[].result` payload (the only live, documented path to either) and as their own orphaned standalone wire commands, since both forms share one core - vs. `scene.setup`/`component.set_properties`, still divergent from each other and from the batch families, wrapped by none of the 32 registered MCP tools but still dispatchable via `hades_regression`'s generic legacy-wire replay (no allowlist) | **NARROWED AND PARTIALLY CLOSED.** The one instance reachable through the documented 32-tool surface - `setImportSettings`/`setClipImportSettings`'s nested result inside `project_settings_apply` - is FIXED: both now answer `{path, applied, failed}` with `failed` as `{property\|clip, error}` objects - `AssetCommands.cs:234-299`; `ProjectSettingsApplyTool.cs:53-62`; `ProjectSettingsApplyTests.cs:102-105`. `scene.setup`/`component.set_properties`'s own vocabulary is UNCHANGED, left open, and recommended NOT to touch this pass: no purpose-built tool exposes it (zero `Core/src/` grep hits for either wire method name, against `Program.cs:123-138`'s 32 registered tools), so converging it is optional busywork on internal-only surface, not a caller-facing fix - but it is not dead code (`EditorProjectTools.cs:520-598`'s legacy replay path can still dispatch either by wire method name, no allowlist, no recording required). Converging it would in fact BREAK that path: replay compares responses to recorded `expected` by exact member count (`ProjectCommands.cs:432`), so any key rename or addition fails replay of every recording holding the old shape. Whether to delete the four orphaned `CommandTable` entries, close the `hades_regression` side channel, or keep both as documented legacy-internal surface is a product-timing call this audit defers to the product owner, not an engineering decision to make unilaterally days before a release. |
| 5 | Consistency/messaging | `scene.apply`'s `reparent` op and `prefab.apply`'s `createVariant` op both correctly inherit their F21 cycle guards via delegation, but neither was pinned by a regression test at the batch-tool layer - a future "inline this for performance" refactor could silently reintroduce the exact regression class the external tester originally reported | `scene.apply` (reparent op), `prefab.apply` (createVariant op) | **FIXED (coverage only, no behaviour change)** - `ReparentOp_UnderItself_RecordedAsOperationFailure_NotSilentlyAccepted` in `SceneApplyCommandsTests.cs`; `CreateVariantOp_BaseEqualsVariant_RecordedAsOperationFailure_BasePrefabUntouched` in `PrefabApplyCommandsTests.cs` |

No gap was found in Table 2 (target type), Table 5 (lease/undo claim-vs-code), or Table 7
(per-op lease/group isolation within a batch) beyond what is listed above - those three dimensions
were audited with the same rigor and came back clean.

---

## Summary for the next reviewer

- **Shared guard reused, not reinvented:** every fix in this pass calls `AssetPathGuard`'s EXISTING
  methods (`RequireWellFormedProjectPath`) or extends a single-tool method to check a case
  `SceneCommands.ReparentGameObject`'s own `F21` guard already established the PATTERN for
  (self/descendant). No new shared abstraction was introduced - `layer.create`'s duplicate-name check
  and `asset.move`'s cycle check are each single-call-site fixes; if a THIRD tool ever needs either
  check, that is the point to factor it into a shared helper, not before.
- **Fixing once closed multiple rows:** `asset.import`/`asset.set_import_settings`/
  `asset.set_clip_import_settings`'s path guard fix (gap #2) automatically also closes the same gap
  in `asset.manage`'s `import` op and `projectSettings.apply`'s `setImportSettings`/
  `setClipImportSettings` ops, since all three call the exact same `DoXxx` core method this pass
  edited - no separate fix was needed in `AssetManageCommands.cs` or `ProjectSettingsApplyCommands.cs`.
- **Gap #4 narrowed and partially closed in a follow-up pass:** the original scoping - "four
  genuinely different vocabularies... side by side" - overstated reachability. Measured directly,
  only `setImportSettings`/`setClipImportSettings`'s shape disagreement inside `project_settings_apply`
  was reachable through the documented 32-tool MCP surface, and it is now fixed with a coordinated
  `UnityPlugin` + `Core` change (`AssetCommands.cs`, `ProjectSettingsApplyTool.cs`,
  `ProjectSettingsApplyTests.cs`) - the "needs a coordinated Core-side change outside this audit's
  scope" blocker below no longer applies to that piece. `scene.setup`/`component.set_properties`'s
  own vocabulary is intentionally left UNconverged: no purpose-built MCP tool exposes either, so
  standardising them now is optional busywork on internal-only surface, not a caller-facing fix -
  see Table 6/Table 8 gap #4 for the reachability evidence, including the `hades_regression`
  legacy-replay side channel that keeps them from being literally dead code, and the delete-vs-keep
  call this audit defers to the product owner rather than making unilaterally.
