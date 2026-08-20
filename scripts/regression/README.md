# Hades regression suite

`FINDINGS.md` in executable form — adopted from an internal tester's findings bundle into this
repo, round 2 (22 Part B cases, up from 17; two of our own F21 guards added afterward, now
E13–E14), merged with round 3 (2026-08-15: F22 added as `E12`, the tester's own numbering) for 25.
Every finding that can be asserted mechanically is a case here, so "is it fixed?" is a command
rather than a re-read.

```
python3 hades_suite.py              # everything (needs Hades.app running + a live Unity Editor)
python3 hades_suite.py --no-editor  # protocol + graph cases only
python3 hades_suite.py --list       # describe every case without running it
```

No dependencies beyond the Python standard library. Talks to the Hades MCP endpoint directly —
default `http://127.0.0.1:7823/mcp`, override with `--url` or the `HADES_URL` environment
variable to point at a rebuilt core on another port — so it does **not** depend on the Claude
Code plugin and is unaffected by F1.

## Running UnityPlugin's own EditMode tests

`hades_suite.py` above never touches `UnityPlugin/Tests/Editor` — those tests only compile inside a
Unity Editor, so `dotnet test` cannot run them either. `run-plugin-editmode.sh` closes that gap:

```
scripts/regression/run-plugin-editmode.sh
```

It builds a throwaway, minimal Unity project in a `mktemp` scratch directory (never inside this
checkout), copies `UnityPlugin/Assets/Hades` and `UnityPlugin/Tests/Editor` into it, and runs
`-batchmode -runTests -testPlatform EditMode` against it — no Hades.app, no MCP endpoint, and no
live project needed. Unity is located automatically (newest version under the Hub's install
location); override with `$UNITY_BIN`. Exits `2` if no Unity install can be found, non-zero if
any test fails, `0` on an all-green run — printing a one-line `total/passed/failed` verdict
either way, and the scratch project's path for inspection if it failed. Baseline: 384/384 (a
higher total with 0 failed is still a pass; only a lower total or any failure is news).

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

Declared state (2.0.0 baseline): **Part A 6/6 replayed**, Part B **25 cases — 24 declaring
`expect="pass"`** (fixed findings, including the F16/F19/F20/F21 stress-round cases E7-E11 and
E13-E14, held as regression guards) **and 1 declaring `expect="fail"`** — `E12` (F22), an open
finding as of the round-3 merge (see Notes). F17 and F18 are excluded by design, and F12 is now
covered by plugin EditMode tests instead (see Notes).
Run the suite for the current deviation count — the whole point of `expect` is that it's checked,
not assumed.

## Why the suite is in two parts

Part A uses the app's own `hades_regression` tool: a recorded fixture is replayed with
`action="replay"` and each call's result compared against the `expected` value captured at record
time. This is the intended workflow — `stop`'s output is documented as being exactly the shape
`replay` accepts, and it is.

Part B exists because of **F15**: at 0.1.0 `hades_regression` could not see most of the defects. It
recorded only "tool calls made against the attached Unity Editor", so the graph and disk-backed read
surface was invisible to it. Measured on 0.1.0 — six mixed calls in, two recorded:

| Call | Routed via | 0.1.0 | 2.0.0-beta.2 |
|---|---|---|---|
| `project_settings_apply` | Editor | yes | yes |
| `scene_apply` | Editor | yes | yes |
| `find_references_to` | graph | **no** | **yes** |
| `trace_dependencies` | graph | **no** | **yes** |
| `project_settings` | disk | **no** | **yes** |
| `graph_query` | graph | **no** | n/a — the call now errors, and a failed call has no result to record |

**F15 is fixed**: 5 of 6 captured on 2.0.0-beta.2. Much of Part B could now be re-expressed as
fixtures. It is deliberately left as direct assertions anyway, for two reasons: the protocol-level
cases (P1–P3) assert on `tools/list` and `initialize`, which are not tool calls at all and can never
be fixtures; and a fixture asserts "the result equals what was recorded", whereas several Part B
cases assert something structural — "this must error", "this must not be empty", "the GUID must not
change" — which survives a project change that a recorded value would not.

Note the recorder now keys calls by **MCP tool name** (`project_settings_apply`) rather than the
UnityPlugin wire name (`projectSettings.apply`) it used at 0.1.0. The existing fixture still replays
6/6, so replay accepts both.

## Cases

Part A (`fixtures/editor-routed.json`, 6 calls) covers the Editor-routed mutation surface: tag
create/delete, the partial-failure contract (`applied=[0,2] failed=[1]`), and component add/remove.
Every block is net-zero on project state so the fixture stays replayable — a fixture containing a
bare `create` would pass once and then fail forever with "already exists".

Part B:

| id | finding | asserts |
|---|---|---|
| P1 | F1 | no top-level boolean subschema — one such field rejects the entire 32-tool list |
| P2 | F1 | no boolean subschemas anywhere (16 existed at 0.1.0; 0 now) |
| P3 | F5 | `initialize` advertises the product version, not `1.0.0.0` |
| G1 | F6 | `trace_dependencies` resolves a real asset's dependencies (script + project texture) |
| G2 | F6 | `find_references_to` answers for a texture the project references |
| G3 | F6 | `search_by_name` finds a texture asset |
| G4 | F6 | **control** — the same lookup works on an indexed kind. If this fails, the F6 diagnosis is wrong |
| G5 | F7 | `graph_query` rejects an unrecognised `kind` |
| G6 | F7 | `graph_query(kind=Scene)` does not silently return 0 despite scenes existing |
| G7 | F13 | unknown parameters are rejected, not silently dropped |
| A1 | F9 | `project.json` records `UnityVersion` while an Editor is attached |
| E1 | F14 | a newly created asset is queryable without a manual rebuild |
| E2 | F14 | after a move, the old path is rejected and the new one resolves |
| E3 | F10 | a created tag is visible to `project_settings` immediately |
| E4 | F8 | **guard** — `prefab_apply create` yields a nested `PrefabInstance`, not a flattened copy |
| E5 | — | **guard** — `asset_manage move` preserves the GUID and removes the stale `.meta` |
| E6 | — | **guard** — `material_apply setProperty` is present in the saved YAML |
| E7 | F16 | a `..` path cannot write outside `Assets/` — **self-cleaning**: removes any escape it finds |
| E8 | F16 | `create` does not silently overwrite an existing file of another type (victim is a prefab the case creates) |
| E9 | F19 | `trace_dependencies` walks prefab→prefab nesting that `find_references_to` sees in reverse |
| E10 | F20 | a repeated `create` refuses, or distinguishes created from replaced |
| E11 | F21 | reparenting a GameObject under itself is refused |
| E12 | F22 | **open finding, `expect="fail"`** — after `hades_rebuild_graph`, a subsequent move still leaves the old path resolvable (`E2` is the no-rebuild control; flips to a guard once fixed) |
| E13 | F21 | `scene_manage duplicate` with `sourcePath==destPath` is refused, not silently self-overwritten |
| E14 | F21 | `prefab_apply createVariant` with `basePrefabPath==variantPath` is refused, base left byte-identical |

The guards matter as much as the failures. E4 pins a bug (F8, the `prefab_apply create` flattening
issue) that's fixed and already correctly removed from
`Documentation/Installing.md`'s Known Issues section — the guard is what stops that
regressing unnoticed. E5 pins the one thing that would silently break a whole project — a move
that changed GUIDs — on an operation that documents itself as having no undo. E12 is the suite's
one open case: it stays red until F22 is fixed, at which point its `expect` flips to `"pass"` and
it becomes a guard like the rest.

## Anchors: running against a different project

`hades_suite.py`'s anchors — `UNITY_ROOT`, `MAT_WITH_TEXTURES`, `REFERENCED_TEXTURE`,
`REFERENCED_PREFAB` — are set for Hades-Unity-Client. Each must be an asset something
**demonstrably references**; the assertions are built on "X references Y, so a query denying Y is
provably wrong", and an unreferenced asset makes them vacuous. The comments above each anchor in
`hades_suite.py` record the exact referencing file and line used to verify it.

Worth knowing if you re-anchor this further: Hades-Unity-Client has exactly one texture in the
whole project (`Assets/TutorialInfo/Icons/URP.png`, referenced only by `Assets/Readme.asset`'s
`icon` field) and no material that references it, so `MAT_WITH_TEXTURES` points at
`Assets/Readme.asset` instead of a `.mat` file — it resolves cleanly to that texture plus its
own script. A renderer asset's `ScreenSpaceAmbientOcclusion` feature (`Assets/Settings/
PC_Renderer.asset`) looked like a plausible substitute — real, GUID-based texture/shader
references — but its GUIDs are all package-bundled and outside every root Hades scans, so
`trace_dependencies` correctly reports them `dangling` rather than resolved; that's a different
condition from F6 and would never pass regardless of F6's fix status. Verified live during the
round-2 adoption (2026-08-15): `trace_dependencies` on `PC_Renderer.asset` returns
`totalReturned: 0` with a populated `dangling` array (`danglingNote` explains why); on
`Readme.asset` it returns `totalReturned: 2`, `dangling: []`. If your project has a material
with real, in-project texture bindings, prefer that — it's the more direct case.

None of the round-2 cases (E7-E11), nor E13/E14 added afterward, nor E12 (F22, merged from round
3), needed a new anchor: F16's two cases operate on the scratch `TMP` folder and a relative escape
path, F20's and F21's cases (including E13/E14, F21's own scene-duplicate and createVariant
self-reference siblings) create and discard their own objects, F22's case works the same scratch
`TMP` folder as E1/E2, and F19's nested-prefab case builds its own nesting at runtime
(`prefab_apply create`, then `instantiate`, then `create` again one level up) rather than
requiring a pre-existing nested prefab in the project.

`fixtures/editor-routed.json` is project-independent (it only creates and deletes its own objects,
all named `RegSuite*`) and can be replayed anywhere. Re-record it with `hades_regression`
`start` / calls / `stop` if the wire protocol changes.

## Notes on the harness itself

- Editor cases work in `Assets/HadesRegressionTmp/`, removed before and after each run.
- That cleanup deletes files **on the filesystem**, because `asset_manage` has `move`/`import`/
  `refresh` but no `delete` op — the one gap the harness had to work around rather than test.
- `E2` and `E12` are a one-variable pair over the same create-then-move scenario. `E2` waits for
  incremental indexing only (no rebuild in between) and expects the old path retired; `E12` (F22)
  inserts a `hades_rebuild_graph` between the create and the move and currently finds *both* the
  old and new path still resolving — the rebuild path has its own stale-entry bug that incremental
  indexing does not share. `E12` stays `expect="fail"` until that's fixed; do not add a rebuild
  back into `E2`'s setup, or it stops testing what it says it tests.
- **F12 is fixed and is covered, just not here.** The scene save turned out to be our own code,
  not Unity's, and it ran for *every* `testMode` — EditMode included, which the original report
  never caught. The write is gone, so there is nothing destructive left to reproduce; the guard
  lives in the plugin EditMode suite (`ProjectCommandsTests.RunTests_*_NeverSavesDirtyOpenScene`),
  which can dirty a scratch scene and assert it stays dirty without touching a tracked file.
- **Two findings have no automated case here, deliberately**: reproducing F18 is disruptive rather
  than merely slow, and F17 (fixed as of round 3) is cheap enough to verify by hand that it was
  never worth automating:
  - **F17** — an over-long (~300 char) asset path used to leave Unity blocked on a modal dialog
    with a Force Quit button. Confirmed fixed as of round 3 (2.0.0-beta.3): the path is now
    refused with a per-op error and the Editor stays `busy: false`. Still left uncovered by a
    case — the fix is upstream of the Editor, and a length check is cheap to eyeball by hand.
  - **F18** — a batch large enough to exceed the 30s bridge timeout writes 1,000+ files and keeps
    going after the error returns. By hand: `material_apply` with 800 creates, then count the folder.
- E7 and E8 are the only cases that deliberately attempt destructive input. E7 removes anything it
  manages to write outside `Assets/` and reports it as a failure; E8's victim is a prefab the case
  creates itself. Neither ever targets a pre-existing project asset — which is exactly the mistake
  that damaged real files during the tester's own stress round.
- Undo is not covered. `scene_apply` and `material_apply` both claim a batch is one undo group, but
  no undo is exposed over MCP and macOS blocks synthetic keystrokes
  (`osascript is not allowed to send keystrokes`). To test by hand: create three objects in one
  `scene_apply` batch, press Cmd+Z once, count survivors — 0 confirms the claim, 1 refutes it.
