# Hades — Getting Started

This guide walks you through installing and verifying Hades from scratch. Follow the steps in order. By the end, your AI agent will have deep structural understanding of your Unity project.

## What you need before starting

| Requirement | How to check |
|---|---|
| **Unity 6000.0+** | Unity Hub → Installs |
| **Node.js 20+** | Run `node --version` in your terminal |
| **Claude Code** | Run `claude --version` in your terminal. Install from [claude.ai/download](https://claude.ai/download) if missing. |
| **A Unity project** | Any project works. A small one is fine for first-time setup. |

You should have received two zip files:

- **Hades.zip** — the Unity Package (installs into your Unity project)
- **hades-plugin.zip** — the Claude Code plugin (installs into your Claude Code environment)

## Step 1: Install the Unity Package

1. Unzip **Hades.zip** to a permanent location on your machine. For example:
   ```
   ~/Tools/Hades
   ```
   This folder must stay in place — Unity references it by path.

2. Open your Unity project.

3. Open **Window > Package Manager**.

4. Click the **+** button (top-left) and choose **Add package from disk...**

5. Navigate to the folder you unzipped and select the `package.json` file inside it.

6. Unity imports the package. You'll see "Hades" appear in the Package Manager list.

7. Wait for the initial graph build to complete. Watch the Unity console — you'll see a log message when it finishes. This takes 10–45 seconds depending on project size.

**Verification:** In the Unity console, you should see messages from Hades including "MCP server started". If you see compilation errors instead, check that you're on Unity 6000.0 or newer.

## Step 2: Install the Claude Code Plugin

1. Unzip **hades-plugin.zip** to a permanent location. For example:
   ```
   ~/Tools/hades-plugin
   ```
   This folder must also stay in place — Claude Code references it by path.

2. That's it — no install command needed. You'll point Claude Code to this folder when you launch it (next step).

**Verification:** You can check the plugin is valid by running:
```bash
claude plugin validate ~/Tools/hades-plugin
```
You should see "Validation passed".

## Step 3: Verify the connection

1. Make sure Unity is open with your project (and Hades is imported from Step 1).

2. In your terminal, `cd` into your Unity project directory:
   ```bash
   cd ~/Projects/YourUnityProject
   ```

3. Start Claude Code with the plugin:
   ```bash
   claude --plugin-dir ~/Tools/hades-plugin
   ```
   Replace the path with wherever you unzipped the plugin. The `--plugin-dir` flag tells Claude Code to load skills, commands, and the MCP server from that folder.

   > **Tip:** You'll need `--plugin-dir` each time you start Claude Code. To save typing, add an alias to your shell profile:
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
   cat ~/.arcforge/hades-hub/hub.json
   ```
   You should see a JSON file with a port and PID. If the file doesn't exist, restart your Claude Code session — the launcher starts the Hub automatically.

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

### Still stuck?

Check the full troubleshooting guide at `Documentation/troubleshooting.md` in the Hades folder, or reach out to whoever gave you the zip files.
