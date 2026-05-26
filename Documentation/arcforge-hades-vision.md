# Hades — Vision Document

**Version:** 1.0
**Status:** Pre-development north-star
**Last updated:** 2026-05-09

---

## 0. About this document

This is the vision document for **Hades** — an integrated AI development stack for Unity. It captures what the product is, who it serves, what problems it eliminates, and how the parts fit together at a high level.

This document is written in an engineering-honest tone. It names trade-offs, acknowledges unknowns, and avoids the inspirational language that vision documents often slide into. Where claims are speculative, they are marked as such. Where competitors already exist, they are named. Where the design has open questions, those questions are listed in the final section rather than glossed over.

This document does **not** prescribe implementation details. Those belong in the Architecture document. It does not prescribe sequence or scope cuts. Those belong in the Roadmap. The vision is the destination; the other two documents are the map and the journey.

---

## 1. Executive summary

Hades is an integrated AI development stack for Unity projects. It augments Claude Code (and any MCP-compatible coding agent) with three capabilities that no Unity tool ships together today:

1. **Hades Graph** — a Unity-aware semantic knowledge graph of the project, built from inside the Unity Editor using `AssetDatabase` and `SerializedObject` introspection. It captures scenes, prefab variants, ScriptableObject instances, addressable groups, render pipeline configuration, and the relationships between them.

2. **Hades Charon** — observability infrastructure that instruments every MCP tool call with OpenTelemetry traces, eval scoring, and a local trace viewer. Built first as an internal engineering necessity for ArcForge itself, exposed second as a user-facing feature for debugging AI workflows.

3. **Hades Asphodel** — persistent project memory in version-controlled markdown files. Captures architectural decisions, learned team patterns, and inferred preferences across sessions. Travels with the project in git.

These three layers connect through feedback loops. The Graph emits change events to Asphodel. Charon traces feed Asphodel pattern detection. Asphodel injects context into the agent's system prompt. Each layer makes the others stronger.

A **Skills layer** sits alongside, providing the architectural decision frameworks and code patterns that the agent draws from when reasoning about Unity-specific problems.

The product addresses a structural failure mode of current AI coding tools when applied to Unity projects: generic code indexers treat Unity projects as plain text and miss the 80% of project semantics that lives outside `.cs` files (in scenes, prefabs, ScriptableObjects, asset references). Existing Unity-specific MCP servers expose action tooling but provide no semantic understanding of the project they operate on.

Hades targets this gap.

---

## 2. The problem space

### 2.1 Why Unity is structurally different from typical codebases

Modern AI coding tools — Cursor, Claude Code, GitHub Copilot, Cline, Aider — are optimized for codebases that match a specific shape:

- Plain source files containing the majority of program logic
- Deterministic import graphs resolvable by static analysis
- Textual APIs with stable signatures
- File-level chunking that maps cleanly to semantic units

Unity projects do not fit this shape. A Unity project is closer to:

- A serialized object graph persisted across hundreds of YAML files
- An asset dependency tree connected by GUIDs and `fileID` references
- A configuration-driven runtime system where critical behavior lives in inspector values, not code
- An editor-state ecosystem where the same file means different things at edit-time vs runtime

The implication is concrete. When a developer asks an AI agent to "add an inventory system that integrates with our existing patterns," the agent needs to know:

- Which `ScriptableObject` types act as data containers in this project
- Which event channel pattern (if any) the project uses for inter-system communication
- Whether the project uses Addressables or Resources for asset loading
- Which render pipeline is active and what custom features it has
- What components hang on which prefab variants
- How the existing systems wire together at the GameObject hierarchy level

None of this information is reliably available from reading `.cs` files alone. It lives in serialized YAML, in `.meta` files, in inspector overrides, in asset import settings.

### 2.2 Why generic code indexers fail here

A generic semantic code indexer (GitNexus, CocoIndex, codebase-memory-mcp, Cognee) parses source files into a knowledge graph using language-agnostic tools like tree-sitter. These tools index the C# in a Unity project competently. They produce nothing for the rest of the project.

Specifically, generic indexers:

- See `.unity` scene files as 3,000-line YAML documents and either skip them or treat them as plain text
- Cannot resolve `fileID` and `guid` cross-references between assets
- Have no concept of prefab variants, override layers, or nested prefab hierarchies
- Treat `ScriptableObject` instance assets identically to their type definitions, missing the actual data
- Ignore addressable group configuration, build settings, and render pipeline assets entirely

This is not a deficiency of those tools. They were not built for Unity. The failure is in applying them to Unity projects and expecting domain understanding to emerge.

### 2.3 Why existing Unity MCP servers fail differently

There are 155+ Unity-specific MCP servers indexed on PulseMCP as of mid-2026. The most notable include IvanMurzak's Unity-MCP (2.6k stars), CoderGamester's mcp-unity (1.7k stars), and CoplayDev's unity-mcp.

These servers solve a different problem. They expose action tooling — the ability for an agent to manipulate the Unity Editor, create GameObjects, modify components, save assets. They do this competently. None of them currently provide:

- A semantic graph of the project the agent can query for relationships
- Persistent memory of architectural decisions across sessions
- Observability into agent behavior for debugging or evaluation
- Domain-specific understanding of how the project is structured

When an agent connected to these tools is asked to "add an inventory system," it must rediscover the project structure on every session. It runs `grep`, reads files, inspects the hierarchy through repeated tool calls. It burns tokens on exploration that the developer already knows is unnecessary. And because exploration is shallow, the suggestions it produces are often inconsistent with established project patterns.

### 2.4 The pain points this creates

The day-to-day failure modes that follow from this structural gap:

**Pattern drift.** The agent suggests solutions inconsistent with how the rest of the project is built. It uses `UnityEvent` when the project uses `ScriptableObject` event channels. It uses `Resources.Load` when the project uses Addressables. Each suggestion that drifts from established patterns either gets rejected (wasted time) or accepted and creates technical debt (worse outcome).

**Token waste on rediscovery.** Every session begins with the agent re-exploring the project. A 30-minute session might spend the first 5 minutes on tool calls that establish what the agent already established yesterday. Across a team of 10 developers running multiple sessions per day, this is significant cost.

**Hallucination against existing code.** The agent proposes a `PlayerHealth` component when one already exists at `Scripts/Player/Health.cs`. It references methods that don't exist on classes it didn't read. It suggests file paths that conflict with what's there.

**No memory across sessions.** Decisions made yesterday are forgotten today. "We decided to use Addressables for level loading" → next session, agent suggests Resources. "Our team prefers minimal refactoring scope" → next session, agent rewrites half the file.

**No way to debug agent behavior.** When the agent does something wrong (modifies a prefab incorrectly, references a non-existent asset), there is no trace of what it knew, what it queried, what it decided. The developer reverts the change and tries again with a different prompt, hoping for better. There is no systematic way to understand why the failure happened or to prevent recurrence.

**No team consistency.** Two developers in the same studio get different agent behavior because their personal context windows have different histories. Project conventions cannot be shared across the team's AI assistants.

These are not theoretical pains. They are the daily friction of using AI tools on Unity projects in 2026.

---

## 3. The vision

### 3.1 What Hades is

Hades is an integrated stack of three connected systems that collectively give an AI agent durable, project-specific understanding of a Unity codebase. It is delivered as:

- A **Unity Package** (UPM) that runs inside the Unity Editor, performing the introspection that builds the knowledge graph and emits observability traces
- An **MCP Server** (Node.js) that exposes the graph, memory, and observability data as tools to MCP-compatible coding agents
- A **Skills library** distributed as a Claude Code plugin, providing architectural decision frameworks calibrated to Unity development
- A **local web dashboard** for viewing traces, evaluating agent behavior, and inspecting the graph

The agent connects to the MCP server. The MCP server reads from artifacts produced by the Unity Package. The Skills layer is loaded into the agent's context. The user works in the agent (Claude Code, Cursor, etc.) and in their Unity Editor side by side, with the two staying synchronized.

### 3.2 What Hades is NOT

Explicit non-goals. These boundaries matter because they keep scope coherent.

- **Not a replacement for existing Unity MCP servers' action tooling.** Hades does not aim to be the most complete editor-manipulation MCP. Existing tools are adequate at that and we will not duplicate the surface area unnecessarily. Where action tools are needed, we provide the minimum required for our knowledge-and-memory features to function.
- **Not a code generation tool.** It does not generate code itself. It provides the agent with the context the agent needs to generate code well.
- **Not a chat interface.** UniClaude was a chat interface in the Unity Editor. Hades is not. The agent lives in Claude Code or Cursor or another MCP-compatible client.
- **Not a Unity Editor replacement.** The Unity Editor remains the source of truth. Hades is read-mostly; it observes and structures, it does not own state.
- **Not an offline tool.** The graph is built from a running Unity Editor (with batch-mode fallback for CI). Without Unity, there is no graph.
- **Not a hosted SaaS.** Local-first. The graph, traces, and memory live on the developer's machine and in the project's git repository. Nothing is sent to ArcForge servers because there are no ArcForge servers.

### 3.3 The four pillars

#### 3.3.1 Hades Graph

A semantic knowledge graph of the Unity project. Built from inside the Unity Editor using `AssetDatabase` and `SerializedObject` APIs (not by parsing YAML from disk — that path is a known engineering trap and has been explicitly rejected; see Architecture document).

Nodes represent the meaningful entities of a Unity project: Scenes, GameObjects, Components, Prefabs, Prefab Variants, ScriptableObject instances, Scripts, Materials, Shaders, Render Pipeline Assets, Addressable Entries, Audio Mixers, Animator Controllers, and others. Edges represent the relationships: contains, references, inherits, instantiates, listens to, uses material, depends on.

The graph is queryable through the MCP server using a fixed set of high-value queries rather than ad-hoc Cypher-like syntax. Examples:

- `find_components_using_pattern(pattern)` — find all components matching a structural pattern (e.g., implementing a specific interface, inheriting from a base type)
- `trace_asset_dependencies(asset_path)` — recursively follow dependency edges from an asset to identify everything it touches
- `find_prefabs_with_component(component_type)` — locate every prefab containing a specific component
- `analyze_render_pipeline()` — summarize URP/HDRP/built-in usage, custom features, and render features
- `find_orphan_scripts()` — scripts not referenced by any prefab, scene, or other script
- `get_project_summary(depth)` — high-level project overview at a configurable depth

The graph is incrementally updated. When the user edits a scene, prefab, or script, hooks (`AssetPostprocessor`, `EditorApplication.projectChanged`) detect the change and update only the affected nodes and edges. A full rebuild is available on demand and runs in seconds for medium-sized projects.

The graph is persisted to `.arcforge/graph.db` (SQLite-based) in the project root. It is gitignored by default — each developer's machine builds its own. A future option allows opt-in committing for shared baselines.

#### 3.3.2 Hades Charon

Observability infrastructure for everything the agent does. Charon — the ferryman who transports souls between worlds — is the metaphor. Each agent action is a soul; Charon transports it and keeps a record.

Implemented as OpenTelemetry instrumentation on every MCP tool call, every graph query, every memory read, every action performed against the Unity Editor. Each operation becomes a span. Spans nest into traces. Traces map to user-visible interactions ("user asked X", "agent did Y, Z, W in response").

The trace structure captures:

- Input context (what the agent received)
- Tool calls invoked, with parameters and responses
- Latency per step
- Token usage (where measurable)
- Custom attributes: project size, graph version, model in use
- Outcome (accepted, rejected, edited by user)

Charon serves two audiences with the same data:

**Internal (engineering necessity).** When ArcForge itself misbehaves — an MCP tool returns wrong data, the graph gives stale information, a skill produces inconsistent suggestions — traces are how we debug. Without Charon we are debugging blind. This is not a feature, it is a precondition for shipping a product that touches user files.

**External (user feature).** Developers using ArcForge can inspect traces to understand why their agent did what it did. When the agent makes a confusing suggestion, the trace shows what the agent saw and what it decided. This is genuinely novel for AI dev tools — most are black boxes — and serves as a debugging aid that other Unity AI tools simply do not offer.

The trace data also feeds an evaluation framework. Traces tagged with outcome (accepted, rejected, edited) accumulate into a dataset that can be replayed against new versions of skills, new prompts, or new models. This eval dataset is itself a candidate open-source artifact — a Unity AI benchmark that other tools could measure against.

A local web dashboard renders traces in real time, runs locally on `localhost`, and never sends data anywhere by default.

#### 3.3.3 Hades Asphodel

Persistent project memory. Asphodel — the field where souls dwell after passing — is the metaphor. Most knowledge about a project is not deserving of Elysium (specifically curated archive) or Tartarus (active alarm), it is just persistently *there*, available when needed.

Implemented as version-controlled markdown files under `.arcforge/memory/` in the project root.

Two tiers:

**Tier 1: Explicit memory** — human-curated, human-readable, git-tracked. Files such as:

- `decisions.md` — architectural choices and their context
- `patterns.md` — established patterns the project uses
- `conventions.md` — naming, structure, and style conventions
- `pitfalls.md` — known traps and historical bug patterns

The agent can read these directly via MCP tools. The agent can propose updates that the developer reviews before committing. The developer can edit these files directly in any text editor.

**Tier 2: Inferred memory** — auto-generated from observability traces, gitignored by default. The system observes patterns across sessions: which suggestion shapes the user accepts, which they reject, which architectural choices keep recurring. When confidence in an inferred pattern is high, it is promoted to Tier 1 with the developer's review.

A critical design constraint: **memory must self-validate against the graph.** Stale memory is worse than no memory. If `patterns.md` claims "we use ScriptableObject event channels" but the graph shows the project has shifted to `UnityEvent`-based communication, this is a flag, not a silent inconsistency. The MCP server detects mismatches and surfaces them for review.

A second critical design constraint: **memory does not auto-load wholesale into context.** Tier 1 short summaries inject into the system prompt. Detailed memory is retrieved on demand by the agent through a `recall_relevant_memory(query)` tool. This avoids burning tokens on memory the current task does not need.

Because memory lives in the project's git repository, **team sharing is automatic**. A new developer joining the team runs `git pull` and their AI assistant immediately knows the team's patterns, decisions, and conventions. This is — to our current knowledge — not offered by any other Unity AI tool.

#### 3.3.4 Hades Skills

The skills layer sits alongside the three Hades layers. It is a library of architectural decision frameworks and code pattern guides that the agent draws from when reasoning about Unity-specific problems.

Carried over and extended from UniClaude's existing skills:

- Architecture decision frameworks (component design, data modeling, scene architecture, prefab architecture)
- Performance reasoning (when to optimize, what to measure, how to interpret profiler results)
- Code review (severity-tiered review approach)
- Workflow guides (scene authoring, prefab workflow, animation workflow)

Skills planned for expansion based on competitive analysis (specifically against Nice-Wolf-Studio's 35-skill library):

- UI Toolkit and UI architecture
- Networking (Netcode for GameObjects, Mirror, Fishnet decision frameworks)
- AI and behavior (state machines, behavior trees, GOAP, NavMesh)
- Audio architecture
- Input System
- Shader and VFX patterns (URP/HDRP)
- Addressables and asset management
- Common gameplay recipes (health, inventory, save systems, spawn systems)
- ECS/DOTS decision frameworks
- Testing strategy

Skills are distributed as a Claude Code plugin. Where competitors emphasize either decision-heavy prose (UniClaude) or example-heavy code (Nice-Wolf-Studio), Hades Skills aim for both: the decision framework explains *when* and *why*, and concrete code examples show *how* — both calibrated by the project context the agent has from Hades Graph.

The integration with Hades Graph is what makes Skills meaningfully better here than in any standalone skill plugin. A skill that recommends "use SO event channels for inter-system communication" is generic advice in isolation. The same skill applied with knowledge that the project already has 4 SO event channels in `Assets/Events/` and a documented pattern in `patterns.md` becomes specific, actionable, and consistent with the codebase.

### 3.4 The integration

The four pillars are not four products. They are connected:

- **Graph emits change events** that Asphodel uses to track project evolution over time.
- **Charon logs every Graph query** with performance and result data, identifying hot paths and slow queries for optimization.
- **Charon traces feed Asphodel's pattern detection.** Repeated user behaviors become inferred preferences.
- **Asphodel injects relevant context into the agent's system prompt.** What the agent knows about the project starts populated, not blank.
- **Asphodel cross-references the Graph for self-validation.** Memory claims that contradict the current graph state are flagged.
- **Skills consume both Graph state and Asphodel context** to give project-specific rather than generic guidance.

This integration is the moat. Each layer alone is replicable. The interconnected behavior emerges from running them together over time on a real project.

### 3.5 ToS compliance and ecosystem alignment

A direct lesson from UniClaude's sunset: dependence on a single auth path that the platform vendor can revoke is a fatal product risk. UniClaude embedded the Anthropic Agent SDK and used subscription OAuth tokens. When Anthropic enforced its Terms of Service against subscription auth in third-party tools in early 2026, UniClaude's pricing model (and therefore its product viability) collapsed overnight.

Hades is architected to avoid this category of risk entirely.

**Hades does not embed Claude.** There is no Anthropic API call inside any Hades component. There is no subscription OAuth handling. There is no API key management. The agent runtime is provided entirely by first-party tooling — Claude Code, or any other MCP-compatible client (Cursor, Cline, Continue, etc.). Hades exposes capabilities to whatever agent the user already runs.

**The architectural pattern is the one Anthropic explicitly recommends.** Hades is an MCP server plus a Claude Code plugin plus a Unity Package — three standard ecosystem extension points, all officially supported, all designed for third-party use. This is the same pattern used by every major Claude Code extension and every MCP server in the registry.

**Compliance footprint of each component:**

- **Hades Graph and Charon (MCP server)** — the Model Context Protocol is an open standard that Anthropic specifically launched for third-party integration. Building MCP servers is the canonical "build on top of Claude Code" pattern. Zero ToS exposure.
- **Hades Skills (bundled in Unity Package repo)** — skills are markdown files in the same repository, installed as a Claude Code plugin via `/plugin install`. Skills are an officially supported extension format. Zero ToS exposure.
- **Hades Unity Package (UPM)** — pure Unity ecosystem, no Anthropic surface area at all. Zero ToS exposure.
- **Eval datasets accumulated by Charon** — local-first by design. Traces never leave the user's machine without explicit opt-in. No exposure to data-handling concerns.

**Strategic robustness.** Because Hades does not depend on any auth path that Anthropic can unilaterally change, future ToS adjustments do not threaten product viability. If Anthropic changes pricing on Claude Code, the user's relationship with Anthropic changes, but Hades's relationship with the user does not. Hades adds value regardless of whether the user is on Claude Code Pro, Max, or pay-per-token API.

This positioning is more than compliance hygiene. It is the difference between building on platform terms (subject to revocation) and building beside the platform (subject to nothing). UniClaude was the former. Hades is the latter.

---

## 4. Who this is for

### 4.1 Two archetypes

Personas described by role and situation rather than fictional names.

#### The Solo Indie Developer

- Working alone or with one collaborator on a 3D or 2D game, mid-complexity
- 1-3 years into the project, codebase has accumulated structure
- Uses Claude Code or similar coding agent daily
- Currently runs an existing Unity MCP server but feels the agent never "gets" the project
- Pain: token costs (paying out of pocket on API), pattern drift, repeated rediscovery
- Cares about: agent that respects existing code, low setup friction, low ongoing cost
- Does not care about: team features, enterprise integrations, theoretical capability beyond their current need
- Will adopt if: setup is one-command, value is visible within a single session, price is approachable

#### The Small Studio Tech Lead

- Leading a team of 5-15 developers on a mid-size game (mobile, indie console, or PC)
- Codebase is the team's collective work, with established conventions that took years to settle
- Multiple developers running AI agents simultaneously, getting inconsistent results
- Pain: maintaining team consistency, onboarding new developers to project conventions, agents drifting from established patterns
- Cares about: team-shareable project memory, observability for debugging when an agent does something wrong, evaluation of whether agent suggestions match team standards
- Does not care about: solo developer convenience features, AAA-scale capabilities
- Will adopt if: memory-as-code workflow integrates with their existing git practices, observability gives them confidence in agent behavior, can demonstrate consistency improvement on a measurable axis

### 4.2 Categorical breakdown

| Category | Project shape | Primary value from Hades | Adoption probability |
|---|---|---|---|
| Solo indie | 1 dev, mid-size project | Token efficiency, pattern consistency, low setup friction | High if value is visible early |
| Small studio (5-15) | Team coordination, accumulated conventions | Team-shared memory, observability, eval | High if team adoption can be coordinated |
| Mid-size studio (15-50) | Multiple teams, varied subsystems | Same as small studio plus per-system memory partitioning | Medium — procurement and security review are barriers |
| AAA studio (50+) | Custom tooling, security review, restricted egress | Local-first architecture is a fit, but deep custom needs may exceed product surface | Low for v1, possibly later with enterprise features |
| Asset Store / plugin authors | Cross-project libraries used by others | Probably wrong shape for ArcForge — they need cross-project portability we do not provide | Low, deprioritize |
| Game jam / prototyping | Throwaway projects | Wrong shape — overhead exceeds value for short-lived projects | Negligible, ignore |

### 4.3 Who this is explicitly NOT for

- **Developers using AI tools for Unity occasionally.** The setup overhead is not justified for irregular use.
- **Studios with strict no-egress policies that disallow any AI tooling.** ArcForge does not transmit data, but it depends on the agent (Claude Code, etc.) which does. We do not solve this air-gap problem.
- **Teams using Unity for non-game contexts (visualization, automotive, simulation).** The Skills layer is calibrated for game development. Non-game Unity users will get the Graph and Asphodel value but the Skills will feel off-fit.

---

## 5. User scenarios

These scenarios show the day-to-day shape of using Hades, paired against the same situation without it. The "before" cases reflect realistic current behavior using a generic Unity MCP server with no semantic graph or memory.

### 5.1 Scenario: "Add an inventory system that fits how we do things"

**Without Hades.** The developer asks the agent to add an inventory system. The agent responds with questions: "What pattern do you prefer for data storage? ScriptableObjects? Direct serialization? Database?" The developer answers. The agent asks more questions: "Should items be objects or data only?" The developer answers. The agent makes assumptions about everything else and produces an inventory using `UnityEvent` for change notifications. The developer rejects this — the project uses SO event channels — and asks for a rewrite. The agent rewrites. This time it uses SO event channels but creates a new event SO instead of routing through the existing `InventoryUpdated.asset` that handles the same concern. Another rejection. Total time: 25-40 minutes of back-and-forth.

**With Hades.** The developer asks the agent to add an inventory system. The agent reads from Asphodel that the project uses SO event channels (Tier 1 memory) and that the developer prefers minimal refactoring scope (Tier 2 inferred). It queries the Graph for existing event channels and finds `InventoryUpdated.asset`, `PlayerHealthChanged.asset`, and `LevelLoaded.asset`. It queries for existing data containers and finds `ItemConfig.asset` ScriptableObjects. It produces an inventory that uses SO event channels (correct pattern), routes through the existing `InventoryUpdated.asset` (no duplication), and follows the existing `ItemConfig` data shape. Total time: 5-10 minutes, mostly the developer reviewing and accepting.

The difference is not in the agent's intelligence. It is in what the agent knows before responding.

### 5.2 Scenario: "Why is the level loading slowly?"

**Without Hades.** The developer asks why level loading is slow. The agent gives generic advice: profile the load, check Addressables vs Resources, look at scene complexity, consider async loading. The developer profiles, identifies that the bottleneck is in instantiating 47 prefabs at scene start, and asks the agent for specific recommendations. The agent suggests object pooling. The developer asks what to pool. The agent asks for the prefab list. The developer provides it. The agent recommends pooling for some that are already pooled and skips others that should be. Time: 20+ minutes.

**With Hades.** The developer asks why level loading is slow. The agent queries the Graph for the scene structure, finds 47 prefab instantiations at scene start, queries which of those are already in pooling systems (3 of them, in `EnemyPool.cs`), checks Addressables configuration for the rest. It produces a report identifying the 12 prefabs most likely to benefit from pooling, with reference to specific file paths. Time: 3-5 minutes.

### 5.3 Scenario: "Refactor this to fit our patterns"

**Without Hades.** The developer hands the agent a script and asks to refactor it for project consistency. The agent does not know what consistency means here. It applies generic best practices: extract methods, reduce parameters, move logic into separate classes. Some of these match the project's patterns. Some don't. The developer reviews and reverts the parts that don't fit, leaving a partial improvement.

**With Hades.** The agent reads `patterns.md` from Asphodel. It queries the Graph for similar scripts in the project. It produces a refactored version that matches established naming, structure, and architectural choices. The agent's work is consistent with what a senior team member would have done because the agent has the same context.

### 5.4 Scenario: "The agent broke a prefab and I don't know why"

**Without Hades.** A previous session resulted in a broken prefab. The developer reverts the change but wants to understand what happened. There is no record. The agent in the current session has no memory of yesterday. The developer cannot prevent the recurrence because they cannot identify the cause.

**With Hades.** The developer opens the Charon dashboard and finds yesterday's session. The trace shows the agent queried the Graph for prefab references, got an empty result (because the graph was stale at that moment due to a recent rebuild that hadn't completed), and proceeded to modify the prefab on the assumption that no other assets referenced it. The bug is identified: graph staleness was not flagged as a confidence issue. The fix is concrete: queries during graph rebuild should return a "stale" warning that the agent can act on.

### 5.5 Scenario: "New developer joins the team"

**Without Hades.** The new developer is given access to the codebase and sets up their AI assistant. Their assistant has zero context for this project. Over the first weeks, they get suggestions inconsistent with team patterns. Senior developers manually correct. The new developer's AI does not learn. Each session restarts from scratch.

**With Hades.** The new developer clones the repo. The team's `decisions.md`, `patterns.md`, `conventions.md` come with the clone. The new developer's AI assistant, on first use, already knows what the team has decided over the past two years. Suggestions are consistent with team standards from the first session. Senior developers' time is freed from constant correction.

This is the team scenario where Hades's structural advantage is most clearly visible.

### 5.6 Scenario: "Evaluate if a new skill or prompt is actually better"

**Without Hades.** The developer wants to know if a new version of a skill produces better outputs. They eyeball a few responses and form an impression. There is no systematic measurement.

**With Hades.** The developer pulls the eval dataset Charon has built from past sessions (with PII redaction). They run the new skill against the dataset. They see acceptance rate change from 73% to 81% on the inventory-related queries, with regression on the networking queries. They make an informed decision about whether to ship the change.

This is a less common scenario — most developers won't run formal evals — but it matters for ArcForge itself, where every skill change needs to be validated, and for studios that take their AI tooling seriously enough to measure it.

---

## 6. Pain points eliminated, by layer

Mapping the pain points from §2.4 to the Hades layers that address them.

| Pain point | Layer that addresses it | Mechanism |
|---|---|---|
| Pattern drift | Graph + Asphodel | Agent sees existing patterns and explicit conventions before responding |
| Token waste on rediscovery | Graph | Project structure available in pre-indexed form, not via repeated tool calls |
| Hallucination against existing code | Graph | Agent queries the graph instead of guessing about file existence and component layout |
| No memory across sessions | Asphodel | Decisions and inferred preferences persist in version-controlled markdown |
| No way to debug agent behavior | Charon | Every action is traced; failures can be inspected post-hoc |
| No team consistency | Asphodel + git | Memory travels with the project; new team members inherit context automatically |
| No way to evaluate changes | Charon | Trace data accumulates into eval datasets that can be replayed |
| Stale information | Graph + Asphodel cross-validation | Memory claims contradicted by graph state are flagged |

Pain points NOT addressed by Hades (by design):

- **Slow agent response times.** Hades reduces tool calls but does not change the underlying model latency.
- **API costs.** Hades reduces tokens on rediscovery but does not subsidize per-token API pricing.
- **Quality of the underlying model.** Hades makes any model better-informed but cannot make a weak model strong.
- **Unity Editor performance.** Hades adds a small overhead from `AssetPostprocessor` callbacks; we work to keep this negligible but cannot eliminate it.

---

## 7. Strategic positioning

### 7.1 Competitive landscape

Synthesized from research on Unity-specific MCP tooling, generic code knowledge graphs, AI observability, and memory systems as of mid-2026.

| Category | Notable competitors | Their strength | Their gap relative to Hades |
|---|---|---|---|
| Generic code knowledge graphs (MCP) | GitNexus (10k+ stars), CocoIndex Code, codebase-memory-mcp, Cognee | Mature semantic indexing of source code, broad language support | No Unity-domain awareness; treat scenes/prefabs/SOs as plain text |
| Unity action MCP servers | IvanMurzak Unity-MCP (2.6k stars), CoderGamester mcp-unity (1.7k stars), CoplayDev unity-mcp | Comprehensive editor manipulation tooling | No knowledge graph, no memory, no observability layer |
| Unity official AI | Unity AI Assistant (in `com.unity.ai.assistant@2.0`) | Deep editor integration, official support, Unity-funded | Generators-focused (asset creation), not agentic-coding-focused |
| Skills-only Claude Code plugins | Nice-Wolf-Studio/unity-claude-skills (35 skills) | Broadest Unity skill library, code-example-rich | No MCP tools, no graph, no memory, no observability |
| MCP + skills combos | nategarelik/game-dev-supercharger, IvanMurzak (with CLI-generated skills) | Combine action and guidance | No graph, no memory, no observability |
| AI observability platforms | Langfuse, LangSmith, Braintrust | Industry-standard tracing and eval infrastructure | Domain-agnostic; no Unity awareness |
| Memory MCP servers | Zep, Cognee, Hindsight, mcp-memory-service | Persistent context, semantic retrieval, knowledge graphs | Domain-agnostic; no Unity-specific decision modeling |

Closest single competitor: **IvanMurzak Unity-MCP**, which has Resources (read-only project access) and Prompts (template injection). It has the largest mindshare in the space and the most development velocity. If they add memory and observability, they become direct competitors. They have not as of this writing.

### 7.2 What Hades is uniquely

The intersection of: Unity domain awareness, knowledge graph (not text indexing), observability, and persistent memory, in one integrated stack.

No competitor occupies that intersection. Closest: generic memory MCPs + generic code KG + Unity action MCP can be wired together by a sufficiently motivated developer, but the result is four disconnected systems with no domain integration and no feedback loops between layers. The integrated experience is the differentiator; the components individually are not.

### 7.3 Time window

Open. The trends that make Hades feasible (knowledge graphs as a code-agent standard, OpenTelemetry as agent observability standard, file-based memory's resurgence over vector DBs) are mid-2026 phenomena. Generic tools will eventually add Unity parsers. IvanMurzak will eventually add memory or observability or both.

The window is in the order of 6-12 months before a serious convergence-toward-the-same-product appears. The strategic implication is shipping early, even with reduced surface area, beats shipping late with fuller features.

### 7.4 Moat

Domain depth, not feature count. Unity-specific scanners that correctly model prefab variants, ScriptableObject instance data, addressable groups, and render pipeline configuration take months to get right. The interconnected behavior between Graph, Charon, and Asphodel emerges from running on real projects over time and accumulates value the longer it runs. A late entrant building the same surface area starts with empty memory and unevolved skills.

This is a moderate moat. Not impossible to replicate; not trivial either. Defended primarily by being first and accumulating real-project tuning.

### 7.5 Distribution strategy

Hades is delivered from a **single repository** that serves both Unity and Claude Code ecosystems. The repository is simultaneously a Unity Package (installable via UPM) and a Claude Code plugin (installable via `/plugin install`).

The full plugin architecture — what the plugin contains (22 skills, 6 commands, MCP Hub connectivity), directory layout, installation flow, Anthropic marketplace compliance, and versioning — is documented in the **Plugin document** (`Documentation/arcforge-hades-plugin.md`). That document is the authoritative source for all plugin packaging and distribution concerns.

**Summary of the install experience.** From the user's perspective, two actions:

1. Add Hades Unity Package via UPM git URL → Hades Scanner runs in the project, MCP server starts inside Unity
2. Install the Claude Code plugin — two options:
   - **Marketplace:** `/plugin install hades@TheArcForge/Hades` (persists across sessions)
   - **Local:** `claude --plugin-dir /path/to/hades-plugin` (per-session only)

Step 1 is per-project. Step 2 is per-user. Both are needed for the full experience.

**Commercial model.** Both artifacts are MIT licensed and open source. No paid tier in the v1 vision. Future commercial considerations (managed eval dashboards, hosted shared memory for distributed teams, enterprise support) are deliberately out of scope for the initial vision and revisited only after product traction is established.

---

## 8. Success criteria

How we will know if the vision is being realized. These are not commitments to specific numbers; they are the dimensions on which to measure.

### 8.1 Technical success

- Hades Graph correctly models prefab variants, override layers, ScriptableObject instances, and addressable groups for representative Unity projects (URP and HDRP, 2D and 3D, mobile and PC targets).
- Incremental graph updates complete within 1 second for typical edit operations on medium projects (10k+ assets); full rebuild within 30 seconds.
- Charon traces produce decipherable post-hoc diagnosis for every failure mode the team encounters during development.
- Asphodel memory survives round-tripping through git without corruption; team members on different machines see consistent agent behavior.

### 8.2 Product success

- Solo developers report measurable token reduction (target: 30%+) on equivalent tasks compared to a generic Unity MCP setup.
- Studio teams report measurable consistency improvement (target: agent suggestions matching team conventions on first response in 70%+ of cases, up from baseline).
- Onboarding time for new developers (to producing project-consistent code via AI assistance) measurably shorter with Hades than without.

### 8.3 Strategic success

- Recognized as the project-context layer in the Unity AI tooling discussion, distinct from action layers.
- Three or more in-depth third-party technical writeups about Hades architecture or specific innovations within 12 months of v1.
- Meaningful adoption signals — cloners, active contributors, dependent projects, citations in technical writing — that demonstrate the product is genuinely useful rather than merely interesting.

### 8.4 Anti-success criteria

What would indicate the vision is not working:

- Adoption stalls at "interesting but not used daily" — the integration is not delivering felt value, only theoretical value.
- A generic tool (GitNexus, codebase-memory-mcp) ships a Unity adapter and captures 80% of our value proposition with 20% of the integration depth — our moat was thinner than estimated.
- Graph staleness causes the agent to give wrong answers often enough that developers turn it off — the incremental update mechanism failed at scale.
- Memory becomes a maintenance burden — developers update code but don't update memory, agent gives confident wrong advice, trust erodes.

These failure modes are real risks. They inform the Architecture and Roadmap.

---

## 9. Open questions

Issues that the vision does not resolve and that need to be answered in the Architecture and Roadmap documents (or through experimentation during development).

### 9.1 Architectural

- Exactly how the Unity Package and the MCP Server communicate. Options include: file-based handoff (Unity writes graph to disk, MCP reads), IPC (named pipes, Unix sockets), HTTP localhost, or shared memory. Trade-offs around latency, cross-platform behavior, and editor lifecycle (script reload, domain reload) need analysis.
- How to handle Unity not being open. Read-only fallback to last-known graph state? Full degradation to grep-based responses? Block until Unity launches?
- Whether to support Unity batch mode for CI environments. Probably yes, but the design implications for incremental updates differ from interactive editor mode.
- Whether the eval dataset that Charon accumulates is local-only or contributable to a shared community benchmark (with explicit opt-in).

### 9.2 Product

- Onboarding flow. The value of Hades is largely invisible until used. How do we make first-session experience demonstrate it within 5 minutes? Possibilities include: a sample project bundled with the package, a guided tour command (`/hades:tour`), pre-recorded demonstration videos. The step-by-step first-time user guide is maintained separately in `Documentation/getting-started.md`.
- Pricing/licensing. Currently set to MIT open source (see §7.5). Future commercial considerations are explicitly deferred until after product traction is established.
- Asset Store distribution as supplementary channel beyond UPM git. Asset Store has discoverability benefits but adds review-and-approval overhead and may conflict with the dual-artifact (UPM + plugin marketplace) shape. Decision deferred.
- Documentation strategy. Engineering docs vs user docs vs marketing — which is built first? Likely engineering and user docs prioritized over marketing. The Architecture document this Vision points to is engineering-internal; user-facing docs are a separate deliverable.
- Anthropic marketplace submission timing. Hades is fully usable without marketplace listing (§7.5). Submission to the official Anthropic catalog is a discoverability optimization — too early risks rejection on insufficient maturity, too late forfeits months of default-discoverability. Target: submit after 3+ months of stable usage.

### 9.3 Domain

- How do we handle Unity version differences? `AssetDatabase` API has changed across Unity versions. Do we target Unity 6+ only, or maintain back-compat?
- How do we handle custom render pipelines (URP/HDRP/custom SRP)? Some projects use heavily customized pipelines that may not fit our default model.
- How do we handle Unity's own AI Assistant when it expands? If Unity's official tool eventually does what we do, our positioning shifts. We need a contingency plan.

### 9.4 Strategic

- How aggressive to be on Skills expansion. Match Nice-Wolf-Studio's 35-skill library, or stay focused on Hades's three-layer differentiator?
- Whether to build a "Hades Lite" — a stripped-down version with Graph only, no Charon or Asphodel, for users who want partial value with lower setup cost. Risk: cannibalizes full product. Benefit: lower-friction entry point.
- How to handle the inevitable convergence of generic tools toward Unity awareness. Do we focus on staying ahead on integration depth, or pivot toward enabling the generic tools (e.g., providing our scanners as a library other tools consume)?

---

## 10. Closing

Hades is, at its core, a bet on a simple thesis: **AI coding agents working on Unity projects need domain-specific understanding, persistent memory, and observability, and these need to work together rather than as separate tools.**

The bet has technical risk (incremental graph updates at scale, memory self-validation), product risk (the value is invisible until experienced), and strategic risk (generic tools will eventually converge on Unity domain). All three are acknowledged in this document and addressed in the Architecture and Roadmap.

The bet also has a structural advantage. Unity is fundamentally graph-shaped, generic tools fundamentally are not graph-aware about Unity, and the trends in 2026 favor the architecture we are proposing. The window to define this category is open and finite.

The next two documents, Architecture and Roadmap, translate this vision into specifics: how each component is built, in what order, and how we know we are on track.

---

*End of Vision document.*
