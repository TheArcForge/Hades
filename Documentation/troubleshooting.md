# Hades — Troubleshooting Guide

Quick reference for diagnosing and fixing common issues with Hades.

---

## Where the hub files live

Several fixes below refer to `<hubDir>`. Hades resolves that directory in this order:

1. The `HADES_HUB_DIR` environment variable, if set.
2. `<projectRoot>/.arcforge/hades-hub/` — the default, used when Hub scope is `Local`.
3. `~/.arcforge/hades-hub/` — used when Hub scope is `Global`, and as the fallback when the project root can't be determined.

**Project Settings → Hades** shows the resolved path for the current project, and is where the Hub scope setting lives. Substitute that path wherever the tables say `<hubDir>`.

---

## Common Issues

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| No Hades tools appear in Claude Code | Hub not running or Unity not registered | Check `<hubDir>/hub.json` exists and contains a valid PID. Is Unity running with Hades installed? Try restarting the Claude Code session. |
| MCP tools missing when Claude Code runs from outside the project directory | A project-local hub isn't discoverable from outside the project, so the launcher falls back to the global hub and finds nothing there | `cd` into the Unity project before starting Claude Code. If you genuinely need to work from elsewhere — a `file:`-referenced package repo, for instance — either switch Hub scope to `Global` at Project Settings → Hades (takes effect on the next Claude Code session), or start the session with `HADES_HUB_DIR=<projectRoot>/.arcforge/hades-hub claude`. |
| Tools disappear after Unity recompile | Domain reload in progress | Wait ~10 seconds. The Hub buffers requests during Unity's domain reload and resumes automatically. |
| "Server hades unavailable" or "No Unity instance found" while the Unity Editor is still running | Unity's main thread is napped (macOS App Nap), typically right after a recompile/domain reload or a deep idle, so the post-reload re-registration never gets the one main-thread tick it needs | Run `<hades-package>/Scripts/wake-unity.sh` — it briefly brings Unity to the foreground (un-napping the main thread so it re-registers) and restores your previous app focus, then retry the call. See "Recovering a stalled MCP connection" below. |
| Wrong Unity project receives tool calls | Hub matched the wrong project by directory | Launch Claude Code from the correct Unity project directory. The Hub routes by matching the working directory to registered projects. |
| Hub won't start | Node.js not found or hub-path.json missing | Run `node --version` to verify Node.js 20+ is installed. Check `<hubDir>/hub-path.json` exists and points to a valid `index.js`. Unity rewrites it on every server start, so reopening Unity is the usual fix. |
| Dashboard won't open | Port file IPC timeout | Verify Node.js is available. Try **Hades > Stop Charon Dashboard** from the Unity menu, then restart. Check the Unity console for error messages. |
| "Agent doesn't know about my project" | Stale or missing graph | Run `/hades:rebuild-graph` to regenerate the knowledge graph from current project state. |
| Graph rebuild is very slow | Large project (50k+ assets) | Expected for large projects. Check the Architecture doc §2.6 for target timings. The Node.js script scanner (Phase 5c) significantly improved performance. |
| Memory validation warnings look wrong | Stale validation results | Run `/hades:validate-memory` to re-validate all memory files against current graph state. |
| Agent ignores documented decisions | Memory files not loaded | Verify `.arcforge/memory/` contains your markdown files. Check that frontmatter YAML is valid. Restart Unity if needed. |

---

## First-Run Issues

Issues that appear on a fresh install and are often mistaken for real failures:

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| Unity shows `DllNotFoundException` for `libgilzoide-sqlite-net` on macOS | macOS quarantine blocks native libraries in zip downloads | Run `xattr -dr com.apple.quarantine <path-to-Hades-folder>` in Terminal, then restart Unity. Using git URL install avoids this entirely. |
| Graph build missing C# nodes (graph much smaller than expected) | Scanner `npm install` failed silently on first boot | Run `cd <Hades-package-path>/Scanner~ && npm install` manually. Check the output for errors. Then run `/hades:rebuild-graph`. |
| Graph build log shows "exit 100" | Node.js not found on PATH | Install Node.js 20+ and restart Unity. Run `node --version` to verify. |
| Graph build log shows "exit 101" | Scanner `npm install` failed (network error, native compilation error) | Run `cd <Hades-package-path>/Scanner~ && npm install` manually to see the full error output. |
| MCP shows "failed" on first Claude Code connect, works after Reconnect | Launcher startup race — Hub wasn't ready when Claude Code sent `initialize` | This is fixed in v0.9.1+. On older versions, click Reconnect in Claude Code's MCP panel. |
| Build log shows `[Hades] Pending edges: N resolved, K unresolvable …` | Most pending edges reference asset types Hades doesn't index (textures, meshes, audio) | This is expected. The log distinguishes resolved vs. unresolvable edges. See "Asset Coverage" below. |

---

## Recovery Procedures

For more serious issues requiring manual intervention:

| Condition | Recovery |
|-----------|----------|
| Graph database corruption | Delete `.arcforge/graph.db` (and `-wal`, `-shm` files if present). Restart Unity — Hades rebuilds automatically. |
| Trace database issues | Delete `.arcforge/traces.db`. Restart Unity — Charon creates a fresh database. Note: historical traces will be lost. |
| MCP tools disappear after Unity restart | Check `<hubDir>/hub.json` — confirm the PID is still alive. Unity re-registers on the next heartbeat (a background timer that keeps running even when the editor is backgrounded, and re-registers automatically if the Hub has evicted it). If the Hub PID is dead, delete `hub.json` and restart your Claude Code session. |
| Hub won't exit cleanly | Find the Hub process: `ps aux \| grep hades-hub`. Kill it manually: `kill <PID>`. Delete `<hubDir>/hub.json`. The next Claude Code session spawns a fresh Hub. |
| Leftover global hub directory after moving to local scope | `~/.arcforge/hades-hub/` was the hub location before Hades installed project-local, and Hades neither moves nor deletes it — it can't know whether another project on the machine still uses it | Safe to remove by hand once every Unity project on the machine uses `Local` hub scope and no hub process is running. Confirm with `ps aux \| grep hades-hub` (expect no matches), then `rm -rf ~/.arcforge/hades-hub`. Nothing in there needs preserving: `launcher.js` and `hub-path.json` are regenerated on every Unity server start, and `hub.json`, `hub.lock`, and `pending/` are runtime state only. |
| Memory file frontmatter broken | Open the file in a text editor. Ensure the YAML block between `---` markers is valid. Common mistakes: missing colon after a key name, or tabs used instead of spaces. |
| Tool calls timing out | The tool timeout is fixed at 30s in the transport and is not user-configurable. The most common cause is a large graph rebuild blocking the main thread — wait for the rebuild to complete and retry. |
| Unity is slow with Hades enabled | Disable Tier 2 inference if it isn't needed: add `enabled: false` to `.arcforge/config.yaml` (this is the only key `config.yaml` supports for Asphodel inference). The file-change debounce delay is hardcoded and not configurable. |

---

## Recovering a stalled MCP connection (macOS)

If an MCP tool call returns **"Server hades unavailable"** or **"No Unity instance found"** while the Unity Editor process is still alive, the editor's main thread is napped — typically right after a recompile/domain reload, or after a deep idle.

**Why it happens:** Hades keeps its Hub registration fresh with a background-timer heartbeat that survives a napped main thread, so a *running* server stays registered on its own. But across a domain reload the server is torn down and re-created, and that re-creation is gated on a single `EditorApplication.delayCall` tick. A napped, backgrounded editor starves that tick, so the fresh server never bootstraps and the Hub's TTL eventually evicts the stale entry.

**The fix** is to give Unity's main thread one tick by briefly bringing it to the foreground. The `wake-unity.sh` helper does this without leaving you staring at Unity — it captures your current frontmost app, activates Unity for ~2s, then restores your previous focus:

```bash
<hades-package>/Scripts/wake-unity.sh        # ~2s focus round-trip, then retry the MCP call
```

Or inline, if you don't have the script path handy:

```bash
PREV=$(osascript -e 'tell application "System Events" to name of first application process whose frontmost is true')
osascript -e 'tell application "Unity" to activate'
sleep 2
osascript -e "tell application \"System Events\" to set frontmost of process \"$PREV\" to true"
```

After it runs, retry the failed call — it should succeed. Routine idle no longer needs this; the background heartbeat keeps a running server registered on its own. The wake step is only for the reload/cold-bootstrap boundary, where the main thread must tick once for the new server to register.

---

## Asset Coverage

Hades indexes the following asset types into the knowledge graph:

**Scanned via C# scanners (Unity API):**

- Scenes (`.unity`)
- Prefabs (`.prefab`)
- ScriptableObjects (`.asset`)
- Materials (`.mat`)
- Shaders (`.shader`, `.shadergraph`)
- Addressable groups and labels
- Project Settings files

**Scanned via Node.js scanner (file I/O):**

- C# scripts (`.cs`) — type declarations, methods, fields, and cross-file references via tree-sitter
- Textures (`.png`, `.jpg`, `.jpeg`, `.tga`, `.bmp`, `.psd`, `.gif`, `.hdr`, `.exr`, `.tif`, `.tiff`)
- Models (`.fbx`, `.obj`, `.blend`, `.dae`, `.3ds`, `.max`, `.ma`, `.mb`)
- Audio clips (`.wav`, `.mp3`, `.ogg`, `.aif`, `.aiff`)
- Animation clips (`.anim`)
- Animator Controllers (`.controller`)
- Fonts (`.ttf`, `.otf`, `.fontsettings`)
- Sprite Atlases (`.spriteatlas`)
- Signal Assets (`.signal`)
- Playable Assets (`.playable`)

**Not currently indexed:**

- Video clips
- Binary and proprietary assets

The MetaScanner (added in v0.9.5) creates Asset nodes for non-script types by reading `.meta` files for GUID, path, and type. This brings pending edges to near-zero — most cross-asset references now resolve fully. A small number of pending edges (typically < 10) may remain for assets in packages or external references.

---

## Diagnostic Commands

These commands help diagnose issues:

| Command | What it shows |
|---------|--------------|
| `/hades:status` | Current Hades state: graph version, node/edge count, trace count, memory file count, Hub connection |
| `/hades:rebuild-graph` | Triggers a full graph rebuild from scratch |
| `/hades:validate-memory` | Re-validates all memory files against current graph |
| `/hades:show-traces` | Opens the Charon dashboard to inspect recent traces |

**From the terminal:**

Run these from your Unity project directory, which is where the default (local) hub lives. In global hub scope, swap `.arcforge/hades-hub` for `~/.arcforge/hades-hub`.

```bash
# Check if Hub is running
cat .arcforge/hades-hub/hub.json

# Check Hub path configuration
cat .arcforge/hades-hub/hub-path.json

# Inspect this project's Hades settings (hub scope, skills scope, and the rest)
cat .arcforge/config.local.yaml

# Test Hub manually
curl -s http://localhost:<port>/api/status
```

---

## Getting Help

- [Architecture document](arcforge-hades-architecture.md) — full system design
- [Plugin document](arcforge-hades-plugin.md) — MCP Hub connectivity details (§3)
- [GitHub Issues](https://github.com/TheArcForge/Hades/issues) — report bugs or request features
