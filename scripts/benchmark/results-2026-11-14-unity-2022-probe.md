# Results — Unity 2022.3 LTS correctness probe

**Scored against:** [`preregistration-2026-11-14-unity-2022-probe.md`](preregistration-2026-11-14-unity-2022-probe.md), frozen in commit `1a50942` before any query was issued.
**Run:** 2026-11-14, Hades v2.2.0, project `d51ef315273689849942dd37265cf7dc` (AutoDRIVE at commit `75b7df52`, Unity 2022.3.52f1).
**Queries:** direct `tools/call` over `POST http://127.0.0.1:7823/mcp`. Raw responses were captured for every case.

## Index

AutoDRIVE indexed on the first attempt: **539,307 nodes, 1,145,922 edges, about 20 seconds**, `indexState: indexed`, no warnings, and the four Unity 6 projects already in the app were undisturbed. The largest single scene in it is 171 MB of YAML.

## Scores

| id | what it probes | verdict |
|---|---|---|
| C1 | script → one prefab | **exact** — 1/1 file, `sampleVia: m_Script` |
| C2 | material → one scene | **exact** — 1/1 |
| C3 | volume profile → one prefab | **exact** — 1/1 |
| C4a | leaf prefab → mid prefab | **exact** — 1/1, typed `corresponds_to` + `instance_of` |
| C4b | mid prefab → scene | **exact** — 1/1 |
| C4c | leaf prefab → *scene*, across the chain | **limit** — see below |
| C5 | material reachable only via override records | **exact — 13/13 scenes** |
| C6 | reference to a GUID with no node | **pass** — reported, not dropped |
| C7 | asset only in a freshly extracted scene | **exact** — 1/1; 47 occurrences collapse to one file |
| C8 | genuine orphan | **exact** — empty, correctly |
| C9 | binary asset | **honesty pass, capability gap** — see below |
| C10 | loaded by `Resources.Load`, no GUID reference | **exact** — empty, which is correct |
| C11 | prefab variants | **not testable on this project** |

Nine exact, two honesty passes, one documented limit, one gap that this project cannot close.

## The case that mattered most

**C5 passed cleanly.** `Assets/Materials/Transparent.mat` is referenced 69 times across 13 scenes, and *every one* of those occurrences is an `objectReference` entry inside a `m_Modifications` block — a prefab-instance override record. There is no ordinary property reference to it anywhere in the project. A tool that does not parse override records would have returned zero. Hades returned all thirteen scenes.

This was pre-registered as the single most discriminating case in the set, and it is the strongest evidence in this probe that the 2022.3 asset layer is read properly rather than approximately.

## C4c — impact analysis is not transitive inward

`find_references_to` on the leaf prefab returns **only its direct parent**. It types the relationship usefully (`corresponds_to`, `instance_of`, via `m_CorrespondingSourceObject`), so a reader can tell this is a nested-prefab instance rather than a plain field reference — but it does not name the scene three levels up, and it does not say that further ancestors exist. Reaching the scene takes a second query by hand.

The information is in the graph: `trace_dependencies` from the scene, depth 5, reaches both the mid prefab and the leaf. So the asymmetry is directional, not a missing edge — **outward dependency walking is transitive, inward impact walking is not.**

This matters more than its one-line verdict suggests, because "what breaks if I change X" is the question the project leads with. On a chain like this one, a single `find_references_to` answers it incompletely.

## C9 — an honest refusal, and a real gap

Asked what references a binary `TerrainData` asset, Hades returned `isError: true`:

> `'…/Terrain00.asset' exists on disk but is an asset type Hades does not index as a graph node, so nothing can be said about what references it here. inspect_asset can often still resolve a specific referencing file's own GUID link to it directly.`

That is the behaviour the pre-registration defined as a pass: it did not return zero references as though that were a fact, it said what it cannot do, and it pointed at an alternative. Exemplary.

The gap is real all the same: the ground-truth scan knows this asset is referenced by **7 scenes, 14 times**, and none of that is reachable through this tool. On a terrain-heavy simulation project, that is not a corner case. The suggested `inspect_asset` route was not exercised here — it was not pre-registered, and testing it now would be scope the freeze rule keeps out. It belongs in the next round.

## C6 — dangling references are handled well

Each unresolvable dependency comes back with `fromPath`, `depth`, `toGuid` and `propertyPath`, alongside a `danglingNote` that explains the cause in the response itself: the target GUID has no node, most often because it resolves into `Library/PackageCache` outside every scanned root. For the probed scene: 30 resolved dependencies, 15 dangling, none silently dropped.

## Where the zero-result honesty actually lives

C8 and C10 both return a bare, correct empty result:

```
{"asset":"…","totalReferences":0,"referencingFiles":0,"truncated":false,"files":[]}
```

They pass, because the pre-registration accepted the limit being visible in **the response or the documentation**, and `README.md`'s trust rubric states plainly that a count of zero means "nothing found statically", not "unused".

Worth recording honestly, though: that warning is not in the response. C10 is a prefab with zero GUID references that `SimpleFileBrowser.cs:44` loads by name at runtime — deleting it on the strength of this answer would break the project. An agent that calls the tool without having read the README has nothing in front of it to say so.

## C11 — not testable here

`m_IsPrefabVariant: 1` appears in **none** of AutoDRIVE's 530 prefabs. The project nests prefabs heavily but never uses variants. Since variants are named in `LIMITATIONS.md` as a specific blind spot, this probe cannot speak to them at all, and says so rather than borrowing the nesting result as a proxy.

## Correction to the scoring harness

The first scoring run reported C1 as a mismatch. That was a defect in the scoring script — it tokenised paths on whitespace, so paths containing spaces ("Windridge City") split apart. The raw response was exactly right. The verdict above reflects the raw response; the bug was in the scorer, not in Hades.

## What this probe does and does not establish

**Establishes:** Hades indexes a Unity 2022.3 LTS project of real size correctly enough to answer reference questions exactly in nine out of nine testable cases, including the override-only case that distinguishes a parsed asset graph from an approximated one; and where it cannot answer, it says so instead of returning a confident empty result.

**Does not establish:** anything about prefab variants, about shader-graph or VFX references (invisible to the ground truth too, and this is an HDRP project), about the `inspect_asset` route around binary assets, or about agent-level task success — that is the benchmark's job, not this probe's.
