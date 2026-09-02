# Installing Hades

Everything needed to get Hades running against a Unity project: install, first launch, the Claude
Code plugin, the optional Unity-side plugin, and migrating off v1.2.

## Requirements

### macOS

- **Apple Silicon (arm64).** The embedded core is published `-r osx-arm64 --self-contained`, so
  Hades cannot function on an Intel Mac. The SwiftUI shell is built universal only so an Intel Mac
  gets a clear "requires Apple Silicon" alert instead of a silent launch failure.
- **macOS 14 (Sonoma) or later** (`Info.plist` `LSMinimumSystemVersion` = 14.0).

### Windows — beta

- **Windows 10 version 1607 (build 14393) or later.** The MSI enforces this, reading the real build
  from the registry; below it, the .NET 10 runtime the app carries will not start at all.
- **x64 or ARM64**, 64-bit only — there is no 32-bit build. Both architectures ship as separate
  MSIs, because an MSI carries exactly one architecture.
- **The ARM64 build has never been executed.** Its binaries were verified genuinely native rather
  than silently x64, which is a different and much weaker claim than "it works".
- **A per-user install**, to `%LOCALAPPDATA%\Programs\Hades`. If your machine has an AppLocker or
  WDAC policy blocking execution from under `%LOCALAPPDATA%` — common on managed and enterprise
  machines — Hades will not run from there, and that is the policy working as intended.

### Both

- **Unsigned.** Deliberate, not forgotten — there is no Apple Developer ID certificate and no
  Windows code-signing certificate for this project yet, which is why the install method below
  matters on both platforms. See the README's "Signing and installation" section.
- **Unity 6000.0+**, and only for the optional in-Editor plugin.

## Building it yourself

If you were handed a `.dmg` or an `.msi`, skip to the next section.

**macOS** — from a checkout (needs Xcode and the .NET SDK):

```
Mac/HadesApp/scripts/build-dmg.sh Release --allow-unsigned
```

That builds the app, embeds the self-contained core, ad-hoc signs it, and stages a DMG at
`Mac/HadesApp/DerivedData/dmg/Hades-<version>-unsigned.dmg`.

**Windows** — from a checkout (needs the .NET 10 SDK and WiX 7, `dotnet tool install --global wix`):

```powershell
Windows\Installer\build-msi.ps1 -Rid win-x64 -Version 2.1.0
```

That publishes the shell, the CLI and the core self-contained, assembles the install layout, and
builds `Windows\Installer\bin\Hades-<version>-win-x64.msi`. Pass `-Rid win-arm64` for the other
architecture. The script verifies that the MSI's file table matches the staged file count and fails
if it does not — WiX reports an empty payload as a *warning* and exits 0, which otherwise produces
an installer that installs cleanly and delivers nothing.

## Installing the app

On both platforms the same principle decides your experience: **the channel you got the file
through matters more than the file.** The mechanism differs — Gatekeeper quarantine on macOS,
Mark-of-the-Web on Windows — but in both cases a command-line download is clean and a browser
download is not.

### macOS

The cask and the DMG are **not equivalent** — they trigger different Gatekeeper behavior,
measured directly on the machine that built this app (`ReleasePipeline.md` §6.2):

| Channel | `com.apple.quarantine` set? | Result |
|---|---|---|
| `install.sh` (curl) | **No** | Launches with **no Gatekeeper prompt at all** |
| DMG downloaded through a browser, Slack, Drive, AirDrop, Mail, etc. | **Yes** | Blocked on first launch — *"Apple could not verify…"* |

`curl` and `git clone` do not mark files as quarantined; anything that "receives" a file on your
behalf (browser, Mail, Messages, AirDrop, Slack downloads) does. This is why the same unsigned app
behaves differently depending on how it arrived — it's about the channel, not the file.

#### Option A — install.sh (recommended: zero Gatekeeper friction)

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

#### Option B — DMG (works today, not frictionless)

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

### Windows

Windows' equivalent of quarantine is the **Mark-of-the-Web**: a `Zone.Identifier` NTFS alternate
data stream that whatever fetched a file may attach to it. SmartScreen warns about unsigned
installers *that carry one*. Measured on Windows 11 build 26200, with a control file carrying a
hand-written `Zone.Identifier` on the same volume to prove the check could see a mark if one were
present:

| How you got the MSI | `Zone.Identifier` written? | Result |
|---|---|---|
| `install.ps1` (uses `curl.exe`) | **No** — measured | Installs with no interstitial |
| `curl.exe` by hand | **No** — measured | Same |
| `Invoke-WebRequest` | **No** — measured | Same |
| `System.Net.WebClient` | **No** — measured | Same |
| Downloaded through a browser | **Yes** | SmartScreen interstitial — see below |

`Invoke-WebRequest`'s behaviour is widely reported both ways; the row above is a measurement on this
build, not a citation.

#### Option A — install.ps1 (recommended: no SmartScreen interstitial)

```powershell
irm https://raw.githubusercontent.com/TheArcForge/Hades/main/install.ps1 | iex
```

Picks the MSI matching your architecture, verifies its SHA-256, and installs per-user. No
Administrator, no UAC prompt, no system settings changed, nothing disabled. It refuses on the wrong
architecture, below build 14393, from an elevated prompt, and while Hades is running — each with an
actionable message rather than a partial install. It also refuses if its own checksums have not been
pinned for a release, rather than installing something it cannot verify.

Until a Windows release is published with the MSIs attached, the URL inside the script 404s.

**Why the elevated-prompt refusal exists:** a per-user MSI installs into the profile of whoever runs
it. Elevated, that is the *Administrator's* profile — the app would land somewhere you never look,
the PATH entry would be on the wrong account, and the tray app would not start with your session.

#### Option B — the MSI directly (works, not frictionless)

Download the MSI for your architecture from [Releases](https://github.com/TheArcForge/Hades/releases)
and run it.

**If it opens cleanly** (you built it yourself, or fetched it with `curl.exe`) — you're done.

**If SmartScreen blocks it**, you'll see a *"Windows protected your PC"* dialog whose default button
is **Don't run**. The app is fine and this is expected for a browser download of unsigned software.
Click **More info**, confirm the publisher shows as *Unknown publisher*, then **Run anyway**.

> The exact wording above is what Windows documents and what this dialog has long said, but it has
> **not yet been confirmed against a published Hades release** — there isn't one to download in a
> browser yet. Treat the shape as reliable and the exact strings as pending verification.

**One case with no override: Smart App Control.** On Windows 11 machines where SAC is enabled — only
possible on clean installs — unsigned code is blocked outright and there is no "run anyway". If that
is your machine, wait for a signed release; nothing in `install.ps1` can help, and it does not
pretend otherwise. **Untested** — this is recorded from documentation, not measured. The machine
this was developed on has SAC off (`VerifiedAndReputablePolicyState = 0`).

Curious whether a given file actually carries a mark?

```powershell
Get-Content .\Hades-2.1.0-win-x64.msi -Stream Zone.Identifier
```

An error saying the stream does not exist means the file is clean.

#### After installing on Windows

The installer also puts a `hades` command-line tool on your PATH. It appears **only in terminals
opened after the install** — Windows hands each process its environment block at launch, so windows
already open never see it. Open a new terminal and check:

```powershell
hades status
```

Uninstalling is **Settings → Apps → Installed apps → Hades**. That removes the app, the Start Menu
shortcut and the PATH entry, and deliberately leaves `%LOCALAPPDATA%\Hades` — your graph, traces and
authored memory — alone. Delete that folder by hand if you want it gone.

---

## First launch

The app walks you through the Claude Code plugin command (see below), adding Unity projects, and
the optional Unity plugin step.

**On macOS** it also explains folder-access permissions before the prompt appears rather than
after — five steps in total. **On Windows** there is no equivalent permission prompt, because
Windows has no per-folder access gate to ask about, so onboarding is **four steps**. Neither
sequence claims a step count the other one has.

**Unity Hub auto-discovery is not implemented yet** — confirmed independently in
`SettingsView.swift`, `SettingsEndpoint.cs`, and the distribution plan doc, all of which
describe it as reserved-but-unbuilt. Add your project by typing/browsing to its path manually;
this is not a bug. On Windows this also means a Unity Hub installed on a non-default drive is
simply a path you type in rather than something the app finds.

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
`/plugin list` (that's expected for a `--plugin-dir` install, not a bug). Worth an alias.

**bash / zsh:**

```
alias claude-hades='claude --plugin-dir <path-to-your-Hades-checkout>/ClaudeCodePlugin'
```

**PowerShell** — a function, **not** `Set-Alias`. A PowerShell alias is a name for a command and
cannot carry arguments, so `Set-Alias claude-hades "claude --plugin-dir ..."` fails at the point of
use with *"The term 'claude --plugin-dir ...' is not recognized"*. `@args` forwards anything else
you pass, so `claude-hades --resume` still works:

```
function claude-hades { claude --plugin-dir <path-to-your-Hades-checkout>\ClaudeCodePlugin @args }
```

That lasts for the session. To keep it, append the same line to your profile — `$PROFILE` names the
file, and it does **not** exist by default, so create it first:

```
if (-not (Test-Path $PROFILE)) { New-Item -ItemType File -Path $PROFILE -Force }
notepad $PROFILE
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
Hades is running. Enabling launch-at-login for Hades — the Claude Code onboarding step's
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
