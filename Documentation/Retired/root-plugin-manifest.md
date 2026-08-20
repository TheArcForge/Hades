# The retired root plugin manifest

`.claude-plugin/plugin.json` used to sit at the repository root,
where Claude Code treats it as an **installable plugin manifest**. It described the v1.2 product —
`"90 MCP tools"`, version `1.2.0` — and is recoverable from git history if ever needed.

## Why it moved

Both the retired v1.2 plugin and the current one are named `hades`, and both were installable.
Pointing Claude Code at this repository root therefore did something, rather than failing — and what
it did depended on the machine:

- **On a checkout that had run v1.2:** a generated root `.mcp.json` was also present, so the install
  succeeded end to end and silently bound to the ~90-tool MCP server running *inside the Unity
  Editor* — not the standalone app's 32. Nothing failed. It simply answered as the wrong generation
  of Hades, which is the worst shape a failure can take.
- **On a fresh clone:** no root `.mcp.json` exists (it is gitignored as a machine-specific runtime
  artifact), so the install produced skills and commands but no MCP server at all — confusing, but
  at least visibly incomplete.

Moving the manifest out makes both cases uniform and obvious: pointing at the repo root now finds no
plugin, which is immediately diagnosable.

For the record, the generated root `.mcp.json` contained:

```json
{
  "mcpServers": {
    "hades": {
      "command": "node",
      "args": ["${CLAUDE_PLUGIN_ROOT}/Bridge~/launcher/dist/index.js"]
    }
  }
}
```

It is not stored here — it was never in version control, and reproducing a generated artifact as
source would be a step backwards.

## What to use instead

`ClaudeCodePlugin/` is the current Claude Code plugin. It carries the same skills and commands,
and its `.mcp.json` reaches the standalone app over HTTP at `http://127.0.0.1:7823/mcp`.

## Note on migration

Retiring these files does **not** retire migration support. `V12Detector`, `V12Importer` and
`V12Cleanup` in `Core/src/Hades.Core/Migration/` read the *user's* machine — their `CLAUDE.md`,
`Packages/manifest.json`, Claude config and `~/.arcforge/` — never anything in this directory.
Someone migrating from a real v1.2 install is unaffected by this move.

A guard test asserts the repository root has no installable plugin manifest, so this cannot quietly
come back. See `Core/tests/Hades.Server.Tests/ClaudeCodePluginTests.cs`.
