using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ArcForge.Hades.Editor.Core
{
    /// <summary>
    /// Auto-configures MCP clients (Claude Code + Claude Desktop) to connect
    /// to the running Hades MCP server.
    ///
    /// Architecture:
    ///   ~/.arcforge/
    ///     mcp-bridge.js          — cross-platform Node.js bridge script (standby mode)
    ///     servers/
    ///       {hash}.json          — one per running Hades instance (port, pid, project path)
    ///
    ///   {project}/.mcp.json      — Claude Code config (auto-generated, gitignored)
    ///   ~/Library/.../claude_desktop_config.json — Claude Desktop config (auto-updated)
    ///
    /// Both clients use command-based stdio config pointing at mcp-bridge.js with
    /// --project argument for exact project targeting. The bridge script:
    ///   1. Reads the server registry for the specified project
    ///   2. Waits (polls) if the server isn't running yet
    ///   3. Connects via npx mcp-remote when available
    ///   4. Reconnects automatically if Unity restarts
    /// </summary>
    public static class MCPClientConfig
    {
        const string BridgeVersion = "v3";
        const string McpJsonFilename = ".mcp.json";
        const string GlobalDir = ".arcforge";
        const string ServersDir = "servers";
        const string BridgeFilename = "mcp-bridge.js";

        static string GlobalRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), GlobalDir);
        static string ServersPath => Path.Combine(GlobalRoot, ServersDir);
        static string BridgeScriptPath => Path.Combine(GlobalRoot, BridgeFilename);
        static string McpJsonPath => Path.Combine(PathSandbox.ProjectRoot, McpJsonFilename);

        static string ProjectHash
        {
            get
            {
                using (var md5 = MD5.Create())
                {
                    var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(PathSandbox.ProjectRoot));
                    return BitConverter.ToString(bytes, 0, 6).Replace("-", "").ToLowerInvariant();
                }
            }
        }

        static string ProjectName => Path.GetFileName(PathSandbox.ProjectRoot);
        static string ServerEntryPath => Path.Combine(ServersPath, $"{ProjectName}-{ProjectHash}.json");

        /// <summary>
        /// Called when the MCP server starts. Registers this project and updates all client configs.
        /// </summary>
        public static void OnServerStart(int port)
        {
            EnsureBridgeScript();
            WriteServerEntry(port);
            WriteClaudeCodeConfig();
            UpdateClaudeDesktopConfig();
        }

        /// <summary>
        /// Called when the MCP server stops. Removes the server entry so the bridge
        /// knows to wait. Client configs stay (they point at the bridge, which handles standby).
        /// </summary>
        public static void OnServerStop()
        {
            RemoveServerEntry();
        }

        // --- Server Registry ---

        static void WriteServerEntry(int port)
        {
            try
            {
                if (!Directory.Exists(ServersPath))
                    Directory.CreateDirectory(ServersPath);

                var entry = new JObject
                {
                    ["projectName"] = ProjectName,
                    ["projectPath"] = PathSandbox.ProjectRoot,
                    ["port"] = port,
                    ["pid"] = Process.GetCurrentProcess().Id,
                    ["startedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                AtomicWrite(ServerEntryPath, entry.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Failed to write server entry: {ex.Message}");
            }
        }

        static void RemoveServerEntry()
        {
            try
            {
                if (File.Exists(ServerEntryPath))
                    File.Delete(ServerEntryPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Failed to remove server entry: {ex.Message}");
            }
        }

        // --- Claude Code (.mcp.json) ---

        static void WriteClaudeCodeConfig()
        {
            try
            {
                JObject root;
                if (File.Exists(McpJsonPath))
                {
                    var existing = File.ReadAllText(McpJsonPath);
                    root = JObject.Parse(existing);
                }
                else
                {
                    root = new JObject();
                }

                if (root["mcpServers"] == null)
                    root["mcpServers"] = new JObject();

                var servers = (JObject)root["mcpServers"];
                servers["hades"] = new JObject
                {
                    ["type"] = "stdio",
                    ["command"] = "node",
                    ["args"] = new JArray(BridgeScriptPath, "--project", PathSandbox.ProjectRoot)
                };

                AtomicWrite(McpJsonPath, root.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Failed to write {McpJsonFilename}: {ex.Message}");
            }
        }

        // --- Claude Desktop (claude_desktop_config.json) ---

        static void UpdateClaudeDesktopConfig()
        {
            try
            {
                var configPath = GetDesktopConfigPath();
                if (configPath == null) return;

                JObject root;
                if (File.Exists(configPath))
                {
                    var existing = File.ReadAllText(configPath);
                    root = JObject.Parse(existing);
                }
                else
                {
                    root = new JObject();
                }

                if (root["mcpServers"] == null)
                    root["mcpServers"] = new JObject();

                var servers = (JObject)root["mcpServers"];
                var entryName = $"hades-{ProjectName}";

                servers[entryName] = new JObject
                {
                    ["command"] = "node",
                    ["args"] = new JArray(BridgeScriptPath, "--project", PathSandbox.ProjectRoot)
                };

                AtomicWrite(configPath, root.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Failed to update Claude Desktop config: {ex.Message}");
            }
        }

        static string GetDesktopConfigPath()
        {
            string dir;
            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "Claude");
            }
            else if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                dir = Path.Combine(appData, "Claude");
            }
            else // Linux
            {
                var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                dir = Path.Combine(configHome, "Claude");
            }

            if (!Directory.Exists(dir))
                return null; // Claude Desktop not installed

            return Path.Combine(dir, "claude_desktop_config.json");
        }

        // --- Bridge Script ---

        static void EnsureBridgeScript()
        {
            try
            {
                if (!Directory.Exists(GlobalRoot))
                    Directory.CreateDirectory(GlobalRoot);

                // Check version to avoid unnecessary rewrites
                if (File.Exists(BridgeScriptPath))
                {
                    var existing = File.ReadAllText(BridgeScriptPath);
                    if (existing.Contains($"// hades-mcp-bridge {BridgeVersion}"))
                        return;
                }

                var script = GetBridgeScript();
                File.WriteAllText(BridgeScriptPath, script);
                Debug.Log($"[Hades] Bridge script installed at {BridgeScriptPath}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Failed to write bridge script: {ex.Message}");
            }
        }

        static string GetBridgeScript()
        {
            return @"#!/usr/bin/env node
// hades-mcp-bridge " + BridgeVersion + @"
// Cross-platform bridge for Claude Code / Claude Desktop -> Hades MCP server.
// Waits for the server to become available (standby mode), then connects
// via mcp-remote. Reconnects automatically if Unity restarts.

const { execFileSync, spawn } = require('child_process');
const fs = require('fs');
const path = require('path');

const POLL_INTERVAL_MS = 3000;
const SERVERS_DIR = path.join(__dirname, 'servers');

function parseArgs() {
    const args = process.argv.slice(2);
    let projectPath = null;
    for (let i = 0; i < args.length; i++) {
        if (args[i] === '--project' && i + 1 < args.length) {
            projectPath = args[i + 1];
        }
    }
    return { projectPath };
}

function findServerEntry(projectPath) {
    if (!fs.existsSync(SERVERS_DIR)) return null;

    const files = fs.readdirSync(SERVERS_DIR).filter(f => f.endsWith('.json'));

    for (const file of files) {
        try {
            const data = JSON.parse(fs.readFileSync(path.join(SERVERS_DIR, file), 'utf8'));
            if (projectPath && data.projectPath === projectPath) return data;
        } catch {}
    }

    // No --project specified: pick most recently started
    if (!projectPath) {
        let best = null;
        for (const file of files) {
            try {
                const data = JSON.parse(fs.readFileSync(path.join(SERVERS_DIR, file), 'utf8'));
                if (!best || data.startedAt > best.startedAt) best = data;
            } catch {}
        }
        return best;
    }

    return null;
}

function isProcessAlive(pid) {
    try { process.kill(pid, 0); return true; } catch { return false; }
}

function waitForServer(projectPath) {
    return new Promise((resolve) => {
        const check = () => {
            const entry = findServerEntry(projectPath);
            if (entry && isProcessAlive(entry.pid)) {
                resolve(entry);
                return;
            }

            // Clean up stale entries
            if (entry && !isProcessAlive(entry.pid)) {
                try {
                    const staleFile = fs.readdirSync(SERVERS_DIR)
                        .filter(f => f.endsWith('.json'))
                        .find(f => {
                            try {
                                const d = JSON.parse(fs.readFileSync(path.join(SERVERS_DIR, f), 'utf8'));
                                return d.pid === entry.pid;
                            } catch { return false; }
                        });
                    if (staleFile) fs.unlinkSync(path.join(SERVERS_DIR, staleFile));
                } catch {}
            }

            const label = projectPath ? path.basename(projectPath) : 'any project';
            process.stderr.write(`[Hades] Waiting for Unity MCP server (${label})...\n`);
            setTimeout(check, POLL_INTERVAL_MS);
        };
        check();
    });
}

function runBridge(entry) {
    return new Promise((resolve) => {
        const url = `http://127.0.0.1:${entry.port}/rpc`;
        process.stderr.write(`[Hades] Connecting to ${entry.projectName} on port ${entry.port}\n`);

        const child = spawn('npx', ['mcp-remote', url], {
            stdio: ['pipe', 'pipe', 'inherit'],
            shell: process.platform === 'win32'
        });

        // Pipe stdin/stdout between this process and mcp-remote
        process.stdin.pipe(child.stdin);
        child.stdout.pipe(process.stdout);

        child.on('exit', (code) => {
            process.stderr.write(`[Hades] mcp-remote exited (code ${code}), will reconnect...\n`);
            resolve();
        });

        child.on('error', (err) => {
            process.stderr.write(`[Hades] mcp-remote error: ${err.message}\n`);
            resolve();
        });

        // Forward termination signals to child
        const cleanup = () => { child.kill(); process.exit(0); };
        process.on('SIGINT', cleanup);
        process.on('SIGTERM', cleanup);
    });
}

async function main() {
    const { projectPath } = parseArgs();

    // Reconnection loop
    while (true) {
        const entry = await waitForServer(projectPath);
        await runBridge(entry);
        // mcp-remote exited — wait a beat, then look for server again
        await new Promise(r => setTimeout(r, 1000));
    }
}

main().catch(err => {
    process.stderr.write(`[Hades] Bridge error: ${err.message}\n`);
    process.exit(1);
});
";
        }

        // --- Helpers ---

        static void AtomicWrite(string filePath, string content)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tmpPath = filePath + ".tmp";
            File.WriteAllText(tmpPath, content);
            if (File.Exists(filePath))
                File.Delete(filePath);
            File.Move(tmpPath, filePath);
        }
    }
}
