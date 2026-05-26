using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ArcForge.Hades.Editor.Core
{
    public static class MCPClientConfig
    {
        public static void OnServerStart(int port)
        {
            var launcherPath = EnsureStableLauncher();
            if (launcherPath == null) return;

            UpdateClaudeDesktopConfig(launcherPath);
            WriteProjectMcpJson(launcherPath);
        }

        /// <summary>
        /// Copies the launcher to ~/.arcforge/hades-hub/launcher.js and writes hub-path.json.
        /// Returns the stable launcher path, or null if it can't be resolved.
        /// </summary>
        static string EnsureStableLauncher()
        {
            var hubDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".arcforge", "hades-hub");

            var stablePath = Path.Combine(hubDir, "launcher.js");

            var packageLauncherDir = FindPackageLauncherDir();
            if (packageLauncherDir == null) return File.Exists(stablePath) ? stablePath : null;

            var sourcePath = Path.Combine(packageLauncherDir, "dist", "index.js");
            if (!File.Exists(sourcePath)) return File.Exists(stablePath) ? stablePath : null;

            if (!Directory.Exists(hubDir))
                Directory.CreateDirectory(hubDir);

            try
            {
                File.Copy(sourcePath, stablePath, true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Failed to copy launcher: {ex.Message}");
            }

            WriteHubPath(packageLauncherDir, hubDir);

            return stablePath;
        }

        /// <summary>
        /// Writes/updates claude_desktop_config.json so Claude Desktop (Chat/Cowork) can reach the hub.
        /// </summary>
        static void UpdateClaudeDesktopConfig(string launcherPath)
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

                servers["hades"] = new JObject
                {
                    ["command"] = "node",
                    ["args"] = new JArray(launcherPath)
                };

                AtomicWrite(configPath, root.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Failed to update Claude Desktop config: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes .mcp.json to the Unity project root so Claude Code auto-discovers the MCP server.
        /// Replaces any stale .mcp.json from the pre-Hub architecture.
        /// </summary>
        static void WriteProjectMcpJson(string launcherPath)
        {
            try
            {
                var projectRoot = PathSandbox.ProjectRoot;
                var mcpJsonPath = Path.Combine(projectRoot, ".mcp.json");

                var root = new JObject
                {
                    ["mcpServers"] = new JObject
                    {
                        ["hades"] = new JObject
                        {
                            ["command"] = "node",
                            ["args"] = new JArray(launcherPath)
                        }
                    }
                };

                AtomicWrite(mcpJsonPath, root.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Failed to write project .mcp.json: {ex.Message}");
            }
        }

        static void WriteHubPath(string packageLauncherDir, string hubDir)
        {
            try
            {
                // packageLauncherDir is Bridge~/launcher — hub is at Bridge~/hub
                var bridgeRoot = Path.GetDirectoryName(packageLauncherDir);
                var hubEntry = Path.Combine(bridgeRoot, "hub", "dist", "index.js");
                if (!File.Exists(hubEntry)) return;

                var hubPathFile = Path.Combine(hubDir, "hub-path.json");
                var json = $"{{\"hubEntry\":\"{hubEntry.Replace("\\", "/")}\"}}";
                File.WriteAllText(hubPathFile, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Failed to write hub-path.json: {ex.Message}");
            }
        }

        static string FindPackageLauncherDir()
        {
            var packageRoot = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "Packages", "com.arcforge.hades"));
            var launcherDir = Path.Combine(packageRoot, "Bridge~", "launcher");
            if (Directory.Exists(launcherDir)) return launcherDir;

            var devRoot = PathSandbox.ProjectRoot;
            launcherDir = Path.Combine(devRoot, "Bridge~", "launcher");
            if (Directory.Exists(launcherDir)) return launcherDir;

            return null;
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
            else
            {
                var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                dir = Path.Combine(configHome, "Claude");
            }

            if (!Directory.Exists(dir))
                return null;

            return Path.Combine(dir, "claude_desktop_config.json");
        }

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
