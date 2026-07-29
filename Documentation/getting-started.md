# Hades — Getting Started

This guide walks you through installing and verifying Hades from scratch. Follow the steps in order. By the end, your AI agent will have deep structural understanding of your Unity project.

## What you need before starting

| Requirement | How to check |
|---|---|
| **Unity 6000.0+** | Unity Hub → Installs |
| **Node.js 20+** | Run `node --version` in your terminal |
| **Claude Code** | Run `claude --version` in your terminal. Install from [claude.ai/download](https://claude.ai/download) if missing. |
| **A Unity project** | Any project works. A small one is fine for first-time setup. |

Both the Unity package and the Claude Code plugin install directly from GitHub — no downloads required.

## Step 1: Install the Unity Package

1. Open your Unity project.

2. Open **Window > Package Manager**.

3. Click the **+** button (top-left) and choose **Add package from git URL...**

4. Enter the following URL and click **Add**:
   ```
   https://github.com/TheArcForge/Hades.git
   ```

5. Unity fetches and imports the package. You'll see "Hades" appear in the Package Manager list.

6. Wait for the initial graph build to complete. Watch the Unity console — you'll see a log message when it finishes. This takes 10–45 seconds depending on project size.

**Verification:** In the Unity console, you should see a message from Hades including `[Hades MCP] Server running on {endpoint}`. If you see compilation errors instead, check that you're on Unity 6000.0 or newer.

### Alternative: Install from local folder

If you can't reach GitHub from your machine, you can install from a local copy instead.

1. Obtain **Hades.zip** and unzip it to a permanent location on your machine. For example:
   ```
   ~/Tools/Hades
   ```
   This folder must stay in place — Unity references it by path.

2. **macOS only — remove the quarantine flag.** macOS blocks bundled native libraries downloaded from the internet. Before importing into Unity, run:
   ```bash
   xattr -dr com.apple.quarantine ~/Tools/Hades
   ```
   Replace the path with wherever you unzipped. Skipping this step causes native module load errors at runtime.

3. Open your Unity project.

4. Open **Window > Package Manager**.

5. Click the **+** button (top-left) and choose **Add package from disk...**

6. Navigate to the folder you unzipped and select the `package.json` file inside it.

7. Unity imports the package. Continue from step 6 of the main path above.

## Installation scope

By default, Hades keeps everything for a project *inside* that project. After Step 1 you'll find the whole installation under your Unity project root:

| Path | What it holds |
|---|---|
| `.arcforge/` | The knowledge graph, the trace database, memory files, the hub's runtime state, and your Hades settings |
| `.mcp.json` | How Claude Code reaches Hades when you launch it from this directory |
| `.claude/skills/` | The 22 Hades skills |
| `CLAUDE.md` | Project guidance for the agent (appended, never overwritten) |

Deleting the project directory removes the installation with it. Of the above, `.arcforge/memory/` is the part worth committing — it's shared team knowledge. The rest is machine-specific: the databases, `.arcforge/hades-hub/`, `.arcforge/config.local.yaml`, and `.mcp.json` are all regenerated locally and should stay out of version control.

### Switching to global

Two settings change where things land. Both live at **Project Settings → Hades**, which you can also reach from the **Hades → Settings…** menu.

**Hub scope** — `Local` (the default) or `Global`. Local gives this project its own hub process, with the hub's rendezvous files (`hub.json`, `hub.lock`, `pending/`, the stable `launcher.js` copy) under `<projectRoot>/.arcforge/hades-hub/`. Global gives you one hub shared by every Unity project on the machine, with those files in `~/.arcforge/hades-hub/`.

Choose Global if you work across a Unity project and a separate `file:`-referenced package repository in the same Claude Code session. A project-local hub isn't discoverable from outside the project directory, so a session started in the package repo can't find it.

Changing hub scope takes effect on your **next** Claude Code session — the launcher reads the setting when its process starts, not while it's running.

**Skills scope** — `Local` (the default) or `Global`. Local installs the skills to `<projectRoot>/.claude/skills/`; Global installs them to `~/.claude/skills/`. Claude Code reads both, so Local is all it needs. Claude Desktop does *not* read project-scoped skills, so choose Global if you use Claude Desktop.

To override the hub directory for a single Claude Code session — ignoring both the setting and the default — set `HADES_HUB_DIR`:

```bash
HADES_HUB_DIR=/path/to/hub claude
```

### The two things that stay outside your project

Hades can't quite contain everything, and it's worth knowing exactly what:

1. **Claude Desktop's config file** — `~/Library/Application Support/Claude/claude_desktop_config.json` (on Windows, `%APPDATA%\Claude\claude_desktop_config.json`). Claude Desktop is a single application with exactly one config file, so there is no project-local equivalent. Turn off **Claude Desktop Integration** at Project Settings → Hades to stop Hades writing it.
2. **`~/.claude/skills/`** — written only when Skills scope is `Global`.

So the fully isolated configuration is: Hub scope `Local`, Skills scope `Local`, Claude Desktop Integration **off**. With those three set, Hades writes nothing outside your project directory.

## Step 2: Install the Claude Code Plugin

The plugin installs in Step 3 below via the marketplace command — no separate download needed. If you need to validate the plugin manually, you can clone the repo and run:

```bash
claude plugin validate /path/to/hades-plugin
```
You should see "Validation passed".

## Step 3: Verify the connection

1. Make sure Unity is open with your project (and Hades is imported from Step 1).

2. In your terminal, `cd` into your Unity project directory:
   ```bash
   cd ~/Projects/YourUnityProject
   ```

3. Start Claude Code with the plugin. Choose the install method that suits you:

   **Option A: Persistent install (recommended)** — add the plugin once via the self-hosted marketplace and it loads automatically every session:
   ```
   /plugin marketplace add TheArcForge/hades-plugin
   /plugin install hades
   ```

   **Option B: Per-session** — pass the plugin directory each time you launch Claude Code:
   ```bash
   claude --plugin-dir ~/Tools/hades-plugin
   ```
   Replace the path with wherever you unzipped the plugin. The `--plugin-dir` flag tells Claude Code to load skills, commands, and the MCP server from that folder.

   > **Tip:** If you prefer Option B but want to save typing, add an alias to your shell profile:
   > ```bash
   > alias claude-unity='claude --plugin-dir ~/Tools/hades-plugin'
   > ```

4. Run the status command:
   ```
   /hades:status
   ```
   You should see output showing the graph node count, edge count, and Hub connection status.

If `/hades:status` is not recognized, the plugin didn't install correctly — go back to Step 2. If it runs but shows "no connection", check that Unity is running and see the Troubleshooting section below.

## Step 4: Try it out

With both pieces connected, try these prompts in Claude Code (from your Unity project directory):

**Ask about your project:**
```
Tell me about this project
```
The agent queries the knowledge graph and gives a project-specific overview — not a generic summary.

**Search structurally:**
```
Where is PlayerController used?
```
Replace `PlayerController` with any script in your project. This searches across scenes, prefabs, and script references.

**Analyze dependencies:**
```
What depends on [SomeScript]?
```
Traces references through the full project graph to show what would break if you removed something.

**Use a skill:**
```
I want to add a new enemy type
```
Hades skills activate automatically based on context. The agent uses Unity-specific decision frameworks instead of generic advice.

## Troubleshooting

### "No tools appear" or "/hades:status fails"

1. Is Unity running with your project open? Hades tools live inside the Unity Editor.
2. Did you `cd` into your Unity project directory before starting Claude Code? The Hub routes tool calls by matching your working directory to registered Unity projects.
3. Check if the Hub is running:
   ```bash
   cat .arcforge/hades-hub/hub.json
   ```
   Run that from your Unity project directory. If you set Hub scope to Global, look in `~/.arcforge/hades-hub/` instead; Project Settings → Hades shows the resolved path either way. You should see a JSON file with a port and PID. If the file doesn't exist, restart your Claude Code session — the launcher starts the Hub automatically.

4. If Claude Code is running from *outside* the Unity project directory, a project-local hub is invisible to it. Either `cd` into the project, switch Hub scope to Global, or point the session at the hub explicitly with `HADES_HUB_DIR`. See "Installation scope" above.

### "Tools were working, then stopped"

This usually means Unity recompiled scripts (domain reload). Wait about 10 seconds — the Hub buffers requests during recompilation and reconnects automatically.

### "Agent doesn't seem to know about my project"

The knowledge graph might be stale or not built. In Claude Code, run:
```
/hades:rebuild-graph
```
This regenerates the graph from the current project state.

### Scanner dependency (advanced)

The Scanner (which indexes C# scripts into the graph) requires a Node.js native module. Unity runs this automatically, but if graph building fails with a module error:

```bash
cd ~/Tools/Hades/Scanner~
npm install
```

This compiles the native SQLite module for your machine. You only need to do this once.

> **Note:** If you installed via git URL, the package lives inside Unity's package cache. Replace `~/Tools/Hades` above with the actual cache path shown in the error message.

### Still stuck?

Check the full troubleshooting guide at `Documentation/troubleshooting.md` in the Hades package, or reach out to whoever set you up with access.
