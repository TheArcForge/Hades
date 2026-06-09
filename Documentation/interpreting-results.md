# Interpreting Hades results

Hades is built to tell you how much to trust each answer. Most tool responses carry a `confidence` block, and several tools add result-specific signals. This guide explains what those signals mean and how to act on them — so "no results" never gets mistaken for "definitely none."

> TL;DR: **structural facts → trust; inferred C# relationships → a strong lead, verify before destructive changes.** See [What to trust](../README.md#what-to-trust-and-what-to-verify) and [Limitations](../LIMITATIONS.md).

## The confidence block

Most query tools return a `confidence` object alongside their result:

```json
"confidence": {
  "level": "medium",
  "result_status": "partial",
  "factors": [
    { "factor": "static_analysis_coverage", "value": "partial",
      "blind_spots": ["reflection", "runtime/string-based dispatch", "DI containers", "dynamic instantiation"] }
  ],
  "recommendations": ["'No references' means none were statically detected; check 'nested_by' before treating an asset as unused"]
}
```

- **`level`** — `high` / `medium` / `low`. A quick "how much should I trust this."
- **`result_status`** — `complete` / `partial` / `uncertain` / `error`. `partial` means the tool worked but a known gap may affect completeness; `error` means it couldn't answer (e.g. graph unavailable).
- **`factors[]`** — the *why* behind the level (see below).
- **`recommendations[]`** — concrete next steps when the answer isn't fully certain.

## Factors you'll see

| Factor | Value(s) | What it means | What to do |
|---|---|---|---|
| `graph_freshness` | `current` / `rebuilding` | Whether the graph is up to date or a rebuild is in progress | If `rebuilding`, retry after it finishes for full results |
| `static_analysis_coverage` | `partial` (with `blind_spots`) | The answer comes from static analysis, which can't see reflection, runtime/string dispatch, DI containers, or dynamic instantiation | Treat "none found" as "none found *statically*" — verify before deleting/refactoring |
| `package_scan` | `degraded` | The package-tier C# scan didn't fully complete, so types from packages may be unindexed | Re-run `/hades:rebuild-graph`; meanwhile expect some cross-package edges to be missing |
| `supertypes_external_unresolved` | a count `N` | `N` base types / interfaces point at precompiled/external types Hades can't index | An empty `implements`/`inherits_from` isn't necessarily "none" — `N` of them are external |

## Result-specific signals

### `nested_by` (on `find_references_to`)
A separate list of **structural parents** that embed the target — prefabs that nest it, or prefab variants derived from it — shown **even when `reference_count` is 0**. A prefab can be unused by direct reference but still embedded somewhere.

> **Rule:** before treating an asset as "unused / safe to delete," check both `reference_count` *and* `nested_by`.

### `scan_health` (on `get_project_summary`)
Per-scanner status so you can see at a glance what's fully indexed:

```json
"scan_health": { "csharp": "ok", "meta": "ok", "addressables": "not_installed", "packages": "ok" }
```
Values: `ok` / `degraded` / `not_installed` / `unknown`. If a scanner is `degraded` or `unknown`, results that depend on it may be incomplete.

### `edge_resolution_percent` (on `get_project_summary`)
The share of edges Hades **attempted** to resolve that it *did* resolve — **not** a project-completeness score. It deliberately excludes references that can never resolve (asset types Hades doesn't index, framework/BCL types). A high number means "the links we tried to make, we made," not "we captured every relationship in your project."

## Putting it together — trust tiers

| Tier | Tools / answers | How to use |
|---|---|---|
| **Trust** | `get_scene_summary`, `prefab_get_contents`, `material_get_properties`, `asset_get_info`, type → file lookups, `search_by_name` | Use directly; these read serialized project data |
| **Verify** | `find_references_to` (scripts/prefabs) | Strong lead. Check `nested_by` + confidence before "unused/safe to delete" |
| **Confirm** | inheritance / `implements` via `query_graph`, `trace_dependencies` (C#), `find_prefabs_with_component` | Confirm independently when types come from packages/DLLs, generics, or reflection/DI |

## The mental model

Hades is a **navigator, not an oracle.** It makes understanding your project fast, structural, and repeatable — and it tells you where it's uncertain so you stay in the loop for anything irreversible. When the confidence block and your own judgment agree, move fast; when a factor flags a gap, take the extra look. That's the whole contract.
