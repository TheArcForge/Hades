# Roadmap

What Hades is likely to do next, in rough order of nearness. **No dates** — this is a
single-maintainer project and a date here would be a guess dressed up as a commitment.

Horizons shift. If something below matters to you, say so in
[Ideas](https://github.com/TheArcForge/Hades/discussions) — a real use case moves an item faster
than anything else, because the alternative is guessing which of these people actually want.

## Now

- **Earn the cost claim, or drop it.** This is the top priority, and it exists because our own
  measurement embarrassed us. Over 24 runs on a public 539k-node project, an agent with only shell
  tools matched Hades on accuracy and cost **1.6–2.2x less** — on the two questions we picked
  expecting the graph to win. Worse, simply having Hades connected costs tokens before a tool is
  called: 33 tool schemas and 23 skills sit in context whether used or not. Three things follow,
  and they are the next release:
  - **Cut the standing cost.** The resting overhead of being connected has to come down. Fewer
    tools in the default surface, leaner schemas, or loading by need rather than by default.
  - **Make the graph worth calling.** One `find_references_to` should beat two `grep` passes, not
    tie them. That means answering in one hop what currently takes the agent two, which is the same
    `maxDepth` gap listed below — now with a measured reason to fix it.
  - **Tell the client the graph exists.** An agent that is told in one line will use it; unprompted
    it will not, even with the plugin installed. The missing MCP `instructions` field below is no
    longer a nicety.

- **Agents other than Claude Code.** The server has always been plain MCP over Streamable HTTP, but
  until recently the only documented way to connect was the Claude Code plugin.
  [Connecting an agent](Documentation/Clients.md) is that documentation. Gemini CLI connects;
  **Cursor and Codex CLI have never been run against Hades**, and those are open
  [`help wanted`](https://github.com/TheArcForge/Hades/issues?q=is%3Aissue+is%3Aopen+label%3A%22help+wanted%22)
  issues rather than claims.
- **A reproducible benchmark.** The comparison in the README is one prompt on one purpose-built
  project. That is an illustration, not evidence. Replacing it with something another person can
  run, on a project they choose, is the single most useful thing left to do for anyone deciding
  whether to trust this.

## Next

- **Unity 2022.3 LTS, properly.** The graph is already measured on it: a 1.1M-edge 2022.3 project
  indexed and checked case by case against independently generated ground truth. The in-Editor
  plugin also compiles and runs there with no errors. What is missing is not porting work — it is a
  CI matrix across both Editor versions, and verification of test running in particular. Until that
  exists, 2022.3 is *known to work* rather than *supported*, and
  [Limitations](LIMITATIONS.md) says so.
- **Reference queries that reach further than one hop.** `find_references_to` walks exactly one hop
  inward, so a prefab nested inside another prefab that a scene uses reports the middle prefab and
  not the scene. On a real project most ancestor relationships sit deeper than one hop, which means
  "everything that would break" currently takes repeated queries. A depth parameter is the intended
  fix, with the deeper hits kept separate from the direct ones so existing answers do not change
  shape.
- **Server-side guidance for clients that want it.** The MCP `initialize` response can carry an
  `instructions` field, and some clients — Codex CLI, for one — use it as guidance alongside the
  tools. Hades sends none, so a non–Claude Code user gets 33 tools with nothing telling an agent
  when to reach for them, while a Claude Code user gets the skills. Narrowing that is cheap.
- **Linux, headless, for CI.** Indexing is static and needs no Editor, so a graph built in CI is a
  natural fit. The core already builds for other platforms; the work is packaging and verification.
- **Signed builds.** Both installers exist to route around Gatekeeper and SmartScreen friction.
  Certificates would remove the friction instead of routing around it.

## Later

- **A documented graph export format.** The graph is a SQLite database with three tables, and
  nothing about it is secret — but nothing about it is *promised* either, so building another tool
  on top of it today means building on an internal shape. An export other tools can consume, with a
  schema that is allowed to be depended on, would make that reasonable.
- **Bigger projects, faster.** The largest project measured so far is 539k nodes and 1.15M edges.
  Queries on it are fast; some of the paths around them are not as careful as they should be at that
  size.
- **C# code relationships — an open question, not a plan.** Hades indexes the *asset* graph. It
  holds no inheritance graph, no call graph, and no "which code uses this type"; see
  [Limitations](LIMITATIONS.md). Adding that is a different kind of work from everything above — it
  needs semantic analysis rather than deserialization — and it is being considered rather than
  scheduled. If this is the thing you want, saying so is useful.

## Where Hades is not, and why

**The official MCP Registry.** Not an oversight. A registry entry describes either a server others
can reach over the network, or a package pulled from npm, PyPI, crates.io, OCI, NuGet or `mcpb`.
Hades is neither: it is loopback-only by design, and it ships as a macOS disk image and a Windows
installer. The transport is not the obstacle — the registry's schema would happily point at
`http://127.0.0.1:7823/mcp` — the distribution shape is. Fitting it would mean publishing in one of
those formats, which is a product decision rather than a listing chore.
