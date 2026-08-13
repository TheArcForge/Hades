# Hades regression suite

Executable regression cases for the defects tracked as F-numbers throughout this suite
(originally written up as `FINDINGS.md` in an internal tester's findings bundle, adopted here
so "is it fixed?" is a command rather than a re-read of that write-up).

```
python3 hades_suite.py              # everything (needs Hades.app running + a live Unity Editor)
python3 hades_suite.py --no-editor  # protocol + graph cases only
python3 hades_suite.py --list       # describe every case without running it
```

No dependencies beyond the Python standard library. Talks to the Hades MCP endpoint directly —
default `http://127.0.0.1:7823/mcp`, override with `--url` or the `HADES_URL` environment
variable to point at a rebuilt core on another port — so it does **not** depend on the Claude
Code plugin and is unaffected by F1.

## How to read the output

Each case declares what it should do *today*:

| Declared | Meaning | Result you want |
|---|---|---|
| `expect="fail"` | an open finding | `still-broken` — the bug reproduces |
| `expect="pass"` | behaviour that is currently correct | `as-expected` — nothing regressed |

Anything else is flagged as a deviation:

- `*** NOW FIXED ***` — an open finding stopped reproducing. **Flip its `expect` to `"pass"`** so
  the fix is guarded from then on.
- `*** NEWLY BROKEN ***` — something that worked has stopped working.

**Exit code is 0 when every case matches its expectation.** A green run therefore means "all known
findings still present, nothing else regressed" — not "no bugs". Read the table.

Current expected state: **Part A 6/6 replayed**, Part B **13 still-broken, 4 guards passing, 0
deviations**.

## Why the suite is in two parts

Part A uses the app's own `hades_regression` tool: a recorded fixture is replayed with
`action="replay"` and each call's result compared against the `expected` value captured at record
time. This is the intended workflow — `stop`'s output is documented as being exactly the shape
`replay` accepts, and it is.

Part B exists because **`hades_regression` cannot see most of the defects.** It records "tool calls
made against the attached Unity Editor", so the graph and disk-backed read surface is invisible to
it. Measured directly: a recording session was given six calls, and only the two Editor-routed ones
were captured.

| Call | Routed via | Recorded? |
|---|---|---|
| `project_settings_apply` | Editor | yes (`projectSettings.apply`) |
| `scene_apply` | Editor | yes (`scene.apply`) |
| `find_references_to` | graph | **no** |
| `graph_query` | graph | **no** |
| `trace_dependencies` | graph | **no** |
| `project_settings` | disk | **no** |

F6, F7, F13 and F14 — four of the most serious findings — all live on the unrecordable surface.
That blind spot is worth closing on its own merits: the observability tooling covers the part of the
system that has proven correct, and not the part that hasn't.

## Cases

Part A (`fixtures/editor-routed.json`, 6 calls) covers the Editor-routed mutation surface: tag
create/delete, the partial-failure contract (`applied=[0,2] failed=[1]`), and component add/remove.
Every block is net-zero on project state so the fixture stays replayable — a fixture containing a
bare `create` would pass once and then fail forever with "already exists".

Part B:

| id | finding | asserts |
|---|---|---|
| P1 | F1 | no top-level boolean subschema — one such field rejects the entire 32-tool list |
| P2 | F1 | no boolean subschemas anywhere (16 exist; 15 are latent under `items`) |
| P3 | F5 | `initialize` advertises the product version, not `1.0.0.0` |
| G1 | F6 | `trace_dependencies` reports a material's texture/shader dependencies |
| G2 | F6 | `find_references_to` answers for a texture the project references |
| G3 | F6 | `search_by_name` finds a texture asset |
| G4 | F6 | **control** — the same lookup works on an indexed kind. If this fails, the F6 diagnosis is wrong |
| G5 | F7 | `graph_query` rejects an unrecognised `kind` |
| G6 | F7 | `graph_query(kind=Scene)` does not silently return 0 on a project that has scenes |
| G7 | F13 | unknown parameters are rejected, not silently dropped |
| A1 | F9 | `project.json` records `UnityVersion` while an Editor is attached |
| E1 | F14 | a newly created asset is queryable without a manual rebuild |
| E2 | F14 | after a move, the old path is rejected and the new one resolves |
| E3 | F10 | a created tag is visible to `project_settings` immediately |
| E4 | F8 | **guard** — `prefab_apply create` yields a nested `PrefabInstance`, not a flattened copy |
| E5 | — | **guard** — `asset_manage move` preserves the GUID and removes the stale `.meta` |
| E6 | — | **guard** — `material_apply setProperty` is present in the saved YAML |

The guards matter as much as the failures. E4 pins a bug that was already fixed but is still
documented as open (F8) — see `Documentation/InternalTesting-Install.md`'s Known Issues section.
E5 pins the one thing that would silently break a whole project — a move that changed GUIDs — on
an operation that documents itself as having no undo.

## Anchors: running against a different project

`hades_suite.py`'s anchors — `UNITY_ROOT`, `MAT_WITH_TEXTURES`, `REFERENCED_TEXTURE`,
`REFERENCED_PREFAB` — are set for Hades-Unity-Client. Each must be an asset something
**demonstrably references**; the assertions are built on "X references Y, so a query denying Y is
provably wrong", and an unreferenced asset makes them vacuous. The comments above each anchor in
`hades_suite.py` record the exact referencing file and line used to verify it.

Worth knowing if you re-anchor this further: Hades-Unity-Client has exactly one texture in the
whole project (`Assets/TutorialInfo/Icons/URP.png`, referenced only by `Assets/Readme.asset`'s
`icon` field) and no material that references it, so `MAT_WITH_TEXTURES` points at
`Assets/Settings/PC_Renderer.asset` (the URP renderer asset) instead of a `.mat` file — its
`ScreenSpaceAmbientOcclusion` feature references seven textures and a shader by GUID, which
exercises the same F6 gap. If your project has a material with real texture bindings, prefer
that — it's the more direct case.

`fixtures/editor-routed.json` is project-independent (it only creates and deletes its own objects,
all named `RegSuite*`) and can be replayed anywhere. Re-record it with `hades_regression`
`start` / calls / `stop` if the wire protocol changes.

## Notes on the harness itself

- Editor cases work in `Assets/HadesRegressionTmp/`, removed before and after each run.
- That cleanup deletes files **on the filesystem**, because `asset_manage` has `move`/`import`/
  `refresh` but no `delete` op — the one gap the harness had to work around rather than test.
- `E2` calls `hades_rebuild_graph` as setup, to get the pre-move asset indexed in the first place.
  Rebuild time scales with project size — seconds on a small project, longer on a large one.
- The suite does not enter play mode. F12 (a PlayMode test run silently saves open scenes) is
  deliberately excluded: reproducing it writes to a tracked project scene. To check it by hand, note
  `git status`, run any `project_run_tests` with `testMode: PlayMode`, and look again.
- Undo is not covered. `scene_apply` and `material_apply` both claim a batch is one undo group, but
  no undo is exposed over MCP and macOS blocks synthetic keystrokes
  (`osascript is not allowed to send keystrokes`). To test by hand: create three objects in one
  `scene_apply` batch, press Cmd+Z once, count survivors — 0 confirms the claim, 1 refutes it.
