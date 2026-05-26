# Hades — Troubleshooting Guide

Quick reference for diagnosing and fixing common issues with Hades.

---

## Common Issues

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| No Hades tools appear in Claude Code | Hub not running or Unity not registered | Check `~/.arcforge/hades-hub/hub.json` exists and contains a valid PID. Is Unity running with Hades installed? Try restarting the Claude Code session. |
| Tools disappear after Unity recompile | Domain reload in progress | Wait ~10 seconds. The Hub buffers requests during Unity's domain reload and resumes automatically. |
| Wrong Unity project receives tool calls | Hub matched the wrong project by directory | Launch Claude Code from the correct Unity project directory. The Hub routes by matching the working directory to registered projects. |
| Hub won't start | Node.js not found or hub-path.json missing | Run `node --version` to verify Node.js 20+ is installed. Check `~/.arcforge/hades-hub/hub-path.json` exists and points to a valid `index.js`. |
| Dashboard won't open | Port file IPC timeout | Verify Node.js is available. Try **Hades > Stop Charon Dashboard** from the Unity menu, then restart. Check the Unity console for error messages. |
| "Agent doesn't know about my project" | Stale or missing graph | Run `/hades:rebuild-graph` to regenerate the knowledge graph from current project state. |
| Graph rebuild is very slow | Large project (50k+ assets) | Expected for large projects. Check the Architecture doc §2.6 for target timings. The Node.js script scanner (Phase 5c) significantly improved performance. |
| Memory validation warnings look wrong | Stale validation results | Run `/hades:validate-memory` to re-validate all memory files against current graph state. |
| Agent ignores documented decisions | Memory files not loaded | Verify `.arcforge/memory/` contains your markdown files. Check that frontmatter YAML is valid. Restart Unity if needed. |

---

## Recovery Procedures

For more serious issues requiring manual intervention:

| Condition | Recovery |
|-----------|----------|
| Graph database corruption | Delete `.arcforge/graph.db` (and `-wal`, `-shm` files if present). Restart Unity — Hades rebuilds automatically. |
| Trace database issues | Delete `.arcforge/traces.db`. Restart Unity — Charon creates a fresh database. Note: historical traces will be lost. |
| MCP tools disappear after Unity restart | Check `~/.arcforge/hades-hub/hub.json` — confirm the PID is still alive. Unity re-registers on the next heartbeat. If the Hub PID is dead, delete `hub.json` and restart your Claude Code session. |
| Hub won't exit cleanly | Find the Hub process: `ps aux \| grep hades-hub`. Kill it manually: `kill <PID>`. Delete `hub.json`. The next Claude Code session spawns a fresh Hub. |
| Memory file frontmatter broken | Open the file in a text editor. Ensure the YAML block between `---` markers is valid. Common mistakes: missing colon after a key name, or tabs used instead of spaces. |
| Tool calls timing out | Add `mcp.request_timeout_ms: 60000` to `.arcforge/config.yaml` to increase the timeout. Also check whether a large graph rebuild is in progress. |
| Unity is slow with Hades enabled | Increase the debounce delay in config. Disable Tier 2 inference if it isn't needed (`asphodel.tier2_enabled: false`). |

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

```bash
# Check if Hub is running
cat ~/.arcforge/hades-hub/hub.json

# Check Hub path configuration
cat ~/.arcforge/hades-hub/hub-path.json

# Test Hub manually
curl -s http://localhost:<port>/api/status
```

---

## Getting Help

- [Architecture document](arcforge-hades-architecture.md) — full system design
- [Plugin document](arcforge-hades-plugin.md) — MCP Hub connectivity details (§3)
- [GitHub Issues](https://github.com/TheArcForge/Hades/issues) — report bugs or request features
