---
description: "Use when calling a Hades MCP tool and its terse tools/list description isn't enough — deeper per-tool detail, caveats, edge cases, and worked examples for graph_query, inspect_asset, find_unset_references, the batch apply/manage tools, and others. Load before a retry after an unexpected result, or when deciding between two similar tools."
---

# Hades Tool Reference

Hades' `tools/list` descriptions are kept short on purpose (payload size — an agent's context is spent on every tool on every turn). This skill holds the detail that was cut: rationale, edge cases, and vocabulary that matters less often than the description's core "what it does and how to call it." For known boundaries of what Hades can see at all (reflection, DI, dynamic instantiation, binary asset coverage, ...), see `LIMITATIONS.md` at the repo root — this skill does not repeat that page.

## Batch tools: shared result shape

`scene_apply`, `material_apply`, `prefab_apply`, `scriptable_object_apply`, `animation_apply`, `asset_manage`, `scene_manage`, and `project_settings_apply` all share one contract, trimmed from each tool's own description to avoid repeating it eight times:

- Every operation in the array is attempted in order. An unrecognised `op` value, or a field not valid for the op it's attached to, refuses the **whole call** before anything reaches the Editor — nothing partially applies from a malformed batch.
- Once dispatched, failure is **per-operation, not transactional**: each op's outcome is reported by its 0-based index in `applied` (succeeded) or `failed` (with its own error). Operations that already succeeded are never rolled back because a later one failed — a partial result is a partially-applied batch, not an all-or-nothing unit.
- Every successful operation's own result data (a new asset's `guid`, `createLayer`'s assigned `index`, `swapShader`'s `survivedProperties`/`lostProperties`, ...) rides along in `results`, alongside the bare index in `applied` — never collapsed into a blanket success flag.
- Undo coverage is **uneven across tools and even across ops within one tool** — see each tool's own section below before assuming one Ctrl/Cmd+Z reverts a whole batch.

## graph_query

`graph_query` is Hades' single consolidated structured-search surface: every one-off "find X" question (orphan scripts, prefabs using a given component, assets of a given type, ...) is expressible as a combination of its filters rather than a dedicated tool. A few combinations are worth knowing by pattern:

- Finding scripts nothing references: `kind: "Class", edgeKind: "references", edgeDirection: "incoming", edgeAbsent: true`.
- Finding prefabs/scenes that use a given component type: a builtin kind matches directly (`kind: "Rigidbody"`); a MonoBehaviour resolves via `kind: "MonoBehaviour", edgeKind: "references", edgeTargetNamePattern: "<TypeName>", edgeTargetKind: "Class"`.
- Finding every file that references one specific asset (e.g. everything using one script): `edgeKind: "references", edgeTargetPath: "<path>"`.
- Whole-file classification (an asset's type as a FILE, not a graph node — the only way to enumerate every Scene/Prefab/ScriptableObject in the project): `fileType: "<Script|Scene|Prefab|Material|AnimatorController|ScriptableObject|Asset>"`, optionally with `pathPrefix`.

**`edgeAbsent` is an honest superset, not confirmed-dead-code.** `edgeAbsent: true` with `edgeKind: "references", edgeDirection: "incoming"` finds classes nothing references *in the graph* — but a class only ever instantiated from C# (`AddComponent<T>()`, `new`, a static utility call) has no incoming reference either, because code-level instantiation isn't tracked as a graph edge, only Unity YAML references are. Treat a hit as "worth checking by hand", not "safe to delete".

**`fileType` answers a different question than `kind`.** Script/Material/AnimatorController each map to exactly one node `kind` per file, so `kind: "Class"` mostly works for scripts — but Scene, Prefab, and ScriptableObject do not: a scene or prefab is many per-object nodes with no single summarising one, and a ScriptableObject instance shares MonoBehaviour's generic kind with an ordinary component. `fileType` sidesteps this by reading file identity instead of node identity — one hit per file, for any of the seven classified types — which is also why it's refused together with every node-level filter (`kind`, `kindPattern`, `namePattern`, `edgeKind`): mixing the two answers is a well-formed no-op, not a useful combination.

**A ScriptableObject asset's graph `kind` is `MonoBehaviour`, not `ScriptableObject`.** A `.asset` file is a `!u!114` YAML document, identical in node shape to a component. `graph_query(kind: "ScriptableObject")` will never find one — use `graph_query(fileType: "ScriptableObject")` or `search_by_name` instead.

**`minWidth`/`minHeight` silently exclude every texture whose header Hades could not read.** They compare against the *measured* source dimensions, which are `NULL` for any format outside PNG/JPEG/TGA/PSD (see `LIMITATIONS.md`) — and `NULL` never satisfies `>=`. So `{"kind": "Texture2D", "minWidth": 2049}` means "every texture Hades could measure that is wider than 2048px", not "every texture wider than 2048px". To tell a genuine absence from an unmeasurable one, drop the dimension filter and call `inspect_asset` on the hits — an unreadable header reports its reason by name rather than guessing a size.

**`assetSubtype` is an exact, case-sensitive match against the importer's own vocabulary.** A texture's is `Sprite`/`NormalMap`/`GUI`/`Default`/…; a model's rig is `Humanoid`/`Generic`/`Legacy`/`None`. There is no substring form and no way to enumerate the set — call `inspect_asset` on one asset you know is of the kind you want and copy the `assetSubtype` string out of its `introspection`. A guessed casing (`"sprite"`) is a well-formed query that matches nothing, not an error.

## inspect_asset

- `guid` is always present at the top level (from the file's `.meta`, `null` if not yet imported) alongside whatever structure/material/controller payload `path` alone produced — identity is available even when a hierarchy is too.
- For textures specifically, `maxTextureSize` in `introspection` is the import **ceiling** Unity was configured with, not the source image's actual size — a 64×64 icon and an 8192×8192 texture both report `2048` under default settings. `sourceWidth`/`sourceHeight` are the *measured* values read from the file's own header, and are `null` for a format Hades can't read a header from (see `LIMITATIONS.md`'s binary-header section for exactly which formats that covers).
- A MonoBehaviour whose script guid doesn't resolve still appears in a `target` result, with its raw guid and `missing: true`, rather than being silently dropped.
- A UnityEvent field's wired listeners (e.g. a Button's `m_OnClick`) are reported even when a given listener's own target is itself unset.
- A `property` value that is itself a reference (`{fileID, guid, type}`) resolves to a project-relative path the same way top-level references always do; an unresolved external guid or Unity's own null reference is reported plainly, never as an error.
- A legacy pre-2018.3 `Prefab:` YAML format file is reported as unsupported by name, not silently returned empty.

## find_unset_references

- Every occurrence is reported unfiltered, because Unity's serialized YAML doesn't record *why* a reference is empty — a field deliberately left optional and one simply forgotten are byte-for-byte identical on disk, so Hades can't tell them apart and won't guess. A field whose name does **not** start with `m_` is more likely a genuine, user-authored gap worth investigating first — `m_`-prefixed fields are Unity's own structural bookkeeping (e.g. `m_Father` unset on a root GameObject's Transform is correct: it has no parent).
- Whole-project mode (`path` omitted) finds UnityEvent fields with at least one wired listener, graph-served — an event with zero listeners, or whose every listener has an unset target, leaves no trace and won't appear here. `listenerCount` is a **lower bound**, not always exact. Call `inspect_asset` with `target`/`component` on one specific component for the complete, exact picture instead.

## scriptable_object_apply

- If `type` isn't found at all, the most likely cause is that the defining script was just written or edited and the Editor hasn't compiled it yet: call `project_recompile_scripts`, poll `hades_charon_status` until it reports attached, then retry.
- `[CreateAssetMenu]` is ignored entirely — not consulted for gating, defaults, or validation.
- Undo destroys `create`'s in-memory object but leaves the `.asset` **file** it wrote on disk. Re-running an identical batch after a partial failure will fail at `create` with "already exists" (the file from the first attempt is still there) — drop the `create` op and re-run `setProperties` alone, or delete the asset first with `asset_manage`.
- The resulting asset's graph Kind is `MonoBehaviour`, not `ScriptableObject` — see the `graph_query` section above.

## asset_manage

- `delete` moves the asset to the OS trash/recycle bin, never a permanent delete — recoverable outside Hades on macOS/Windows; Linux/headless recoverability is unverified. It refuses folders outright (delete their contents instead) and reports `failed`, never a false success, if the underlying move-to-trash call itself fails (a locked file, a VCS hold, ...) — nothing is deleted in that case.
- Call `find_references_to` before deleting an asset other content may reference, and `find_unset_references` afterward to find the holes a delete left behind.
- `duplicate` registers Undo for the new in-memory copy, but Ctrl/Cmd+Z only destroys that in-memory object — the copied file stays on disk, so finish undoing a duplicate with a `delete` op on the copy.

## prefab_apply

- `editProperty` is atomic (load, edit, save in one step) — there's no separate `open`/`save` pair, so this tool cannot leave a prefab stuck open the way an old open→edit→save sequence could. Changing several properties on the same prefab in one call means several `editProperty` ops, not one op with multiple values.
- The whole batch acquires and releases Unity's reload lock exactly once, not once per operation — prefab operations are lease-bounded, unlike scene/material/animation's lease-free class.
- `applyOverrides` always reports `unappliedProperties` in its own result: `PrefabUtility.ApplyPrefabInstance` can never write a prefab instance root's own name/position/rotation back to the prefab asset — this is permanent Unity behaviour, not an error, so check `results`, never assume `applied` alone means everything was written.

## material_apply

- `swapShader` reports `survivedProperties`/`lostProperties` in its own result — Unity silently drops any shader property the new shader doesn't declare, by name and type, and this is the only way to find out which values just vanished.
- Undo destroys `create`/`duplicate`'s in-memory Material object but leaves the `.mat` file on disk — same pattern as `scriptable_object_apply` and `asset_manage`'s `duplicate` above.

## project_settings

- `"layers"` returns all 32 slots by index, 0–31; an unused slot is a real, meaningful empty string **at its own index**, never omitted or compacted. Layer 8 being free is not the same as "layers end at 7" — always read by index, never assume the array truncates at the first gap.
- `"buildScenes"`: each entry's `enabled` flag means actually included in a build, not merely present in the Build Settings list.
- `"renderPipeline"` reports `"unknown"` only when a custom pipeline is configured but can't be positively identified — never guessed from a partial match, since Built-in/URP/HDRP differ enough that a guess would sometimes just be wrong.

## get_project_summary

- `appliedDefines` is built from `UNITY_EDITOR`, the Unity-version ladder read from `ProjectVersion.txt`, the project's own `scriptingDefineSymbols`, and every asmdef `versionDefine` whose named package resolves to a satisfying version — the **same set project-wide, for every file**. This is an approximation: Unity's real compiler uses a different set per assembly (asmdef), and Hades doesn't yet track file-to-asmdef membership. Code gated on a symbol outside this list (a platform define, a csc.rsp-only symbol, or a versionDefine keyed to a built-in Unity module rather than an installed package) is invisible to the graph entirely — not a false negative in a query, just absent.

## hades_rebuild_graph

- Waits its turn behind any other indexing already running for this project before it starts running itself.
- Hades' incremental sync already keeps the graph current on its own during normal use — call this only when you suspect the graph has actually drifted from disk, not as routine maintenance.

## hades_regression

- A tool whose result varies between calls (a timestamp, an uptime counter — e.g. `hades_ping`) replays as a mismatch every time under `expected` comparison; such tools make poor fixture entries for `action='replay'`.

## trace_dependencies

- Walks `references` and `instance_of` edges outward; `corresponds_to` is deliberately excluded from the walk because every nested prefab instance that writes a `corresponds_to` edge also writes an `instance_of` edge to the same file, so including both would just duplicate hits.
