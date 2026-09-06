# Hades — Performance Benchmark

Benchmark recorded against a representative development project (Unity 6000.0).

---

## Test Project Profile

| Metric | Value |
|--------|-------|
| Graph nodes | 163,449 |
| Graph edges | 161,696 |
| Graph database size | 128 MB |
| Trace database size | 14 MB (671 traces) |
| Project assets | ~20 (scenes, prefabs, materials, scriptable objects) |
| Package scripts scanned | 6,268 |
| Script types indexed | 13,264 |
| Script methods indexed | 143,868 |
| Memory files | 6 |

The project has few assets but a large graph due to the Node.js script scanner indexing the full `Library/PackageCache`. This makes it representative of a medium-to-large project by graph size (163k nodes falls in the "Large" tier by node count) while having minimal project-tier scanning overhead.

---

## Build Performance

### Full rebuild (project assets only, scripts already cached)

| Run | Duration |
|-----|----------|
| 1 | 229 ms |
| 2 | 200 ms |
| 3 | 198 ms |
| 4 | 167 ms |
| 5 | 193 ms |
| **Average** | **197 ms** |

The full rebuild rescans all project assets (scenes, prefabs, materials, scriptable objects) and resolves GUID edges. With only ~20 project assets, this completes in under 230ms. Script nodes from the package cache persist across rebuilds.

### First boot (cold start with script scanning)

Measured across 23 `lifecycle.graph_init` traces:

| Metric | Value |
|--------|-------|
| Average | 185 ms |
| Max | 236 ms |

Note: these measurements are for graph init *after* the Node.js scanner has already populated script nodes on a prior boot. The true cold-start first boot (including the Node.js scanner) was measured during Phase 5c at approximately 10 seconds for 6,268 scripts → 163,449 nodes.

### Script scanning (Node.js scanner, Phase 5c)

Measured across 78 `graph.scan.ScriptScanner` traces:

| Metric | Value |
|--------|-------|
| Average | 1,749 ms |
| Max | 39,596 ms |

The max (39.6s) represents the initial full scan of all package scripts. Subsequent scans are incremental and average under 2 seconds.

### Incremental update

Measured across 20 `graph.build.incremental` traces:

| Metric | Value |
|--------|-------|
| Average | 2,414 ms |
| Max | 28,630 ms |

The max (28.6s) includes cases where the incremental update triggered a re-scan of changed package scripts. Typical single-asset changes complete much faster.

---

## Query Performance

Measured from Charon trace data (total_duration_ms including MCP overhead):

| Tool | Calls | Avg (ms) | Max (ms) | Architecture §2.6 Target |
|------|-------|----------|----------|--------------------------|
| `hades_ping` | 17 | 3 | 13 | — |
| `hades_status` | 9 | 20 | 65 | — |
| `search_by_name` | 69 | 33 | 539 | 5-20ms (LIKE scan) |
| `query_graph` (→ `graph_query`) | 10 | 117 | 503 | 10-100ms (aggregations) |
| `find_references_to` | 2 | 9 | 16 | 1-5ms (one-hop) |
| `trace_dependencies` | 3 | 350 | 1,031 | 10-50ms (5-hop) |
| `find_orphan_scripts` (→ `graph_query`) | 2 | 1,114 | 2,215 | — |
| `find_components_using_pattern` (→ `graph_query`) | 3 | 436 | 776 | — |
| `get_project_summary` | 7 | 17 | 73 | — |
| `get_scene_summary` | 1 | 3 | 3 | — |
| `component_find` (→ `graph_query`) | 1 | 12 | 12 | — |
| `find_prefabs_with_component` (→ `graph_query`) | 1 | 4 | 4 | — |
| `asset_find` (→ `graph_query`) | 1 | 186 | 186 | — |
| `recall_memory` | 7 | 5 | 7 | — |
| `validate_memory` | 3 | 7 | 14 | — |

_Tool names above reflect the pre-Phase-10 surface these numbers were measured against. `query_graph`, `find_orphan_scripts`, `find_components_using_pattern`, `component_find`, `find_prefabs_with_component`, and `asset_find` no longer exist as separate tools — all six were consolidated into `graph_query`'s filter parameters (see the tool's own description). The historical per-shape timings remain informative for what each access pattern costs._

### Comparison to §2.6 targets

| Query type | Target | Actual | Status |
|------------|--------|--------|--------|
| Lookup by GUID | < 1ms | 0ms (from traces) | ✅ |
| Lookup by path | < 1ms | — (not directly measured) | — |
| List all nodes of a type | 1-10ms | 0ms (`find_by_type` span) | ✅ |
| Find references (one-hop) | 1-5ms | 9ms avg (16ms max) | ⚠️ Slightly over |
| Find dependencies (5-hop) | 10-50ms | 350ms avg (1,031ms max) | ❌ Over target |
| Full-text search | 5-20ms | 33ms avg (539ms max) | ⚠️ Avg over |
| Project-wide aggregations | 10-100ms | 117ms avg (503ms max) | ⚠️ Avg slightly over |

**Notes on misses:**
- `trace_dependencies` max (1,031ms) is inflated by a wildcard `search_by_name` scan across 163k nodes before traversal. The actual traversal span took 0ms. This is a query planner issue, not a graph performance issue.
- `search_by_name` max (539ms) occurred during concurrent rebuild. Average (33ms) is acceptable for the graph size.
- `graph_query` (measured as `query_graph`, its pre-Phase-10 name) max (503ms) involved full ScriptType enumeration (13,264 results). Typical queries are well under 100ms.
- All misses are within agent reasoning latency (100ms-2s) and do not create perceptible delays in agent interactions.

---

## Scanner Breakdown

Per-scanner performance from Charon traces:

| Scanner | Traces | Avg (ms) | Max (ms) |
|---------|--------|----------|----------|
| ScriptScanner (Node.js) | 78 | 1,749 | 39,596 |
| ScriptableObjectScanner | 37 | 4 | 35 |
| PrefabScanner | 6 | 9 | 24 |
| SceneScanner | 4 | 5 | 12 |
| MaterialScanner | 4 | 2 | 5 |
| ShaderScanner | 1 | 3 | 3 |

The script scanner dominates build time by orders of magnitude. Project-tier scanners (scenes, prefabs, materials, scriptable objects) are fast enough to be negligible.

---

## Assessment

The benchmark graph (163k nodes, 128MB) is representative of a medium-to-large project. Performance is acceptable for agent interactions:

1. **Rebuild**: 197ms average for project asset rebuild. Well within usability.
2. **Queries**: Most queries complete in under 50ms. Outliers (search, aggregation) stay under 1s.
3. **Storage**: 128MB graph + 14MB traces. Reasonable for the node count.
4. **First boot**: ~10s for cold start including full script scanning. Acceptable as a one-time cost.

A true 50k+ *project asset* benchmark (50k scenes, prefabs, textures — not package scripts) would stress the project-tier scanners differently and is recommended for validation on a real game project.

**Note:** This benchmark predates the v1.0.0 / Phase 10 release. Phase 9 (v0.9.5) added the MetaScanner (Asset nodes for textures, models, audio, animation, fonts via `.meta` file reads), tree-sitter C# parser (AST-based cross-file reference extraction replacing regex), and Unity builtin type seeding (4,001 ScriptType nodes from Unity assemblies); Phase 10 built further on that foundation. These additions increase total node/edge counts and may affect build and query timings. A fresh benchmark against the current codebase is recommended.
