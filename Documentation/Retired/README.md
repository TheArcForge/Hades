# Retired v1.2 documentation

Everything in this folder describes Hades v1.2: a Unity Editor package, an in-Editor MCP
server, and a Node.js Bridge/Hub that routed Claude Code to the correct Unity instance. That
architecture is retired. Hades is now a standalone macOS menu-bar app with an embedded .NET
core — see `Documentation/Architecture.md`.

These files were moved here **verbatim, with zero content edits** — most on 2026-08-10, and
`interpreting-results.md` and `performance-benchmark.md` on 2026-08-30, having been listed below
but left behind in `Documentation/` by the first pass. (`comparison.md` was moved here in that
same pass and then moved back out on 2026-09-05: it is still the README's proof link, and a file
cannot be both the current evidence and something this README tells you not to treat as current.
It lives at `Documentation/comparison.md` carrying a dated note that its figures were measured
against v1.2 and a re-measurement is due.) They are the only
written record of the v1.2 design and are kept for reference only. Nothing in this folder was
updated to reflect the current app. Do not treat anything in this folder as current.

**No exceptions to "verbatim" remain.** There used to be one — `comparison.md`'s two recording
links, repointed from `media/…` to `../media/…` so they still resolved one directory deeper. That
file has since moved back to `Documentation/` and its links were repointed again to match, so
nothing in this folder differs from what it said when it was retired.

## What's here

- **`arcforge-hades-architecture.md`, `arcforge-hades-roadmap.md`, `arcforge-hades-vision.md`,
  `arcforge-hades-plugin.md`** — the v1.2 architecture, roadmap, vision, and plugin-manifest
  docs.
- **`getting-started.md`, `troubleshooting.md`** — the v1.2 install and troubleshooting guides.
  Both describe the Node 20 / Unity Package Manager install path and `hub.json` — none of which
  exist in the current app.
- **`performance-benchmark.md`** — real, measured numbers, but measured against the v1.2
  in-Editor system. Stale evidence for the current app, and nothing live links to it.
  (`comparison.md` was in this list until 2026-09-05 — see the note above for why it is not.)
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
