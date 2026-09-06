# Hades — Limitations

Hades is honest about what it can and can't know. Most of the boundaries below are **by design** (a consequence of static analysis), not bugs. **The tools do not announce them at runtime** — there is no confidence score on a result and no health field to check, so this page is the record, not a reminder. Read it before you hit an edge rather than after.

If you see a result that's wrong *outside* these boundaries, that's a real bug — please [open an issue](https://github.com/TheArcForge/Hades/issues) with a repro.

## The one-line version

**Structural facts are exact. Inferred C# relationships are a strong lead, not gospel.** Trust "what is this / where is it / what does it contain"; verify "who references this / what inherits this" before destructive changes — especially across package boundaries.

## What Hades is rock-solid at

These come from reading your project's serialized data directly, so they're exact:

- Type → declaring file; class/struct/interface/enum membership
- Prefab / scene / material / ScriptableObject contents (hierarchy, components, serialized values), including variants and nested/stripped roots
- Asset GUID, type, and direct dependency lists
- Project structure, counts, and "where is X" lookups

Use these without second-guessing them.

## Where Hades is partial (verify before acting)

The C# **relationship** layer is inferred by static analysis, so it's a strong lead rather than a guarantee. Confirm independently before deleting/refactoring when the answer touches:

- **Reference queries** — "what references X" for scripts and prefabs. A count of zero means "nothing was found statically", never "definitely unused", and nothing in the response distinguishes the two for you. Check by hand — a prefab variant or a nested prefab can embed an asset without producing a reference edge — before treating an asset as unused.
- **Inheritance / `implements`, dependency traces, "which prefabs use this component"** — most reliable for plain types defined under `Assets/`; less so across the cases below.

## What static analysis cannot see (by design)

None of these are extracted, because they aren't visible without running your game:

- **Reflection** and runtime/string-based dispatch (`Type.GetType`, `SendMessage`, string-keyed lookups)
- **Dependency-injection / service-locator wiring** resolved at runtime
- **Dynamic instantiation** and other runtime-only object graphs

A reference that exists *only* through one of these will not appear in the graph. That's why "no references found" means "none were found statically" — not "definitely unused."

## Coverage boundaries

- **Precompiled DLL types** can't be turned into graph nodes from source, so edges *to* a base class/interface/type that lives in a compiled package or DLL may stay unresolved. Hades reports these as external/unindexed rather than pretending they don't exist.
- **Generics** are resolved best-effort; deeply nested or open generic relationships may be incomplete.
- **Binary/imported assets (textures, models, audio, fonts, shaders, animation clips) are meta-only nodes** — path, name, kind, and GUID, so references into them resolve, but their own content (a texture's pixels, a clip's curves) is never read. Any remaining unindexed asset kind, or a reference resolving outside every scanned root entirely (most commonly a registry package's own copy under `Library/PackageCache` — a built-in shader or texture bundled with a Unity package, for example), still has no node; references into those are reported as unresolvable, not missing user code.
- **Addressables are indexed only as generic ScriptableObjects** — there is no dedicated reader for Addressables groups, entries, or addresses.

## Operational notes

- **First build is a one-time cost** — a few seconds on a typical project, up to a few minutes on a very large one, behind a progress bar. Updates after that are incremental and near-instant.
- **The graph is a cache.** If it ever looks stale or wrong, `/hades:rebuild-graph` (or **Rebuild** in the app's Projects view) regenerates it from scratch.
- **A very deep GameObject hierarchy (~65–512 levels) can surface a raw serializer error instead of a clean message** — .NET's default `System.Text.Json` `MaxDepth` (64) is hit during serialization before `inspect_asset`'s own 512-level depth guard gets a chance to emit its friendly "nested more than 512 levels deep" message.

## Windows (beta)

The analysis itself is not platform-specific: the Windows shell renders, the core decides, and it is the *same* core the Mac runs. What is newer on Windows is everything around it — the installer, the tray, process supervision — and a class of environmental hazard that no CI run can reach. These are named individually because a solo maintainer cannot reproduce what he cannot see, and a named risk is one you can recognise in your own setup.

- **Long paths.** Real Unity projects routinely exceed 260 characters under `Library/PackageCache/com.unity.*@x.y.z/…`. .NET is long-path capable, but anything that shells out may not be. `hades diagnose` reports whether long paths are enabled on your machine.
- **OneDrive-redirected folders.** Documents is OneDrive-redirected by default on consumer Windows 11. With Files On-Demand, a full index can trigger mass hydration of placeholder files, and placeholder timestamps and sizes can undermine the incremental change detection that keeps rebuilds fast. **Untested.**
- **Antivirus.** Real-time scanning can hold a SQLite WAL file open mid-checkpoint, which surfaces as "database is locked" with no reliable repro. It can also produce file-watcher event storms, and spawn heuristics may object to an unsigned executable running from `%LOCALAPPDATA%`. **Untested.**
- **AppLocker / WDAC.** Managed and enterprise machines commonly block execution from under `%LOCALAPPDATA%` — which is exactly where a per-user install lives. If your machine has such a policy, Hades will not run from there, and that is the policy working as intended rather than a bug. **Untested.**
- **Unity Hub installed on a non-default drive.** ~~Untested.~~ **Tested, and it was broken — now fixed.** Editor discovery looked only in `C:\Program Files\Unity\Hub\Editor`, so "Open in Unity" refused to launch an editor that was installed and working in a relocated Hub root. Hades now also reads the root Unity Hub records in `secondaryInstallPath.json`, and searches both — editors installed before the root was changed stay in the old one.
- **Path case-insensitivity.** `D:\Proj` and `d:\proj` are the same directory to Windows but not to an ordinal string comparison, which makes duplicate project nodes a plausible failure mode.
- **The ARM64 build has never been executed.** It is built, and its binaries verified genuinely native rather than silently x64 — nothing beyond that.

If something misbehaves, run `hades diagnose` and put its output in the issue. It reports the OS build, architecture, runtime, long-path status and storage layout, and it exists specifically because these are the failures that cannot be reproduced from a description. It never prints your access token.

## Maturity

Hades has been field-tested on a large production Unity project, but **not yet across many projects, Unity versions, or OS versions.** Treat surprising results on your project as a chance to help — file an issue with a concrete repro.

**macOS is the more proven platform; Windows is beta.** Both run the same core and pass the same suites, so the analysis is the same. The difference is exposure: the Mac app has real field use behind it and the Windows one does not yet.

## How limitations surface at runtime

**They don't.** This is the part worth knowing, because an earlier version of this page said the
opposite.

Hades v1.2 attached runtime signals to tool responses — a `confidence` block carrying `level` and
`result_status`, a `static_analysis_coverage` factor, `nested_by` on `find_references_to`,
`package_scan` and `scan_health`. **The current app emits none of them.** Verified against the
source: no such field appears anywhere in `Core/src`.

So a v2 tool response tells you what it found and nothing about how much to trust it. A reference
count of zero looks exactly like a reference count of zero for an asset reached only by reflection.
That makes this page load-bearing rather than optional: the boundaries above are the ones you have
to hold yourself, because nothing will remind you at the point of use.

Restoring some form of this is a reasonable thing to want back, and the v1.2 design is written down
— see [Interpreting results](Documentation/Retired/interpreting-results.md), an archived v1.2 doc
kept for exactly that reason. **It describes an architecture that no longer runs. Do not read it as
a description of the current app.**
