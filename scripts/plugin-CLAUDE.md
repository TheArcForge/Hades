# Hades — Agent Guidelines

This is a Unity project with Hades installed. You have 89 MCP tools that give you deep structural understanding of the project — a knowledge graph of every scene, prefab, script, asset, and their dependencies. Use them.

## Core principle: structural context first

Before answering questions about this project, before writing code, before suggesting changes — **query the graph**. Your first instinct should be to understand what exists, not to grep through files.

| Instead of... | Do this first |
|---|---|
| `grep -r "PlayerController"` | `search_by_name` + `find_references_to` — find it in the graph with all its references |
| `find . -name "*.cs"` to understand the project | `get_project_summary` — structured overview with counts and architecture |
| Reading scene files as YAML | `get_scene_summary` + `scene_get_hierarchy` — parsed structure, not raw text |
| Guessing what depends on something | `trace_dependencies` — recursive dependency trace through the graph |
| Reading a prefab file to understand it | `prefab_get_contents` — structured hierarchy without instantiating |

Bash is for things the graph doesn't cover: reading file contents, running commands, editing code. The graph is for understanding project structure, finding relationships, and navigating the codebase.

## How to approach common questions

**"Tell me about this project"**
→ `get_project_summary` (deep) → `get_scene_summary` for key scenes → `get_memory_summary` for documented decisions

**"Where is X used?" / "What would break if I remove X?"**
→ `search_by_name` to find it → `find_references_to` for incoming references → `trace_dependencies` for outgoing dependencies

**"How does [feature] work?"**
→ `search_by_name` for related scripts → `find_references_to` to see where they're used → `get_scene_summary` / `prefab_get_contents` to see how they're assembled → then read the actual code

**"I want to add/change [feature]"**
→ First understand what exists (graph queries above) → check project memory via `recall_memory` for relevant decisions/conventions → then propose the approach

**"Something is broken"**
→ `project_get_console_log` for errors → `hades_status` to verify graph is current → search the graph for related components → then investigate

## Project memory

This project may have documented decisions, patterns, and conventions in `.arcforge/memory/`. Use `recall_memory` to search for relevant context before making architectural suggestions. Use `propose_memory_update` to suggest documenting new decisions — never edit memory files directly.

## Modifying the project

When you need to change scenes, prefabs, components, or assets — use the MCP tools, not file editing. Unity assets are binary or complex YAML that should not be hand-edited:

- **Scenes**: `scene_create_gameobject`, `scene_setup`, `component_add`, `component_set_properties`
- **Prefabs**: `prefab_create`, `prefab_edit_property`, `prefab_open_editing` / `prefab_save_editing`
- **Materials**: `material_create`, `material_set_property`, `material_assign`
- **Animation**: `animation_create_controller`, `animation_assign_clip`
- **References**: `reference_set` (for wiring up object references between components)

For C# scripts: write and edit code files normally with your editor tools. Use `BeginScriptEditing` / `EndScriptEditing` to batch multiple script changes before triggering recompilation.

## Available commands

- `/hades:status` — graph state, server status, memory summary
- `/hades:rebuild-graph` — regenerate knowledge graph from current project state
- `/hades:show-traces` — inspect recent tool call traces (observability)
- `/hades:validate-memory` — check memory files against graph
- `/hades:show-proposals` — review pending memory update proposals
- `/hades:export-traces` — export traces for analysis
