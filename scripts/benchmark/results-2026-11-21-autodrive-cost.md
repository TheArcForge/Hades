# Results — AutoDRIVE cost probe, 2026-11-21

**Status: exploratory, not a frozen benchmark.** These runs were made *before* the slice-one
pre-registration was committed, and their purpose was to validate the harness and the task set.
They did the opposite: they falsified the task set. That is why they are reported here rather than
as the benchmark — the frozen run is still ahead, and this is the evidence behind withdrawing the
v1.2 cost headline.

**Subject:** AutoDRIVE Simulator at `75b7df5216524a3c6b3150d698139190c6c44f8f`, Unity 2022.3.52f1,
indexed as 539,307 nodes / 1,145,922 edges. **Hades v2.2.0, Claude Code 2.1.169,
`claude-opus-5-5`.** Keys from `reference_scan.py`, which shares no code with Hades.

## Conditions

Every arm: identical prompt, identical permission mode, `--strict-mcp-config` (the account has six
unrelated MCP servers that would otherwise load), and a `git` tree check before and after each run.
All 24 runs valid, zero permission denials, tree byte-identical throughout.

| arm | what it had |
|---|---|
| **A** | shell only — no MCP server |
| **C** | Hades over `--mcp-config` + the 23 skills |
| **P** | the plugin loaded via `--plugin-dir`, exactly as a user installs it |
| **D** | as C, plus one line of system prompt saying a project graph is available |

## T1 — a material reachable only through override records

`Assets/Materials/Transparent.mat`: 69 occurrences, every one an `objectReference` inside
`m_Modifications`. Expected: 13 scenes. Chosen because override records were expected to defeat
`grep`.

| arm | costs (3 runs) | median | recall | Hades calls |
|---|---|---|---|---|
| A | $0.069 / $0.084 / $0.087 | **$0.084** | 13/13 each | 0 |
| C | $0.153 / $0.154 / $0.158 | $0.154 | 13/13 each | 0 |
| P | $0.176 / $0.178 / $0.189 | $0.178 | 13/13 each | 0 |
| D | $0.187 / $0.188 / $0.189 | $0.188 | 13/13 each | 2 each |

**The premise was wrong.** The GUID sits inside `m_Modifications` as literal text, so `grep` finds
override records perfectly well. The graph added *meaning* — arm D alone reported that every
reference is a material override on a prefab placed in the scene — but not a different answer.

## T3 — a three-level nested-prefab chain

`GulleyLocalFog.prefab` → `GulleyShot.prefab` → `Off-Road Terrain.unity`. Expected: that one scene.
Chosen because a chain is the one thing `grep` cannot walk.

| arm | median | found the scene | Hades calls |
|---|---|---|---|
| A | **$0.125** | 3/3 | 0 |
| C | $0.203 | 3/3 | 0 |
| P | $0.237 | 3/3 | 0 |
| D | $0.217 | 3/3 | 3 each |

**That premise was wrong too.** The control took the prefab's GUID, found the parent prefab, took
*that* GUID, and found the scene — two `grep` passes, described accurately in its own answer.

## What this establishes

1. **No cost advantage, on these questions.** The control was cheapest in both tasks, by 1.6–2.2x,
   for identical answers. The v1.2 "27% less cost" figure does not reproduce here and is withdrawn.
2. **Connection is not free.** Arms C and P cost $0.07–0.11 more than the control **while never
   calling Hades once**. That is 33 tool schemas and 23 skills sitting in context.
3. **The graph is not reached unprompted.** Not with `--mcp-config`, and not with the plugin
   installed the way a user installs it. One line of system prompt changes that: arm D loaded a
   schema through `ToolSearch` and called `find_references_to` every time.
4. **A single run misleads.** One early control run cost $0.3835 and suggested Hades was cheaper.
   Repeated three times, the control's median was $0.084. Within-arm variance is otherwise tiny.

## What it does not establish

One project, one model, one Unity version, read-only asset questions, n=3. AutoDRIVE is large but
text-serialized and therefore greppable, and `claude-opus-5-5` writes good `grep`s. Untested:
larger or binary-heavy projects, weaker models, questions needing many hops or semantics `grep`
cannot see, and the entire Editor-action surface. **Prefab variants remain untested in public** —
AutoDRIVE has none (0 of 530 prefabs), and they are what the v1.2 correctness claim turns on.

## Method notes

- **A failed run can look like a successful one.** An auth failure returns
  `{"subtype":"success","is_error":true,"api_error_status":401,"total_cost_usd":0}` with the error
  text in `result`. The runner voids on `is_error`, `api_error_status`, permission denials, or a
  modified tree.
- **`claude auth status` is not a preflight.** It reported `loggedIn: true` while every call
  returned 401. Only a real call tells you.
- **Scoring nearly produced a spectacular false result.** The first pass extracted paths with a
  regex that stopped at whitespace; AutoDRIVE's scenes are named `RZR - Dirt Road.unity`. It scored
  every arm at recall 0.08. When every arm fails identically, suspect the scorer.
- Raw per-run JSON (envelope, tool counts, tree state) is written by `run_benchmark.py`; these runs
  were kept outside the repository as exploratory.
