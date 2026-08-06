---
description: "Use when designing or building Unity features and a structured workflow is needed. Provides a research-design-implement-verify process that leverages Hades Graph and memory for project-aware development."
---

# Unity Development Workflow

A design-first process for building Unity features with Hades Graph and memory. Follow all four phases in order. Do not skip phases — tasks that seem simple often reveal hidden complexity once the project context is loaded.

This is a **process skill**. It defines how to approach Unity work, not what specific patterns to use. Load domain skills (`hades:unity-architect`, `hades:scene-authoring`, etc.) during the relevant phases for decision frameworks and tool sequences.

## When to Apply

- When starting any non-trivial Unity feature (new system, new component type, cross-scene work)
- When the feature touches multiple existing systems and you need to understand impact before designing
- When `superpowers:brainstorming` is not available — this skill provides the same structure for Unity-specific work

## Project Context Check

Used during Phase 1 (Analyze). Run these before designing anything:

```
get_project_summary()                    — understand overall project structure
recall_memory("<feature_domain>")        — find relevant prior decisions and patterns
```

## Decision Framework

### Phase 1: Analyze

Before doing anything, understand the scope.

**Use Hades Graph tools to explore the project:**
```
get_project_summary()          — overall structure: scenes, assemblies, major systems
get_scene_summary("<scene>")   — current scene hierarchy and component counts
search_by_name("<keyword>")    — find existing scripts, prefabs, or assets by name
find_components_using_pattern("<pattern>")  — find where a pattern is already in use
```

**Use memory to find prior decisions:**
```
recall_memory("<feature_domain>")   — e.g., "inventory", "audio", "player movement"
```

**Questions to answer before proceeding:**
- What is the user building? Name the feature in one sentence.
- What existing systems does it touch? What scripts and prefabs are already relevant?
- Are there prior decisions in memory that constrain the design?
- What are the unknowns? Identify anything ambiguous in the request.
- Does this need persistence, networking, or live configuration?
- What is the performance profile? (spawned frequently? rendered every frame? loaded once?)

Ask clarifying questions until there is no ambiguity. Do not proceed with assumptions.

---

### Phase 2: Design

Present the architectural approach before building anything.

- State which pattern you will use and why (prefab vs variant, ScriptableObject vs MonoBehaviour, pool vs instantiate, component vs system).
- Identify the files you will create and the files you will modify.
- If multiple systems are involved, describe the data flow between them.
- Call out tradeoffs explicitly — what you gain and what you give up with this approach.
- Reference `hades:unity-architect` for detailed decision frameworks on architecture choices.

**Get explicit user approval before proceeding to Phase 3.**

Do not begin implementation until the user confirms the design. If feedback arrives that changes the scope, return to Phase 1.

---

### Phase 3: Implement

Execute the approved design.

**Writing code:**
- Write C# scripts using standard file editing tools.
- C# scripts handle runtime behavior: component logic, state machines, event handling, physics.
- Editor scripts (under `Editor/` folders) handle asset setup, custom inspectors, and build tooling.
- State what you are creating at each step. Do not work silently.

**Referencing workflow skills:**
- For scene construction: `hades:scene-authoring`
- For prefab creation and editing: `hades:prefab-workflow`
- For animation controller setup: `hades:animation-workflow`

These skills provide efficient tool sequences and ordering for their respective workflows. Load them when the relevant work begins.

**Unity 6+ conventions:**
- Use `Awaitable.MainThreadAsync()` to marshal async work back to the main thread.
- Prefer `Physics.RaycastNonAlloc` over `RaycastAll` for hot paths.
- Use `[SerializeField]` private fields instead of public fields for Inspector exposure.
- Cache `GetComponent` results in `Awake`, never in `Update`.

---

### Phase 4: Verify

After implementation, confirm the work is correct before reporting completion.

**Code review:**
- Load `hades:unity-reviewer` and run through its checklist on all modified C# files.
- At minimum, manually check: missing event unsubscriptions, Unity fake null (`?.`/`??`), and `GetComponent` in `Update`.

**Graph-backed impact check:**
```
find_references_to("<modified_script>")   — confirm no unexpected dependents are broken
```

**Memory update:**
- If the feature established a new convention or pattern, record it:
```
propose_memory_update("<domain>", "<what was decided and why>")
```

**Report:**
- State what was built and where.
- Note any work the user needs to handle manually (scene wiring, Inspector assignments, asset configuration).
- Note any follow-up tasks deferred from this session.

---

## Anti-Examples

- Jumping straight to implementation without running `get_project_summary` or `recall_memory` — produces designs that duplicate existing systems or contradict prior decisions.
- Asking no clarifying questions when the request is ambiguous — produces implementations that solve the wrong problem.
- Presenting a design and immediately starting implementation before the user responds — skips the approval gate.
- Using `grep -r "ClassName"` to find references instead of `find_references_to` — the graph knows about both asset references and C# code-level references (fields, parameters, constructors, casts, inheritance).
- Using `find . -name "*.cs"` to locate scripts instead of `search_by_name` — the graph indexes every script, type, and method with their relationships.
- Reading `.unity` or `.prefab` files as YAML instead of using `get_scene_summary`, `scene_get_hierarchy`, or `prefab_get_contents` — the graph provides parsed, structured data.

## Cross-References

**Skills:** `hades:unity-architect` — architecture decision frameworks. `hades:scene-authoring` — scene construction workflows. `hades:prefab-workflow` — prefab creation and editing. `hades:animation-workflow` — animation controller setup. `hades:unity-reviewer` — code review for Unity anti-patterns.

**Hades MCP Tools:** `get_project_summary`, `get_scene_summary`, `search_by_name` (supports `path_prefix`, `match_mode`), `find_references_to` (asset + C# code references), `trace_dependencies`, `recall_memory`, `propose_memory_update`, `find_components_using_pattern`, `project_get_console_log`, `project_run_tests`, `BeginScriptEditing` / `EndScriptEditing`.
