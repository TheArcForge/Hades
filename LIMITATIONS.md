# Hades — Limitations

Hades is honest about what it can and can't know. Most of the boundaries below are **by design** (a consequence of static analysis), not bugs — and the tools surface them in their confidence signals at runtime. This page exists so you know where the edges are before you hit them.

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

- **Reference queries** — "what references X" for scripts and prefabs. Check the `nested_by` field and the confidence block before treating an asset as unused.
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

## Operational notes

- **First build is a one-time cost** — a few seconds on a typical project, up to a few minutes on a very large one, behind a progress bar. Updates after that are incremental and near-instant.
- **The graph is a cache.** If it ever looks stale or wrong, `/hades:rebuild-graph` (or **Hades → Rebuild Graph**) regenerates it from scratch.

## Maturity

Hades v1 has been field-tested on a large production Unity project, but **not yet across many projects, Unity versions, or platforms.** Treat surprising results on your project as a chance to help — file an issue with a concrete repro.

## How limitations surface at runtime

You don't have to remember this page — the tools tell you. Watch for these in tool responses:

- `confidence` blocks with `level` (`high`/`medium`/`low`) and `result_status` (`complete`/`partial`/`uncertain`/`error`)
- a `static_analysis_coverage: partial` factor listing blind spots (reflection, runtime/string dispatch, DI, dynamic instantiation)
- `nested_by` on `find_references_to` — structural parents that embed an asset even when `reference_count` is 0
- `package_scan: degraded` / `supertypes_external_unresolved` — package/external types may be unindexed
- `scan_health` (`csharp` / `meta` / `addressables` / `packages`) — per-scanner status

See [Interpreting results](Documentation/Retired/interpreting-results.md) for what each one means and how to act on it.
