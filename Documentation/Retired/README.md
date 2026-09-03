# Retired v1.2 documentation

Everything in this folder describes Hades v1.2: a Unity Editor package, an in-Editor MCP
server, and a Node.js Bridge/Hub that routed Claude Code to the correct Unity instance. That
architecture is retired. Hades is now a standalone macOS menu-bar app with an embedded .NET
core — see `Documentation/Architecture.md`.

These files were moved here **verbatim, with zero content edits** — most on 2026-08-10, and
`interpreting-results.md`, `comparison.md` and `performance-benchmark.md` on 2026-08-30, having
been listed below but left behind in `Documentation/` by the first pass. They are the only
written record of the v1.2 design and are kept for reference only. Nothing in this folder was
updated to reflect the current app. Do not treat anything in this folder as current.

**One exception to "verbatim":** `comparison.md`'s two recording links were repointed from
`media/…` to `../media/…`. The recordings themselves stay in `Documentation/media/` as live
assets, so the link had to change to keep resolving. Nothing else in this folder was touched.

## What's here

- **`arcforge-hades-architecture.md`, `arcforge-hades-roadmap.md`, `arcforge-hades-vision.md`,
  `arcforge-hades-plugin.md`** — the v1.2 architecture, roadmap, vision, and plugin-manifest
  docs.
- **`getting-started.md`, `troubleshooting.md`** — the v1.2 install and troubleshooting guides.
  Both describe the Node 20 / Unity Package Manager install path and `hub.json` — none of which
  exist in the current app.
- **`comparison.md`, `performance-benchmark.md`** — real, measured numbers, but measured against
  the v1.2 in-Editor system. Stale evidence for the current app. The recordings `comparison.md`
  embeds are **not** here: they remain live at `Documentation/media/`, which is why this file's
  links point at `../media/`.
- **`openupm-cover.png`** — a v1.2 distribution asset. OpenUPM distribution ended with the v1.2
  Unity-package install path. (`demo.gif` is **not** here — it remains live at
  `Documentation/demo.gif` and is still the README's hero image.)
- **`interpreting-results.md`** — documents a `confidence` block (`level`, `result_status`,
  `factors`, plus signals like `nested_by` and `scan_health`) on tool responses. Verified against
  the current app: `Core/src` has no such fields anywhere in the MCP tool surface, and a live
  call to a v2 tool returns no `confidence` block. v1.2-only; does not describe the current app.

## Superseded by

- Architecture: `Documentation/Architecture.md`
- Install: `Documentation/Installing.md`
