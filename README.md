# Hades

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![CI](https://github.com/TheArcForge/Hades/actions/workflows/ci.yml/badge.svg)](https://github.com/TheArcForge/Hades/actions/workflows/ci.yml)
[![MCP compatible](https://img.shields.io/badge/MCP-compatible-blue)](https://modelcontextprotocol.io)

Hades is a standalone macOS menu-bar app. Its .NET core builds a queryable knowledge graph of a Unity project — every scene, prefab, script, and asset, including textures, models, audio, fonts, shaders, and animation clips, and the dependencies between them — and serves it to AI agents over MCP: 32 tools, 22 skills, 6 commands. Everything runs locally. Binary/imported assets are indexed by path, name, kind, and GUID only — their content is never parsed, and an asset resolved outside every scanned root (e.g. a registry package's own copy in `Library/PackageCache`) still has no node; see [Documentation/Architecture.md](Documentation/Architecture.md) §4.3.

## Status

Requires Apple Silicon and macOS 14+ — the embedded .NET core is arm64-only. On Intel Macs the app shows a clear alert and quits instead of failing to launch silently. Unsigned and un-notarized, which shapes how you install it — see [Signing and installation](#signing-and-installation).

## Pieces

| Path | What it is |
|---|---|
| `Shell~` | The macOS menu-bar app. Launches and supervises the core. |
| `App~` | The .NET core — the knowledge graph, the MCP server, project and migration management. |
| `Plugin~` | The Unity-side plugin. The app installs it into a project's `Assets/Hades`. Optional — only needed for live-Editor features (scene/prefab editing, play mode, console, tests). |
| `Plugin-ClaudeCode~` | The Claude Code plugin — skills, commands, and the `.mcp.json` that connects to the app over HTTP. |

## Install

Apple Silicon Mac, macOS 14 or later.

```sh
curl -fsSL https://raw.githubusercontent.com/TheArcForge/Hades/main/install.sh | bash
```

That downloads the release DMG, verifies its SHA-256, and copies `Hades.app` to `/Applications`.
It needs no `sudo`, changes no system settings, and disables nothing. If you would rather read a
script before running it — a reasonable habit — the source is [`install.sh`](install.sh):

```sh
curl -fsSL -O https://raw.githubusercontent.com/TheArcForge/Hades/main/install.sh
```

You can also take the DMG straight from [Releases](https://github.com/TheArcForge/Hades/releases)
and drag it to Applications. That works, but macOS will block it on first launch — see
[Signing and installation](#signing-and-installation) for why, and for the one-time approval steps.

Then connect Claude Code. In a `claude` session:

```
/plugin marketplace add TheArcForge/hades-plugin
```

followed by `/plugin install hades`. That installs the skills, commands, and the `.mcp.json` that
points at the app on `127.0.0.1:7823`. Run `/mcp` afterwards and confirm `hades` reports **32 tools**.

Working from a clone instead? Point Claude Code at the plugin directly — per-session, so pass it
every time:

```sh
claude --plugin-dir <your-Hades-checkout>/Plugin-ClaudeCode~
```

Longer walkthrough, including troubleshooting: [Documentation/Installing.md](Documentation/Installing.md).

## Architecture

See [Documentation/Architecture.md](Documentation/Architecture.md).

## Migrating from v1.2

The app detects an existing v1.2 install (Unity package + in-Editor MCP server + Node bridge) and offers to migrate its project memory and clean up the old install.

## Signing and installation

**Hades is not yet signed with an Apple Developer ID certificate.** There is no such certificate
for this project yet; getting one is the plan, and this section disappears when it happens.

macOS blocks unsigned apps on first launch — but only files carrying the `com.apple.quarantine`
attribute, which is set by whatever fetched the file, not by the file itself. That single detail
decides your install experience:

| How you got it | Quarantined? | First launch |
|---|---|---|
| [`install.sh`](install.sh) (uses `curl`) | No | Opens normally |
| DMG downloaded in a browser, or via Slack/Drive/AirDrop/Mail | Yes | Blocked — *"Apple could not verify…"* |

So [`install.sh`](install.sh) is the recommended route today. It does not disable Gatekeeper,
strip attributes, or ask for `sudo` — it simply fetches with a tool that does not mark downloads,
which is the same mechanism every `curl | bash` developer installer relies on.

**If you did get the blocked dialog**, the app is fine and this is the recovery (macOS 15 removed
the old right-click → Open shortcut, so this is now the only route):

1. **System Settings → Privacy & Security**, scroll to the Security section.
2. A line naming Hades appears with an **Open Anyway** button. Click it and authenticate.
3. Open Hades again; click **Open** in the second dialog.

Once per installed version, not once per launch.

**This is a stopgap, and it is meant to be temporary.** Signing and notarizing is the real fix:
it removes the prompt on every channel and lets this section be deleted.
It is waiting on the Developer ID account, not on a technical decision.

## License

MIT
