# Pre-registration — Benchmark v2, slice one

**STATUS: NEVER FROZEN — superseded before it ran.** It was to be frozen by being committed, and
that did not happen. The exploratory runs of
[2026-11-21](results-2026-11-21-autodrive-cost.md) falsified **both** discriminating tasks before
any scored run: `grep` matched the graph on T1 (override records are literal text) and on T3 (a
capable model walks a nesting chain in two passes). It is committed as-is, unedited, as the record
of a task set that did not survive contact with a dry run — not as a live protocol.

Anything here still worth reusing: the arms, the two protocol rules, the scoring unit, the blind
scoring procedure, and the ground-truth keys. What needs replacing is the task set, and the open
question is where a graph beats a shell at all.

**Target:** AutoDRIVE Simulator, pinned commit `75b7df5216524a3c6b3150d698139190c6c44f8f`,
Unity 2022.3.52f1, indexed as project `d51ef315273689849942dd37265cf7dc`
(539,307 nodes / 1,145,922 edges).
**Ground truth:** `scripts/benchmark/reference_scan.py` over the same working tree — an independent
textual GUID scan sharing no code with Hades. 7,494 GUIDs mapped, 1,884,501 occurrences, zero
warnings.
**Design note:** `docs/design/BENCHMARK-01.md`.
**Ground-truth keys and the variant check were produced 2026-11-17**; the arms and the two protocol
rules below were revised on 2026-11-21 after the dry run, before any scored run.

## Why this file exists

The claim under test is the project's own headline, so the people running the test are not
disinterested. Freezing the prompts, the keys and the scoring rule before any scored run is the
cheapest available defence against deciding what counts as success once the answers are in.

**The freeze rule** (carried over verbatim from the 2026-11-14 probe): nothing below changes once a
scored run has been made. If a task turns out to be badly posed, it is scored as posed and the
problem is recorded as a finding, not fixed retroactively. New tasks may be **added** in a later,
separately dated pre-registration.

**The dry run is deliberately on a different asset** (`AprilTag_3.mat`), so that nothing learned
while debugging the mechanics can be used to reshape the task set below.

## Arms

Every arm passes `--strict-mcp-config`. This is not ceremony: the account has six unrelated MCP
servers configured (Gmail, Drive, Calendar and others) which would otherwise load into every
session and change both the tool surface and the token counts.

| arm | flags | what it measures |
|---|---|---|
| **A — control** | no MCP server, `--disable-slash-commands`, full default tools (Bash/Read/Grep/Glob) | the honest baseline |
| **B — graph, unadvertised** | Hades MCP over Streamable HTTP, `--disable-slash-commands` | whether an agent reaches for a graph nothing told it about |
| **C — the product** | Hades MCP + the 23 skills + 6 commands | **the headline** — what a user actually installs |

**A vs C is the product claim. B vs C is how much of any benefit is the prose rather than the
graph** — the question `Documentation/related-work.md` raises about Unity's first-party plugin,
turned on Hades itself.

**This three-arm split replaced an earlier two-arm one because the 2026-11-17 dry run falsified it.**
That design made "graph without skills" the headline, on the reasoning that bundling 23 skills'
worth of Unity prose with the tools would measure the graph plus the prose against neither. The dry
run showed the flaw: **arm B had Hades connected and never called it**, reporting that it had read
the GUID from the `.meta` file and grepped — arm A's method, at almost the same cost. The skills are
what tell an agent the graph exists, so removing them to avoid a confound removed the mechanism that
makes the tools get used. The "confound" is load-bearing, and arm B is kept as a finding rather than
as the headline.

Arm A keeps full shell tooling. The largest scene in this project is 171 MB of YAML, which is a real
hazard for a naive `grep` — that is a property of the baseline, and the report says so rather than
scoring it as cleverness.

**Model:** `claude-opus-5-5`, pinned, recorded in every run record. **Runs:** 3 per task per arm,
fresh session each → 3 tasks × 3 arms × 3 runs = **27 runs**.

## Two protocol rules the dry run forced

**1. Identical permission mode for every arm (`--permission-mode bypassPermissions`).** In the
2026-11-17 dry run, **`Bash` was denied in both arms**. Arm A *is* shell tooling: deny its `Bash`
and it limps while a graph arm keeps 33 MCP tools, so the result would have read as "the graph
wins" when the truth was "the opponent was blocked". Headless sessions inherit the interactive
permission setup, so this is systematic, not a one-off. Tool access must be symmetric or the
comparison is void.

**2. The project tree must be byte-identical before and after every run.** Permissive tool access
plus read-only questions is an assumption, and this turns it into a checked invariant: the runner
records `git rev-parse HEAD` and `git status --porcelain` before and after each run and **voids any
run that modified the tree**. The checkout is pinned at `75b7df52` and already carries 54 dirty
paths from the extracted `.zip` archives, so the comparison is snapshot-to-snapshot, not against a
clean tree.

## Scoring, decided in advance

**Unit: the file set.** The ground truth does not deduplicate — N occurrences on N lines are N
edges — so every key below is a **set of project-relative paths**, never an occurrence count. An
answer is scored on precision and recall against that set.

**Harmful-recommendation flag (binary, scored separately).** Did the answer advise an action that
would break the project — most importantly, calling an asset unused on the strength of a zero? This
exists because the 2026-11-14 probe found `SimpleFileBrowserCanvas.prefab`: zero GUID references,
yet loaded by name at runtime from `SimpleFileBrowser.cs:44`. No token saving offsets a harmful
answer, and the flag is reported alongside correctness, never averaged into it.

**Blind scoring.** Transcripts cannot be blinded — arms B and C visibly contain Hades tool calls. The
final answers can be: each answer is extracted, stripped of tool traces, shuffled, and scored
against the key without the scorer knowing which arm produced it.

**n = 3, and the report says so.** Three runs do not support medians with interquartile ranges
dressed as statistics. All three values are printed per cell with the spread stated. This is the one
place the report must not do what it criticised: Unity Insight's 53% / 52% rest on one run per arm
per question, and this project's own v1.2 headline shares that weakness.

## Tasks

Prompts are **verbatim**. The same string goes to both arms.

### T1 — references reachable only through override records

> Which files in this Unity project reference the asset `Assets/Materials/Transparent.mat`? Answer
> with the full project-relative path of every file that references it, one per line. If none, say
> so.

All 69 occurrences are `objectReference` entries inside `m_Modifications` — prefab-instance override
records. There is no ordinary property reference to this material anywhere in the project, so a tool
that does not parse override records returns zero.

**Expected set — exactly 13 files, all scenes:**

```
Assets/Scenes/City.unity
Assets/Scenes/Hunter SE - New.unity
Assets/Scenes/Husky - New.unity
Assets/Scenes/OpenCAV - AEB - AVLDC.unity
Assets/Scenes/OpenCAV - AEB - HPC.unity
Assets/Scenes/OpenCAV - XR.unity
Assets/Scenes/RZR - AEB - HPC.unity
Assets/Scenes/RZR - Dirt Road.unity
Assets/Scenes/RZR - Scaled Forest - Data Collection.unity
Assets/Scenes/RZR - Scaled Forest - Deployment.unity
Assets/Scenes/RZR - TN1-ORT.unity
Assets/Scenes/RZR - TN3-ORT.unity
Assets/Scenes/VSR - RZR Convoy Data Collectrion.unity
```

*(The misspelling in the last filename is the project's own and is reproduced exactly.)*

### T2 — ordinary reference finding, mixed asset types

> Which files in this Unity project reference the script `Assets/Scripts/LapTimer.cs`? Answer with
> the full project-relative path of every file that references it, one per line. If none, say so.

The common case, with no subtlety: 12 occurrences across 10 files, both scenes and prefabs. A
failure here is a defect rather than a nuance. It is the project's own code, not a third-party
plugin, and the mixed set means a partial answer is visible as partial.

**Expected set — exactly 10 files (8 scenes, 2 prefabs):**

```
Assets/Prefabs/F1TENTH/F1TENTH.prefab
Assets/Prefabs/NeoRacer/NeoRacer.prefab
Assets/Scenes/F1TENTH - Sim Racing - H2H.unity
Assets/Scenes/F1TENTH - Sim Racing.unity
Assets/Scenes/NeoRacer - AuE-X930 - Explore.unity
Assets/Scenes/NeoRacer - AuE-X930.unity
Assets/Scenes/RoboRacer - Sim Racing - Explore.unity
Assets/Scenes/RoboRacer - Sim Racing - H2H.unity
Assets/Scenes/RoboRacer - Sim Racing.unity
Assets/Scenes/RoboRacer - Variable Friction.unity
```

### T3 — transitive impact across a nesting chain (Hades is known to be weak here)

> I am about to change `Assets/Environments/Off-Road Terrain/Prefabs/LocalFog/GulleyLocalFog.prefab`.
> Which **scenes** in this project would be affected by that change? Answer with the full
> project-relative path of every affected scene, one per line, and briefly say how you determined
> it. If none, say so.

The chain, confirmed independently by the ground truth today:

```
GulleyLocalFog.prefab  --17 occ-->  GulleyShot.prefab  --24 occ-->  Off-Road Terrain.unity
```

**Expected set — exactly 1 scene:**

```
Assets/Environments/Off-Road Terrain/Scenes/Off-Road Terrain.unity
```

**This task is in the set because Hades is expected to lose it.** The 2026-11-14 probe established
that `find_references_to` walks exactly one hop inward: it returns `GulleyShot.prefab`, which is not
a scene, and says nothing about further ancestors. The gap is already public in `ROADMAP.md` and
`LIMITATIONS.md`. A benchmark that tests only what the tool is good at is marketing with error bars.

**T3 carries a second, separately reported dimension — honesty.** An answer that names no scene but
states plainly that it found only the direct parent and that further ancestors may exist is
materially better than one that implies the search was complete. Scored as: *disclosed* /
*not disclosed*. This mirrors the honesty cases of the 2026-11-14 probe and is **not** folded into
precision or recall.

## Not testable on this project

**Prefab variants.** Re-verified 2026-11-17: `m_IsPrefabVariant` appears in **0 of 530** prefabs,
in any form, while 430 carry `m_CorrespondingSourceObject`. Variants are named in `LIMITATIONS.md`
and the README trust rubric as a specific blind spot, and this project cannot close it. Recorded as
a gap, not as a pass, and deferred to the full matrix on a project that has them.

*(A first attempt at that check returned zero for every row because zsh expanded an unquoted
`--include=*.prefab` glob before `grep` saw it. The figures above are from the re-run, which carries
two non-zero control rows precisely so that a false zero cannot pass unnoticed.)*

## Known holes in the ground truth itself

Stated so that no task silently depends on them:

- It **cannot read binary assets**, so nothing about a binary asset's outgoing references is
  scorable. No task above depends on one.
- It does **not** resolve variants, stripped objects or any semantic relationship. It reports
  textual facts only.
- It does **not** deduplicate, which is why every key is a file set.
- A GUID resolving outside the scanned roots (e.g. into `Library/PackageCache`) has no asset path.
  No task above targets one.
