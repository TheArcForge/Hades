# Installing Hades

Everything needed to get Hades running against a Unity project: install, first launch, the Claude
Code plugin, the optional Unity-side plugin, and migrating off v1.2.

## Requirements

- **Apple Silicon (arm64).** The embedded core is published `-r osx-arm64 --self-contained`, so
  Hades cannot function on an Intel Mac. The SwiftUI shell is built universal only so an Intel Mac
  gets a clear "requires Apple Silicon" alert instead of a silent launch failure.
- **macOS 14 (Sonoma) or later** (`Info.plist` `LSMinimumSystemVersion` = 14.0).
- **Unsigned and un-notarized.** Deliberate, not forgotten — there is no Apple Developer ID
  certificate for this project yet, which is why the install method below matters. See the
  README's "Signing and installation" section.

## Building it yourself

If you were handed a `.dmg`, skip to the next section. Otherwise, from a checkout (needs Xcode and
the .NET SDK):

```
Mac/HadesApp/scripts/build-dmg.sh Release --allow-unsigned
```

That builds the app, embeds the self-contained core, ad-hoc signs it, and stages a DMG at
`Mac/HadesApp/DerivedData/dmg/Hades-<version>-unsigned.dmg`.

## Installing Hades.app

The cask and the DMG are **not equivalent** — they trigger different Gatekeeper behavior,
measured directly on the machine that built this app (`ReleasePipeline.md` §6.2):

| Channel | `com.apple.quarantine` set? | Result |
|---|---|---|
| `install.sh` (curl) | **No** | Launches with **no Gatekeeper prompt at all** |
| DMG downloaded through a browser, Slack, Drive, AirDrop, Mail, etc. | **Yes** | Blocked on first launch — *"Apple could not verify…"* |

`curl` and `git clone` do not mark files as quarantined; anything that "receives" a file on your
behalf (browser, Mail, Messages, AirDrop, Slack downloads) does. This is why the same unsigned app
behaves differently depending on how it arrived — it's about the channel, not the file.

### Option A — install.sh (recommended: zero Gatekeeper friction)

```
curl -fsSL https://raw.githubusercontent.com/TheArcForge/Hades/main/install.sh | bash
```

Downloads the release DMG, verifies its SHA-256, and copies `Hades.app` to `/Applications`. No
`sudo`, no system settings changed, nothing disabled. It refuses on Intel, on macOS < 14, under
`sudo`, and while Hades is running — each with an actionable message rather than a partial install.

Until a `v2.0.0` release is published with the DMG attached, the URL inside the script 404s. To
test it against a locally built DMG, copy the script and point `URL` at a `file://` path, redirect
`INSTALL_DIR` to a scratch directory, and drop the `--proto '=https'` guard — the full recipe and
what the run must show is in `ReleasePipeline.md` §6.7.

### Option B — DMG (works today, not frictionless)

Open the DMG, drag **Hades.app** to **Applications**, try to launch it.

**If it opens cleanly** (e.g. you built it yourself locally and never let it transit through
a "quarantine-applying" app) — you're done, no further steps.

**If macOS blocks it** with *"Apple could not verify that 'Hades' is free of malware…"* —
this is expected for anything that arrived via browser/Slack/Drive/AirDrop, not a corrupted
download. Recovery (macOS 15 removed the right-click-Open shortcut, so this is now the only
path — `ReleasePipeline.md` §6.3):

1. Open **System Settings → Privacy & Security**.
2. Scroll to the Security section. A line naming Hades as blocked appears, with an
   **Open Anyway** button.
3. Click it, authenticate (password or Touch ID).
4. Open Hades.app again — a second dialog appears with a real **Open** button. Click it.

Curious whether a given file is actually quarantined? `xattr -l <path>` — look for
`com.apple.quarantine` in the output.

---

## First launch

The app walks you through: folder-access permissions (explained before the prompt, not
after), the Claude Code plugin command (see below), adding Unity projects, and the optional
Unity plugin step.

**Unity Hub auto-discovery is not implemented yet** — confirmed independently in
`SettingsView.swift`, `SettingsEndpoint.cs`, and the distribution plan doc, all of which
describe it as reserved-but-unbuilt. Add your project by typing/browsing to its path manually;
this is not a bug.

---

## Install the Claude Code plugin

The plugin is `ClaudeCodePlugin/` in the repo — skills, commands, and a plugin-root
`.mcp.json` pointing straight at `http://127.0.0.1:7823/mcp` (the app itself; no separate Node
hub process this time, unlike v1.2).

The normal way to install it, in a `claude` session:

```
/plugin marketplace add TheArcForge/hades-plugin
```

then `/plugin install hades`. Run `/mcp` afterwards and confirm `hades` reports **32 tools** — if
it reports closer to 90, you have the retired v1.2 plugin; see "Confirm you're testing the new
Hades" below.

Working from a clone instead — contributors, or anyone wanting the exact tree they checked out:

```
claude --plugin-dir <path-to-your-Hades-checkout>/ClaudeCodePlugin
```

This is **per-session** — pass it every time you start `claude`. It won't appear in
`/plugin list` (that's expected for a `--plugin-dir` install, not a bug). Worth an alias:

```
alias claude-hades='claude --plugin-dir <path-to-your-Hades-checkout>/ClaudeCodePlugin'
```

---

## Confirm you're testing the new Hades, not old v1.2

**Read this before you test anything else.** If you (or the Unity project you're testing
against) ever had Hades installed before this app existed, Claude Code may still be wired to
the **old in-Editor MCP server** — a completely different thing from the app this guide
covers. If that happens, you'll spend your session "testing Hades" without ever touching the
new app, and the resulting report is worthless to us. This has enough of a track record that
it's worth a dedicated check, not a footnote.

Check both:

1. **Tool count.** In Claude Code, run `/mcp`. Find `hades` in the list — it shows a tool
   count next to each server.
   - **New app: 32 tools.** (counted directly from `[McpServerTool]` attributes in
     `Core/src/Hades.Server/Mcp/*.cs` — also stated in `ReleasePipeline.md` §6.9 and the
     ~90→32 consolidation noted in `docs/backlog/mutation-tool-defects.md`)
   - **Old v1.2 package: ~90 tools.** (counted directly from `[MCPTool]` attributes in
     `Editor/MCP/Tools/*.cs`; the old plugin manifest's own description also says "90 MCP
     tools")
   - **Connected, but 0 tools.** Neither of the above — and a real outcome, not a hypothetical
     one. It reads like a botched install, but it has two distinct causes with an easy tell:
     look at what `/mcp` shows as the `hades` entry's command/URL.
     - **The new plugin, hitting a client-side schema bug.** The entry shows the HTTP URL
       (`http://127.0.0.1:7823/mcp`) and an error like *"tools fetch failed — Invalid input (at
       tools.N.outputSchema…)"*. Claude Code's schema validator rejects a boolean-form JSON
       Schema subschema exported by one tool's output schema; the server's own `tools/list` is
       fine — this is Claude Code's validator rejecting the response, not the server failing to
       produce it — so it's a client-side rejection, not a server fault. Fixed as of 2.0.0 —
       if you still hit it, note the exact `tools.N…` path from the error in your report.
     - **The retired v1.2 stdio plugin, timing out.** The entry shows a `node` command instead
       of an HTTP URL — something like `node …/Bridge~/launcher/dist/index.js` — and the error
       is a timeout (*"MCP error -32001: Request timed out"*), not a schema rejection. That's
       the old plugin's stdio launcher waiting on a Unity Editor that was never attached. You're
       on the old surface, not the new one — see "If you're on the old server" below.

2. **Server version.** Ask Claude to call the `hades_status` tool. Check the `version` field.
   - **New app reports `2.0.0`** — a fixed constant (`HadesTools.cs`, `ServerVersion`).
   - **Old v1.2 package reports whatever Unity Package Manager has installed** —
     `Editor/MCP/Tools/GraphQueryTools.cs`'s `hades_status` reads it live via
     `PackageInfo.FindForAssembly(...).version`, not a hardcoded string. For the current old
     release that resolves to `1.2.0`.

   **Do not** use the Unity-side plugin's own version number for this — the
   *new* plugin's `Assets/Hades/Runtime/HadesBoot.cs` carries its own independent version line,
   currently `"1.4.0"` (`PluginVersion` constant, verified in source, kept in sync with two test
   mirrors). That number tells you nothing about old-vs-new; only the two checks above do.

3. **Extra tell, if you want a third data point:** the new tool list includes `prefab_apply`.
   The old list has no tool by that exact name — it has `prefab_create`, `prefab_instantiate`,
   `prefab_apply_overrides`, etc. as separate tools instead.

**If you're on the old server:** it registers itself globally via
`~/.arcforge/hades-hub/hub.json`, independent of any one project — closing the Unity Editor
that owns it and starting a fresh Claude Code session is usually enough. Full cleanup is part
of the migration flow below.

---

## Install the Unity-side plugin (optional, per project)

The Unity side is no longer a package dependency — it's a plugin the app writes directly into
your project at `Assets/Hades/`, from resources embedded in the app binary
(`Core/src/Hades.Core/Editors/PluginInstaller.cs`). It's optional: graph queries, memory, and
traces all work without it. You only need it for live-Editor features — scene/prefab editing,
play mode, console, test running.

**Use the app's install action — don't hand-copy anything.** In the app: **Projects** view (or
the "Unity Plugin" onboarding step) → find your project → **Install Plugin**. It writes or
updates `Assets/Hades/` in place; safe to click again later, it's idempotent. Let Unity
recompile after. As with any Hades-touched folder, no `.meta` files are written by hand —
Unity generates them on its next refresh.

If you already have an older `Assets/Hades/` from a previous test round and the app ships a
newer plugin version, connecting the Editor should **degrade with a warning, not hard-refuse**
— that's the intended behavior, worth specifically trying to break.

---

## Migrating from v1.2 (only if you have an old install)

If a Unity project's `Packages/manifest.json` has a `com.arcforge.hades` dependency, the app
detects it the moment you add that project and offers migration.

**A clean `Packages/manifest.json` does not mean a clean machine.** v1.2 leftovers can live
entirely outside the Unity project — a marketplace-installed old plugin recorded in your global
Claude Code settings (`enabledPlugins`), a stray project-root `.mcp.json`, or
`~/.arcforge/hades-hub/` — and the app's per-project detector cannot see any of these. Check
`/plugin` in Claude Code for an installed old-plugin entry, and use the app's Settings cleanup
actions for the rest.

The stray `.mcp.json` is the sneakiest of these, because it still *works*: `claude mcp list`
run inside the project shows `hades: node .../.arcforge/hades-hub/launcher.js — ✔ Connected`,
and every session in that project quietly talks to the old server instead of this app (found
live on a real migrated machine, not hypothetical). The app's migration cleanup removes it, or
by hand: `claude mcp remove "hades" -s project` — after the new plugin is installed, or the
project has no Hades at all in between.

**The one rule that matters here: `.arcforge/memory/` is authored, irreplaceable content.**
It's the decisions and conventions your project has accumulated — nothing regenerates it if
it's lost. Migration copies it into the app's own storage; the source is **never modified or
deleted**, and an existing copy on the app side is never silently overwritten — a collision is
reported, not clobbered (`Core/src/Hades.Core/Migration/V12Importer.cs`). After migrating,
confirm your project's `.arcforge/memory/` is still sitting there untouched.

Everything else is optional and confirmed individually — never one "migrate everything"
button:

- Import `.arcforge/traces.db` (history) — optional.
- Remove the `com.arcforge.hades` entry from `Packages/manifest.json` — optional, but leaving
  both installed means the old and new servers fight over port 7823.
- Clean the generated `.mcp.json`, the marked `<!-- HADES:START -->…<!-- HADES:END -->` block
  in `CLAUDE.md` (unmarked content is never touched), and the `hades` entry in Claude Desktop's
  global config — each its own confirmation.
- `.arcforge/graph.db` is deliberately never imported — schema differs; it just rebuilds.

v1.2 keeps working the whole time. Nothing here is forced or automatic.

---

## Known issues

**Hades wasn't running yet when the Claude Code session started.** Claude Code does not retry
an MCP server that was unreachable at session start (confirmed against Claude Code's own docs)
— the `hades` entry shows as failed, not the same as the 0-tools cases above, and it will not
recover on its own. Run `/mcp` and reconnect, or start a new Claude Code session, once
Hades.app is running. Enabling launch-at-login for Hades — the Claude Code onboarding step's
own toggle, or Settings → Login — prevents this class of failure entirely, since Hades is then
already running before any Claude Code session starts.

If something you hit isn't described above, it's more likely new than already known — please
[open an issue](https://github.com/TheArcForge/Hades/issues).

*(The previously-listed asset-type indexing boundary is fixed — `BinaryAssetIndexer` now gives
textures, models, audio clips, fonts, shaders, and animation clips a meta-only graph node
(path/name/kind/guid), so `search_by_name`, `find_references_to`, and `trace_dependencies` all
resolve them instead of reporting them absent or their dependencies as empty. Content is still
never read — `inspect_asset` still can't describe what's inside one of these — see
`Documentation/Architecture.md` §4.2–4.3. Removed from this list.)*

---
