# Connecting an agent to Hades

Hades serves its knowledge graph over **MCP (Streamable HTTP)** at a fixed local address, so the
tools are not tied to one agent. The skills and commands are a different story, and this page is
explicit about where the line falls.

## What is portable, and what is not

| | Portable to any MCP client | Claude Code only |
|---|---|---|
| **33 MCP tools** | ✅ plain MCP, nothing client-specific in any tool description | |
| **23 skills** | | ✅ Claude Code plugin skills (`SKILL.md`) |
| **6 commands** | | ✅ Claude Code slash commands |

There is **no implied parity**. Another client gets the tools — the graph, the queries, the traces,
the memory, the Editor actions — and does not get the skills that tell an agent *when* to reach for
them. That is a real difference in behaviour, not a packaging detail.

## Any MCP client

- **Transport:** Streamable HTTP
- **URL:** `http://127.0.0.1:7823/mcp`
- **Auth:** none
- **Reachability:** loopback only. Verified: a request to the same port on this machine's LAN
  address is refused, and `GET /` returns 404 — only `/mcp` is served.

### Verify it before configuring anything

This needs no client at all, which makes it the fastest way to tell "Hades is not running" apart
from "my client is misconfigured":

```bash
curl -s -X POST http://127.0.0.1:7823/mcp -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"1"}}}'
```

A healthy app answers with `"serverInfo":{"name":"Hades","version":"2.2.0"}` and
`"capabilities":{"logging":{},"tools":{}}`. Then count the tools:

```bash
curl -s -X POST http://127.0.0.1:7823/mcp -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' | sed -n 's/^data: //p' | python3 -c 'import json,sys; print(len(json.load(sys.stdin)["result"]["tools"]))'
```

That prints `33`. (Counting `"name"` with `grep` instead gives 42 — some tool schemas have a `name`
property of their own.)

### How Hades decides which project you mean

It reads the client's **MCP roots** and matches them against the Unity projects it knows. If your
client does not report roots, or reports several projects at once, pass the project explicitly:
**31 of the 33 tools take a `project` parameter** (`hades_ping` and `hades_status` do not need one —
`hades_status` lists every project it knows, which is where the handles come from).

### What Hades does not implement

The server declares `logging` and `tools`. There are no MCP **prompts** and no MCP **resources** — a
client that lists them will find none. Nothing errors; there is simply nothing there.

## Claude Code — the baseline

The only client Hades is developed against, and the only one these tools have actually been used
with.

```
/plugin marketplace add TheArcForge/hades-plugin
```

then `/plugin install hades`. Run `/mcp` afterwards and confirm `hades` reports **33 tools**.

Working from a clone instead? Point Claude Code at the plugin directly — per-session, so pass it
every time:

```sh
claude --plugin-dir <your-Hades-checkout>/ClaudeCodePlugin
```

## Other clients

| Client | Status |
|---|---|
| **Gemini CLI** | **Connects** — verified 2026-11-15, CLI 0.68.0, macOS 26.6.2. Tool calls blocked: Google no longer lets an individual account sign in to this CLI. |
| **Codex CLI** | Config from the vendor's docs; not run. |
| **Cursor** | Config shape unconfirmed; not run. |
| **VS Code / Copilot** | Not attempted. |

> Everything below that is not marked verified is **transcribed from the vendor's current official
> documentation**. That tells you the endpoint shape the client expects — not that Hades works
> through it. If you try one, please [open an
> issue](https://github.com/TheArcForge/Hades/issues) with what happened.

### OpenAI Codex CLI — not run

Per the [official MCP guide](https://developers.openai.com/codex/mcp), Codex stores MCP servers in
`~/.codex/config.toml` (or `.codex/config.toml` for a trusted project), and a Streamable HTTP server
is one that has a `url`:

```toml
[mcp_servers.hades]
url = "http://127.0.0.1:7823/mcp"
```

`codex mcp list` shows configured servers, and `/mcp` inside the TUI shows the active ones.

Worth knowing: Codex reads the MCP `instructions` field from `initialize` and uses it as server-wide
guidance. **Hades returns no `instructions`**, so a Codex user gets the 33 tools with no guidance
attached — the gap the Claude Code skills fill today.

### Gemini CLI — connection verified

Streamable HTTP uses the **`httpUrl`** key (a plain `url` means SSE) inside `mcpServers` in
`~/.gemini/settings.json`:

```json
{
  "mcpServers": {
    "hades": {
      "httpUrl": "http://127.0.0.1:7823/mcp"
    }
  }
}
```

Check it with:

```bash
gemini mcp list
```

A working setup prints `✓ hades: http://127.0.0.1:7823/mcp (http) - Connected`.

**The gotcha that will bite you first: folder trust.** Gemini CLI disables MCP servers in a folder
you have not trusted — *including user-level servers* — and `gemini mcp list` then reports `○ hades
… - Disabled` with no hint that Hades is fine. Trust the project folder (the CLI offers this
interactively, and records it in `~/.gemini/trustedFolders.json` as `"TRUST_FOLDER"`), then run the
check again.

**Verified 2026-11-15:** Gemini CLI **0.68.0** on macOS 26.6.2 (Darwin 25.6.0), run from a Unity
project directory, connects to Hades 2.2.0 and reports the server as `Connected`. Note that the MCP
connection itself needs no Gemini account — `gemini mcp list` connects before you have signed in at
all, which is what makes it a clean check of Hades rather than of your Google setup.

**What we could not test, and it is not a Hades problem.** Using the tools needs the model, and
**"Sign in with Google" fails on an individual account**. Gemini CLI 0.68.0 — the latest published
release — answers:

> Failed to sign in. Message: This client is no longer supported for Gemini Code Assist for
> individuals. To continue using Gemini, please migrate to the Antigravity suite of products:
> https://antigravity.google

So on this CLI an individual needs a **`GEMINI_API_KEY`** (option 2 in its sign-in picker) or
**Vertex AI** with a billed GCP project (option 3). A Gemini subscription on its own is not enough.
If you have an API key, tool execution should work and an issue report saying so would be genuinely
useful — it is the one row nobody has filled in.

Google's own redirect points at **Antigravity**, which speaks MCP too (its config lives in
`~/.gemini/antigravity/mcp_config.json`, under `mcpServers`, with a `serverUrl` key rather than
`httpUrl`). We have not run it, so that shape is unverified here.

### Cursor — not run

Cursor reads `mcpServers` from `.cursor/mcp.json` (project) or `~/.cursor/mcp.json` (global), and a
remote server is declared with a `url` and no `command`:

```json
{
  "mcpServers": {
    "hades": {
      "url": "http://127.0.0.1:7823/mcp"
    }
  }
}
```

**Caveat, honestly flagged:** sources disagree on whether an explicit transport field is required
for Streamable HTTP as opposed to SSE — some show `"transport"`, some `"type": "streamable-http"`,
and some say the URL is auto-detected. We could not confirm the current field from Cursor's own
documentation, so if the plain form above fails, an explicit transport field is the first thing to
try.

### VS Code — GitHub Copilot agent mode

Not attempted. Copilot agent mode needs a GitHub Copilot subscription, and the configuration moved
recently (workspace `.mcp.json`, with the older "Local harness" deprecated), so transcribing a shape
we have not read in full would be guessing. This section will be filled in when the client is
actually run.

## Reporting a client that works

If you connect an agent that is not listed here, an issue saying **which client, which version, and
the config block that worked** is directly useful: it is what turns a row above from "documented" to
"verified".
