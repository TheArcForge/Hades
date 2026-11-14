# Pre-registration — Unity 2022.3 LTS correctness probe

**Frozen:** 2026-11-14, before a single query was put to Hades.
**Target:** AutoDRIVE Simulator, pinned commit `75b7df5216524a3c6b3150d698139190c6c44f8f`, Unity 2022.3.52f1, indexed as project `d51ef315273689849942dd37265cf7dc` (539,307 nodes / 1,145,922 edges).
**Ground truth:** `scripts/benchmark/reference_scan.py` over the same working tree. 7,494 GUIDs mapped, 1,884,501 textual occurrences, zero warnings.

## Why this file exists

The person designing these cases knows Hades, wrote the brief for the ground-truth tool, and will score the results. That is the largest bias risk in the whole exercise — larger than anything about the tool itself. Writing the cases and their expected answers down *before* asking Hades anything removes the room for unconscious adjustment afterwards.

**The freeze rule:** nothing below changes once Hades has been queried. If a case turns out to be badly posed, it is scored as posed and the problem is recorded as a finding, not fixed retroactively. New cases may be *added* in a later, separately dated pre-registration.

## Scoring unit, decided in advance

The ground-truth tool counts **every textual occurrence** of a GUID. Hades builds a **de-duplicated graph**. Those are not the same unit, and comparing them directly would be unfair to Hades.

So the scored unit is **the set of referencing files**, not the occurrence count. Occurrence counts are recorded for information only. A case passes when Hades returns the same file set; precision and recall are computed over that set.

## Honesty cases are scored differently

For C6, C8, C9 and C10 the question is not "did Hades find it" — static analysis provably cannot in some of them. The pass criterion is whether the answer **misleads**:

- **Pass** — the result is correct, or it is empty/partial *and* the response or documentation makes the limit visible.
- **Fail** — an empty or partial result is presented as a complete fact, such that a reader would conclude the asset is unused.

This distinction is set now, deliberately, because it is exactly the kind of thing that is tempting to soften after seeing an answer.

## Cases

Each case asks Hades: **which files reference this asset?** Queries go directly to the MCP endpoint (`POST http://127.0.0.1:7823/mcp`, `tools/call`), and both the request and the raw response are recorded verbatim.

### Simple — these prove the method works. Failure here is a defect, not a subtlety.

| id | asset | GUID | expected file set | occ. |
|---|---|---|---|---|
| **C1** | `Assets/Environments/Windridge City/Windridge City Assets/Scripts/UIQuallityManager/UIQualityManager.cs` | `194fd04eca007e941a7651b75d543d2d` | exactly 1: `…/UIQuallityManager/canUIQuallityManager.prefab` | 1 |
| **C2** | `Assets/Materials/AprilTags/AprilTag_3.mat` | `058cf645c5426954c9fb13cd16f1a6df` | exactly 1: `Assets/Scenes/Multi-Agent Tiny Town.unity` | 1 |
| **C3** | `Assets/Environments/Off-Road Terrain/Volumes/Screenshots/ScreenshotOxbowVolume.asset` | `0aecb8319b887406dae821509d0b4a8f` | exactly 1: `Assets/Environments/Off-Road Terrain/Prefabs/Screenshots/ScreenshotOxbow.prefab` | 1 |

### Hard

**C4 — three-level nesting.** A leaf prefab inside a mid prefab inside a scene. Asked as two queries plus one judgement.
- C4a: `Assets/Environments/Off-Road Terrain/Prefabs/LocalFog/GulleyLocalFog.prefab` (`55993c484e547744fac372bbc9716986`) → expected exactly 1 file: `…/Prefabs/Shots/GulleyShot.prefab` (17 occurrences).
- C4b: `Assets/Environments/Off-Road Terrain/Prefabs/Shots/GulleyShot.prefab` (`a6a134db39c954e45b14678328cb897b`) → expected exactly 1 file: `Assets/Environments/Off-Road Terrain/Scenes/Off-Road Terrain.unity` (24 occurrences).
- C4c: ask which **scenes** are affected by changing `GulleyLocalFog.prefab`. The correct answer requires traversing the chain: `Off-Road Terrain.unity`. Scored pass only if the scene is named; scored as a documented limit if Hades returns only the direct parent and says nothing more.

**C5 — a material reachable only through prefab-instance overrides.** `Assets/Materials/Transparent.mat` (`9dd42d57d44fea6428f457c9c0180adc`). All 69 occurrences are `objectReference` entries inside `m_Modifications`, i.e. override records — there is no ordinary property reference anywhere.
Expected file set, exactly these 13 scenes:
`Assets/Scenes/City.unity`, `Hunter SE - New.unity`, `Husky - New.unity`, `OpenCAV - AEB - AVLDC.unity`, `OpenCAV - AEB - HPC.unity`, `OpenCAV - XR.unity`, `RZR - AEB - HPC.unity`, `RZR - Dirt Road.unity`, `RZR - Scaled Forest - Data Collection.unity`, `RZR - Scaled Forest - Deployment.unity`, `RZR - TN1-ORT.unity`, `RZR - TN3-ORT.unity`, `VSR - RZR Convoy Data Collectrion.unity` (all under `Assets/Scenes/`).
This is the single most discriminating case in the set: a tool that ignores override records returns zero.

**C6 — a GUID that belongs to no asset in the project** (honesty case). `73c176f402d2c2f4d929aa5da7585d17` has no `.meta` anywhere in the tree — it lives in `Library/PackageCache` — yet it is referenced 8,225 times across 53 files, 6,784 of them in `Assets/Scenes/PIXKIT - Mcity.unity` alone. Expected: Hades either resolves it via the package cache, or reports the references with the target unresolved. Fail if it silently drops the references or invents a path.

**C7 — an asset that exists only inside a freshly extracted scene.** `…/Dynamic Nature - Mountain Tree Pack/Materials/M_Fir_02_Cross Baked.mat` (`6c03fdc6b5360b84982a76f547d34e57`) → expected exactly 1 file: `Assets/Environments/Windridge City/Scenes/Windridge City.unity`, where it occurs 47 times. Confirms archived content indexes like everything else, and that 47 occurrences collapse to one file rather than 47 phantom references.

**C8 — a genuine orphan** (honesty case). `Assets/Environments/OSM/Materials/ASPHALT_0.mat` (`f43af0e2562f92f4bb1714d0c90445ba`) has zero references of any kind. Expected: an empty result. Pass requires that the emptiness is not presented as proof the material is unused.

**C9 — a binary asset** (honesty case). `Assets/Environments/Off-Road Terrain/Terrain/Data/Terrain00.asset` (`584c420dcdc12ae4888913a4057c5cca`). Unity wrote it in binary, so the ground-truth tool cannot read what it *points at*; its 14 incoming occurrences across 7 scenes are known and listed. Expected incoming file set: `Hunter SE - New.unity`, `Husky - New.unity`, `RZR - Scaled Forest - Data Collection.unity`, `RZR - Scaled Forest - Deployment.unity`, `RZR - TN1-ORT.unity`, `RZR - TN3-ORT.unity`, `VSR - RZR Convoy Data Collectrion.unity`. Outgoing references are **unknown to the ground truth** and are therefore not scored — only whether Hades claims to know them.

**C10 — used at runtime, invisible to static analysis** (honesty case). `Assets/Plugins/SimpleFileBrowser/Resources/SimpleFileBrowserCanvas.prefab` (`9ea2606f8fddead46aabb7adb3d8d434`) has **zero** GUID references, yet `Assets/Plugins/SimpleFileBrowser/Scripts/SimpleFileBrowser.cs:44` loads it by name:
`Instantiate<GameObject>( Resources.Load<GameObject>( "SimpleFileBrowserCanvas" ) )`.
Deleting it on a zero-reference result would break the project. Expected: an empty reference set — that is correct — with the limit visible. This is the case the README's own trust rubric is about.

## Not testable on this project

**Prefab variants.** `m_IsPrefabVariant: 1` appears in **zero** files across all 530 prefabs. AutoDRIVE nests prefabs but never uses variants. Since variants are named in `LIMITATIONS.md` and in the README's trust rubric as a specific blind spot, this cannot be closed here and must be probed on a project that has them — a Unity 6 project, or the regression fixture. Recorded as a gap, not as a pass.

## Known holes in the ground truth itself

Stated now so they cannot be used as excuses later:
- 98 binary files are unreadable by the scanner; their outgoing references are missing (C9 is scored with this in mind).
- `.shadergraph`, `.vfx`, `.uxml`, `.inputactions` and `.asmdef` are not scanned, and AutoDRIVE is an HDRP project, so shader-graph references are invisible to the ground truth.
- Path-based and reflection-based references are invisible by construction (C10 exists because of this).
- `Library/PackageCache` is not scanned, so package GUIDs resolve to no path (C6 exists because of this).
