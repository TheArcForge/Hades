# Hades macOS App — Internal Testing Install Guide

> **TEMPORARY DOCUMENT.** This exists for this internal testing round only and will be
> replaced by a full documentation revamp before release. Everything below was verified
> directly against the `Hades` repo checkout on 2026-08-09 — file paths, source line numbers,
> and measured behavior, not assumptions. The build is changing daily; if something here
> doesn't match what you see, say so rather than assuming you did something wrong.

---

## Before you do anything: three hard constraints

1. **Apple Silicon (arm64) only.** The embedded core is published `-r osx-arm64
   --self-contained`, and nothing produces a universal build today. **A Hades.app built now
   will not run on an Intel Mac**, full stop. (`Documentation/ReleasePipeline.md` §6.9)
2. **Unsigned and un-notarized.** Deliberate, not forgotten — there is no Apple Developer ID
   certificate for this project yet (`security find-identity -v -p codesigning` → 0 valid
   identities, measured). This is *why* installing takes a couple of extra steps below.
3. **macOS 14 (Sonoma) or later.** (`Info.plist` `LSMinimumSystemVersion` = 14.0)

---

## Quick start (if you already have a Hades build)

1. Confirm you're on Apple Silicon + macOS 14+.
2. Install `Hades.app` — DMG or cask, see [Installing Hades.app](#installing-hadesapp) below.
   Launch it.
3. First-run onboarding: allow the folder-access prompts, add your Unity project (type the
   path — Hub auto-discovery isn't built yet, see below), Unity plugin step is optional and
   skippable.
4. In a terminal: `claude --plugin-dir <path-to-your-Hades-checkout>/Plugin-ClaudeCode~`
5. In that Claude Code session, run `/mcp` and confirm `hades` reports **32 tools**. If it's
   closer to 90, or if it connects with **0 tools**, stop — see [Confirm you're testing the new
   Hades](#confirm-youre-testing-the-new-hades-not-old-v12).

Everything past this point is detail and troubleshooting.

---

## Getting Hades.app

If you were already handed a `.dmg` or an `.app`, skip to the next section.

Otherwise, build it from a checkout of this repo (needs Xcode — this tree was last built
with 26.6 — and the .NET SDK — 10.0.301 here):

```
Shell~/HadesApp/scripts/build-dmg.sh Release --allow-unsigned
```

This builds the app, embeds the self-contained core, ad-hoc signs it, and stages a DMG at
`Shell~/HadesApp/DerivedData/dmg/Hades-<version>-unsigned.dmg`. Takes a few minutes the
first time. (`Shell~/HadesApp/scripts/build-dmg.sh`, `Documentation/ReleasePipeline.md` §6.9)

*How you get a checkout of the repo itself in the first place isn't something this guide can
verify — ask whoever pointed you at it if that isn't already obvious.*

---

## Installing Hades.app

The cask and the DMG are **not equivalent** — they trigger different Gatekeeper behavior,
measured directly on the machine that built this app (`ReleasePipeline.md` §6.2):

| Channel | `com.apple.quarantine` set? | Result |
|---|---|---|
| Homebrew cask (`curl`/`git` under the hood) | **No** | Launches with **no Gatekeeper prompt at all** |
| DMG downloaded through a browser, Slack, Drive, AirDrop, Mail, etc. | **Yes** | Blocked on first launch — *"Apple could not verify…"* |

`curl`, `git clone`, and Homebrew never mark files as quarantined; anything that "receives" a
file on your behalf (browser, Mail, Messages, AirDrop, Slack downloads) does. This is why the
same unsigned app behaves differently depending on how it arrived — it's about the channel,
not the file.

### Option A — Homebrew cask (recommended: zero Gatekeeper friction)

**Caveat, verified directly from `Casks/hades.rb`'s own header comment:** the cask's `url`
points at a GitHub Release asset that does not exist yet — nothing has published a DMG there.
A plain `brew install --cask hades` will fail today, tap or no tap. If your test coordinator
has since stood up a real tap for this round, use whatever `brew tap …` they give you instead
of the below.

Otherwise, this is the exact local-tap recipe verified in `ReleasePipeline.md` §6.7 against
this same cask file (commands re-verified read-only against this machine's Homebrew 6.0.15
while writing this doc):

```
brew tap-new local/hades-test
cp Casks/hades.rb "$(brew --repo local/hades-test)/Casks/hades.rb"
```

Edit that copied `hades.rb`: point `url` at `file:///absolute/path/to/Hades-<version>-unsigned.dmg`
(the DMG from the previous section) and set `sha256` to the real checksum:

```
shasum -a 256 /absolute/path/to/Hades-<version>-unsigned.dmg
```

Then:

```
brew install --cask local/hades-test/hades
```

You should see no Gatekeeper prompt. Confirm the app actually launches. When done testing the
cask path specifically, clean up:

```
brew uninstall --cask local/hades-test/hades
brew untap local/hades-test
```

`brew uninstall --zap hades` additionally removes `~/Library/Application Support/Hades` and
the Preferences plist — never your Unity project's own `.arcforge/memory/`, which structurally
can't be reached from this cask (`Casks/hades.rb`, `ReleasePipeline.md` §6.6).

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

The plugin is `Plugin-ClaudeCode~/` in the repo — skills, commands, and a plugin-root
`.mcp.json` pointing straight at `http://127.0.0.1:7823/mcp` (the app itself; no separate Node
hub process this time, unlike v1.2).

It isn't published to a marketplace yet, so for this round install it from your local
checkout:

```
claude --plugin-dir <path-to-your-Hades-checkout>/Plugin-ClaudeCode~
```

This is **per-session** — pass it every time you start `claude`. It won't appear in
`/plugin list` (that's expected for a `--plugin-dir` install, not a bug). Worth an alias:

```
alias claude-hades='claude --plugin-dir <path-to-your-Hades-checkout>/Plugin-ClaudeCode~'
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
     `App~/src/Hades.Server/Mcp/*.cs` — also stated in `ReleasePipeline.md` §6.9 and the
     103→32 consolidation noted in `docs/backlog/mutation-tool-defects.md`)
   - **Old v1.2 package: 90 tools.** (counted directly from `[MCPTool]` attributes in
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
       produce it — so it's a client-side rejection, not a server fault. Fixed in builds after
       this testing round — if you hit it, note the exact `tools.N…` path from the error when
       you report it.
     - **The retired v1.2 stdio plugin, timing out.** The entry shows a `node` command instead
       of an HTTP URL — something like `node …/Bridge~/launcher/dist/index.js` — and the error
       is a timeout (*"MCP error -32001: Request timed out"*), not a schema rejection. That's
       the old plugin's stdio launcher waiting on a Unity Editor that was never attached. You're
       on the old surface, not the new one — see "If you're on the old server" below.

2. **Server version.** Ask Claude to call the `hades_status` tool. Check the `version` field.
   - **New app reports `2.0.0-beta.3`** — a fixed constant (`HadesTools.cs`, `ServerVersion`).
   - **Old v1.2 package reports whatever Unity Package Manager has installed** —
     `Editor/MCP/Tools/GraphQueryTools.cs`'s `hades_status` reads it live via
     `PackageInfo.FindForAssembly(...).version`, not a hardcoded string. For the current old
     release that resolves to `1.2.0`.

   **Do not** use the Unity-side plugin's own version number for this — confusingly, the
   *new* plugin's `Assets/Hades/Runtime/HadesBoot.cs` also carries the literal string
   `"1.2.0"` (`PluginVersion` constant, verified in source). That number tells you nothing
   about old-vs-new; only the two checks above do.

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
(`App~/src/Hades.Core/Editors/PluginInstaller.cs`). It's optional: graph queries, memory, and
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
reported, not clobbered (`App~/src/Hades.Core/Migration/V12Importer.cs`). After migrating,
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

## Known issues (read before you report these as new)

**Hades wasn't running yet when the Claude Code session started.** Claude Code does not retry
an MCP server that was unreachable at session start (confirmed against Claude Code's own docs)
— the `hades` entry shows as failed, not the same as the 0-tools cases above, and it will not
recover on its own. Run `/mcp` and reconnect, or start a new Claude Code session, once
Hades.app is running. Enabling launch-at-login for Hades — the Claude Code onboarding step's
own toggle, or Settings → Login — prevents this class of failure entirely, since Hades is then
already running before any Claude Code session starts.

**A PlayMode `project_run_tests` run saves open scenes to disk, even unsaved scratch changes.**
Entering play mode makes Unity's own Test Runner save whatever scenes are open; `project_run_tests`
with `testMode: PlayMode` inherits that behavior and doesn't say so, so a tracked scene can change
with no explicit `scene_apply`/`scene_manage save` in between — confirmed directly, across separate
testing sessions. **EditMode runs are unaffected** — the tool's own description already notes
that EditMode triggers a domain reload, and that path never saves scenes. If a PlayMode run leaves
scratch content behind, the recovery is clean: remove it via `scene_apply`, then `scene_manage`
`save` — the rest of the file round-trips byte-identical. Check `git status` on scenes you have
open before and after a PlayMode run if this matters to you.

Fixes for the findings raised during this testing round are in progress and not yet reflected
here. If something you hit isn't described above, it's more likely new than already known —
report it.

*(The previously-listed `prefab_apply create` flattening issue is fixed — both a black-box
repro and the shipped plugin source confirm a nested prefab is produced — and has been removed
from this list.)*

*(The previously-listed asset-type indexing boundary is fixed — `BinaryAssetIndexer` now gives
textures, models, audio clips, fonts, shaders, and animation clips a meta-only graph node
(path/name/kind/guid), so `search_by_name`, `find_references_to`, and `trace_dependencies` all
resolve them instead of reporting them absent or their dependencies as empty. Content is still
never read — `inspect_asset` still can't describe what's inside one of these — see
`Documentation/Architecture.md` §4.2–4.3. Removed from this list.)*

---

## What to actually test

Roughly in priority order — this is where problems are most likely to be:

1. **The new-vs-old check above, first, always.** A test session run against the wrong server
   invalidates everything else in that session.
2. **Your actual install path** (cask or DMG) on a Mac that's never seen this app — does
   Gatekeeper behave exactly as described above? Any deviation is worth a report by itself.
3. **Onboarding** — permission prompts, adding a real project manually, the Claude Code step's
   in-app verification.
4. **The Unity plugin install action**, including re-installing over an existing copy
   (version-skew handling — should degrade, never hard-refuse).
5. **The batch `_apply`/`_manage` tools with a live Editor attached** — `scene_apply`,
   `material_apply`, `animation_apply`, `prefab_apply`, `asset_manage`, `project_settings_apply`.
   These are the newest and least-exercised surface (consolidated down from 103 older tools to
   32). For anything that mutates a scene/prefab/material/asset, **check the actual saved YAML
   on disk, not just whether the tool call reported success** — that's exactly how a previously
   reported flattened-prefab bug was caught, and later confirmed fixed, in earlier rounds; a
   tool's own "success" message is not proof.
6. **Port conflicts** — if you still have the old v1.2 package attached to a project, or run
   two instances, confirm the app fails loudly with an actionable message rather than silently
   binding a different port.
7. **Migration**, if you have a v1.2 project to test it against.

---

## How to report

Include:

- What you did, what you expected, what actually happened.
- Which install path (cask / DMG), and whether Gatekeeper fired.
- `hades_status`'s output (gives the MCP server version — `2.0.0-beta.3` for the new app — and
  which projects the app knows about). Paste it directly.
- macOS version and confirmation you're on Apple Silicon.
- For anything Unity-mutation-related: the actual `.unity`/`.prefab`/`.mat`/`.controller` YAML
  diff, not just the tool's response. For Unity Editor errors specifically, the
  `project_get_console_log` tool.

**Where logs live:**

- The Swift shell app logs its own launch/supervision decisions (including whether it launched
  the bundled core or fell back to a dev-mode `dotnet run`) via macOS's unified logging —
  subsystem `com.arcforge.hades.shell`, category `CoreLaunch` (`AppDelegate.swift`). View in
  **Console.app** (filter by process or subsystem) or:
  ```
  log show --predicate 'subsystem == "com.arcforge.hades.shell"' --last 1h --info --debug
  log stream --predicate 'subsystem == "com.arcforge.hades.shell"' --info --debug
  ```
  The `--info --debug` flags are not optional — this subsystem logs at info/debug level, and
  macOS's unified logging suppresses both by default. Without them, both commands run cleanly
  and show nothing, which reads like "no logs" rather than "wrong verbosity."
- **`~/Library/Application Support/Hades/logs/` is a reserved path, not a populated one** —
  `AppPaths.LogsDir` is declared in source but nothing currently writes to it (confirmed: it
  doesn't exist on disk on a machine actively running the app). Don't spend time looking for
  log files there yet.
- The app's data root is `~/Library/Application Support/Hades` (confirmed on disk: holds
  `control.token`, `editor.token`, `projects/`). Preferences are in
  `~/Library/Preferences/com.arcforge.hades.shell.plist`.
- **Not verified while writing this guide:** where the embedded .NET core's own console output
  goes when launched from the bundled app (as opposed to a terminal) — nothing in
  `Shell~/HadesSupervision`'s process-launch code captures its stdout/stderr explicitly. If you
  need core-side output for a report, the most reliable option today is running it yourself in
  a terminal against an isolated port — ask before assuming this is set up for you.
