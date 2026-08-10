# Retired v1.2 documentation

Everything in this folder describes Hades v1.2: a Unity Editor package, an in-Editor MCP
server, and a Node.js Bridge/Hub that routed Claude Code to the correct Unity instance. That
architecture is retired. Hades is now a standalone macOS menu-bar app with an embedded .NET
core — see `Documentation/Architecture.md`.

These files were moved here **verbatim, on 2026-08-10, with zero content edits.** They are the
only written record of the v1.2 design and are kept for reference only. Nothing in this folder
was updated to reflect the current app, including cross-references between these files to each
other — those still resolve, since everything they point at moved here together. Do not treat
anything in this folder as current.

## What's here

- **`arcforge-hades-architecture.md`, `arcforge-hades-roadmap.md`, `arcforge-hades-vision.md`,
  `arcforge-hades-plugin.md`** — the v1.2 architecture, roadmap, vision, and plugin-manifest
  docs.
- **`getting-started.md`, `troubleshooting.md`** — the v1.2 install and troubleshooting guides.
  Both describe the Node 20 / Unity Package Manager install path and `hub.json` — none of which
  exist in the current app.
- **`comparison.md`, `performance-benchmark.md`** (plus `media/`, the recordings `comparison.md`
  embeds) — real, measured numbers, but measured against the v1.2 in-Editor system. Stale
  evidence for the current app.
- **`demo.gif`, `openupm-cover.png`** — v1.2 demo assets. OpenUPM distribution ended with the
  v1.2 Unity-package install path.
- **`interpreting-results.md`** — documents a `confidence` block (`level`, `result_status`,
  `factors`, plus signals like `nested_by` and `scan_health`) on tool responses. Verified against
  the current app: `App~/src` has no such fields anywhere in the MCP tool surface, and a live
  call to a v2 tool returns no `confidence` block. v1.2-only; does not describe the current app.

## Superseded by

- Architecture: `Documentation/Architecture.md`
- Install: `Documentation/InternalTesting-Install.md`
