# Related work

Hades is not the only project pointing an AI agent at a Unity codebase, and it is not the first to
notice that the interesting relationships live in serialized assets rather than in code. This page
describes the neighbours as accurately as we can, with a link to each primary source, so you can
decide what actually fits your problem.

**How to read it.** Everything attributed below comes from the project's own documentation, its
repository metadata, or a published paper — not from running it. Where a claim is theirs rather than
ours, it says so. Hades is held to the same standard everywhere else in this repository, and it
would be dishonest to lower it here.

Figures were checked on **2026-11-16**.

---

## Unity's own plugin for coding agents

Unity ships a first-party plugin, [announced 2026-09-09](https://unity.com/blog/unity-plugin-for-claude-code),
that installs Unity's engineering skills into a coding agent — including a `unity-cli` skill for
driving the Unity CLI introduced at Unite Seoul. Unity's framing is worth
quoting fairly, because it is close to ours: a general-purpose agent is like a contractor who has
never seen your codebase, so "everything they produce that day is a reasonable guess". Unity says
Claude Code is the first agent it installs into, with others to follow.

**What it closes, and what it does not.** Unity's plugin teaches an agent *how Unity works* — where
a project keeps its assets, how a given job is meant to be done, which framework the engine
intends. It does not build a model of *your* project's structure. Those are different gaps, and
closing one does not close the other: an agent can know perfectly how prefab variants work in
general and still not know which four of yours break when you change a base.

If you are choosing one thing, start with Unity's. It is first-party, it is free, and the knowledge
it adds is the knowledge most agents are missing most of the time.

## Editor-action bridges

The largest family. These expose the Unity Editor's own API as MCP tools, so an agent can create
GameObjects, edit components, run tests and drive the Editor.

| Project | Notes |
|---|---|
| [IvanMurzak/Unity-MCP](https://github.com/IvanMurzak/Unity-MCP) | 4.6k stars, C#, Apache-2.0, actively developed. Describes a "full AI develop and test loop", and that any C# method can be turned into a tool with one line |
| [CoderGamester/mcp-unity](https://github.com/CoderGamester/mcp-unity) | 2.0k stars, C#, MIT. Explicitly aimed at Cursor, Claude Code, Codex and Windsurf |

They are the direct comparison for Hades' *optional* Unity plugin, which does the same kind of
thing. They are not a comparison for its graph, because they do not build one — a point easy to
confirm at scale: of the 43 Unity-related entries currently in
[awesome-mcp-servers](https://github.com/punkpeye/awesome-mcp-servers), every description is about
controlling the Editor. None mentions a project model.

The practical difference is what happens with the Editor closed. An action bridge needs a running
Editor by definition; Hades answers structural questions from disk, and treats the Editor as a
participant rather than a dependency.

## Project graphs — the closest neighbours

### UnityGraph

[`Shades-28/UnityGraph`](https://github.com/Shades-28/UnityGraph) — MIT, Python, a static parse that
emits `graph-out/graph.json`, served to agents over a **stdio** MCP server. Read-only middleware
that, in its own words, "does not chat" and "does not call an LLM". Its README reports working with
Claude Code, Cursor, Windsurf and Aider, and no Editor required.

**Where it goes further than Hades, and this is worth saying plainly.** Its documented graph carries
`inherits`, `subscribes_to` and `depends_on` edges, with every code-derived edge carrying evidence
sites (`file`, `line`, `col`, `snippet`). `who_uses(script)` is described as returning attachments,
`GetComponent` and method callers, subclasses and UnityEvent listeners; `impact_of(script)` as
multi-hop. It also documents `is_variant_of` and `overrides` edges.

Hades has **none** of the C# relationship layer — see [Limitations](../LIMITATIONS.md) — and its
reference query walks exactly one hop. So on code-level references and on multi-hop traversal,
UnityGraph's documented design is ahead of what Hades does today, and both are on the
[roadmap](../ROADMAP.md) partly because of it.

It documents its own boundaries too, in a "Limits, by design" section: no method bodies, no return
types, no string-based dispatch (`SendMessage("Foo")` is invisible — the same blind spot Hades
has), properties only partially tracked.

Two things to weigh if you are choosing: it is a one-shot build to a JSON file rather than a live
incrementally-updated store, and it offers no Editor actions, memory or tracing. At the time of
checking it had 2 stars and its last commit was 2026-04-26.

### Unity Insight

[arXiv 2609.27585](https://arxiv.org/abs/2609.27585) — *"Unity Insight: A Production Code–Asset Index
for LLM Coding Agents in Unity Projects"*, Gu, Zhu, Zhang et al., 2026-09-23. Not open source: it
ships in production with Tuanjie Codely, the agent CLI of Tuanjie Engine, and the paper dates that
public launch to 2026-07-28.

The paper's premise is the one Hades was built on, stated more precisely than we had managed:
*"application logic is inseparable from serialized assets"*, and the tools agents carry — "shell
utilities and code-only indexes" — cannot answer cross-file questions because the relationships live
in `.meta` files and YAML rather than in code.

**Its reported result**, quoted because it is the most rigorous number in this space: in a paired
experiment of 28 project-specific questions across two Unity games, same model and harness, the
index-backed agent spent **53% fewer tokens and 52% less wall-clock time** than a general-purpose
exploration agent, with exact paired sign tests at *p* < 0.004.

**Read that honestly, including the limit the paper itself states: one run per arm per question.**
Our own headline comparison has the same shape and the same weakness, which is precisely why a
reproducible benchmark is the next thing on the roadmap rather than a louder number.

The paper describes Unity Insight as, to its authors' knowledge, the first persistent, LLM-facing,
agent-integrated cross-file code–asset index for Unity. We have no interest in contesting a priority
claim. The more useful reading is that two independent efforts, one commercial and one not, arrived
at the same conclusion about the asset layer — which is a stronger signal about the problem than
either project's own enthusiasm.

## Code-graph and semantic-retrieval tools

General-purpose tools that give an agent structured access to code: for example
[oraios/serena](https://github.com/oraios/serena) (31k stars, semantic retrieval and editing) and
[isaacphi/mcp-language-server](https://github.com/isaacphi/mcp-language-server) (1.6k stars,
exposing definitions, references, rename and diagnostics over a language server).

These are genuinely good at what Hades does not do at all — C# semantics — and they are blind to
what Hades is for. A language server knows that `PlayerController` inherits from `MonoBehaviour`; it
has no idea which four prefabs attach it, and no mechanism that could tell it, because that fact
exists only as a GUID inside a YAML asset. **Using one alongside Hades is a reasonable setup**, and
closer to complete than either alone.

## Simulation and digital twins

New ground, and we have less to report than we would like. Unity itself ships
[`Unity-Technologies/industry-ai-workflows`](https://github.com/Unity-Technologies/industry-ai-workflows),
an experimental Claude Code plugin marketplace for industry production workflows — Unity Asset
Manager, CAD preparation via Asset Transformer, and pipeline automation. That is adjacent to the
digital-twin audience, but it addresses pipelines and asset processing rather than a model of a
project's internal structure.

We looked for a simulation- or robotics-specific equivalent of a project graph and **did not find
one**. That is a statement about our search, not proof of absence — if you know of one, an
[issue](https://github.com/TheArcForge/Hades/issues) correcting this page is welcome.

---

## Where Hades sits

Not better, and not worse in every respect — the honest summary is a different combination:

- **MIT, and the whole thing is in the open**, including [what it gets wrong](../LIMITATIONS.md).
- **Runs locally**, loopback-only, with no telemetry and no account.
- **Asset graph plus Editor actions plus project memory plus tracing in one place.** The neighbours
  above each do one of these. Memory in particular — decisions recorded and version-controlled with
  the project — has no counterpart in anything surveyed here.
- **Client-neutral MCP by construction**, over Streamable HTTP; see
  [Connecting an agent](Clients.md) for what that does and does not currently buy you.
- **Continuously indexed** rather than built on demand, so a query reflects what is on disk now.

And where it is behind: **no C# relationship layer**, and **one-hop reference queries**. Both are
documented in [Limitations](../LIMITATIONS.md) and both are on the [roadmap](../ROADMAP.md). A
reproducible public benchmark does not exist yet either — the figure in the README is one prompt on
one project, labelled as such.

*Corrections to this page are welcome, especially from the maintainers of anything described here.
Nothing on it is meant as a competitive claim; getting it wrong would be a bug.*
