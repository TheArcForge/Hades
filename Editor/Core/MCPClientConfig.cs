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

            var settings = new HadesSettings();

            if (settings.DesktopIntegration)
                UpdateClaudeDesktopConfig(launcherPath);

            WriteProjectMcpJson(launcherPath);
            WriteProjectClaudeMd();
            InstallSkills(settings.SkillsScope);
        }

        /// <summary>
        /// Copies the launcher to &lt;hubDir&gt;/launcher.js and writes hub-path.json beside it.
        /// hubDir is resolved by HadesPaths — the project's .arcforge/hades-hub by default.
        /// Returns the stable launcher path, or null if it can't be resolved.
        /// </summary>
        static string EnsureStableLauncher()
        {
            var hubDir = HadesPaths.HubDir;

            var stablePath = Path.Combine(hubDir, "launcher.js");

            var packageLauncherDir = FindPackageLauncherDir();
            if (packageLauncherDir == null) return File.Exists(stablePath) ? stablePath : null;

            var sourcePath = Path.Combine(packageLauncherDir, "dist", "index.js");
            if (!File.Exists(sourcePath)) return File.Exists(stablePath) ? stablePath : null;

            if (!Directory.Exists(hubDir))
                Directory.CreateDirectory(hubDir);

            // Single-file copy is sufficient ONLY because the launcher is built as a self-contained
            // esbuild bundle (Bridge~/package.json `build:launcher`) — it has no relative sibling
            // imports to resolve from this stable location. If the launcher build ever reverts to a
            // multi-file `tsc` emit, copying just index.js here would crash the launcher at startup
            // with ERR_MODULE_NOT_FOUND. The bundle invariant is regression-guarded by
            // Bridge~/tests/launcher/bundle.test.ts.
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
        /// Writes/updates claude_desktop_config.json so Claude Desktop (Chat/Cowork) can reach
        /// the hub.
        ///
        /// This is the one Hades write that CANNOT be project-local: Claude Desktop is a single
        /// global application with exactly one config file. Gated by the `desktop_integration`
        /// setting, which defaults to OFF for two reasons: nothing should leave the workspace
        /// unasked, and the entry is inert under the default local hub scope anyway. Desktop
        /// spawns the launcher with a cwd outside the project, so findProjectRoot returns null and
        /// resolveHubDir falls through to $HOME/.arcforge/hades-hub, while a local-scope Unity
        /// publishes hub.json into the project's own hub dir — the two never meet. Desktop
        /// therefore needs `hub_scope: global` today; see the roadmap item on passing
        /// HADES_HUB_DIR through the Desktop entry to lift that restriction.
        ///
        /// Turning the setting off does not remove an existing entry — Hades does not exclusively
        /// own this file, and a stale entry is harmless.
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
                            ["args"] = new JArray(McpLauncherArg(launcherPath, projectRoot))
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

        /// <summary>
        /// The value written to .mcp.json `args[0]` for the launcher.
        ///
        /// Project-relative whenever the launcher lives inside the project — the default local hub
        /// scope, i.e. .arcforge/hades-hub/launcher.js. Claude Code discovers .mcp.json in the
        /// directory it was started from, and spawns the server with that same directory as cwd, so
        /// a project-relative arg resolves to exactly the same file as the absolute one while
        /// keeping the committed-adjacent file free of one developer's home directory. The launcher
        /// itself already relies on that cwd (findProjectRoot walks up from process.cwd()), so this
        /// adds no new assumption.
        ///
        /// Falls back to the absolute path when the launcher is outside the project — global hub
        /// scope, or HADES_HUB_DIR pointing elsewhere — where no relative form exists.
        /// Forward slashes on every platform: Windows node accepts them, and it avoids escaping
        /// backslashes in JSON.
        /// </summary>
        internal static string McpLauncherArg(string launcherPath, string projectRoot)
        {
            if (string.IsNullOrEmpty(launcherPath) || string.IsNullOrEmpty(projectRoot))
                return ToForwardSlashes(launcherPath);

            string relative;
            try
            {
                relative = Path.GetRelativePath(projectRoot, launcherPath);
            }
            catch (ArgumentException)
            {
                return ToForwardSlashes(launcherPath);
            }

            if (Path.IsPathRooted(relative) || EscapesProject(relative))
                return ToForwardSlashes(launcherPath);

            return ToForwardSlashes(relative);
        }

        static bool EscapesProject(string relative)
            => relative == ".."
               || relative.StartsWith("../", StringComparison.Ordinal)
               || relative.StartsWith("..\\", StringComparison.Ordinal);

        static string ToForwardSlashes(string path) => path?.Replace("\\", "/");

        const string HadesMarkerStart = "<!-- HADES:START -->";
        const string HadesMarkerEnd = "<!-- HADES:END -->";

        static readonly string HadesClaudeMdContent = string.Join("\n", new[]
        {
            "# Hades — Agent Guidelines",
            "",
            "This is a Unity project with Hades installed. You have 89 MCP tools that give you deep structural understanding of the project — a knowledge graph of every scene, prefab, script, asset, and their dependencies.",
            "",
            "## IMPORTANT: Always use Hades tools first",
            "",
            "**Do NOT use bash commands (`grep`, `find`, `ls`, `cat`) for project exploration.** Hades tools are faster, more accurate, and understand Unity project structure. Only fall back to bash for things the graph does not cover (reading file contents, running shell commands, editing code).",
            "",
            "| Do NOT do this | Use this Hades tool instead |",
            "|---|---|",
            "| `grep -r \"PlayerController\"` | `search_by_name(\"PlayerController\")` — finds scripts, types, prefabs, assets by name |",
            "| `grep` to find what uses a script | `find_references_to(target_path)` — returns asset references AND C# code references (fields, params, inheritance, constructors) |",
            "| `find . -name \"*.cs\"` | `search_by_name(type_filter=\"Script\")` or `get_project_summary()` for counts |",
            "| `find . -name \"*.png\"` or `ls Assets/Textures` | `search_by_name(type_filter=\"Texture\")` — the graph indexes textures, models, audio, fonts, animations |",
            "| Reading `.unity` / `.prefab` files | `get_scene_summary` + `scene_get_hierarchy` / `prefab_get_contents` — parsed structure |",
            "| Guessing what depends on something | `trace_dependencies(asset_path)` — recursive dependency trace |",
            "| `cat` to read a `.cs` file for structure | `search_by_name` to find it → `find_references_to` for all dependents → then `Read` only if you need the implementation |",
            "",
            "## What the graph covers",
            "",
            "The knowledge graph indexes the **entire** project:",
            "",
            "- **Scripts**: every `.cs` file, class, struct, interface, method — plus cross-file type references (which script uses which types)",
            "- **Scenes & Prefabs**: full hierarchy, components, serialized field values, references between objects",
            "- **Materials & Shaders**: shader assignments, texture bindings, property values",
            "- **Textures, Models, Audio, Fonts, Animations**: all indexed by type with GUID-based reference tracking",
            "- **Unity builtin types**: MonoBehaviour, ScriptableObject, Editor, and ~4000 other Unity types — inheritance chains resolve fully",
            "- **ScriptableObjects**: both types and instances, with field values",
            "- **Addressables**: groups, entries, and asset mappings",
            "",
            "## How to approach common questions",
            "",
            "**\"Tell me about this project\"**",
            "→ `get_project_summary(depth=\"deep\")` → `get_scene_summary` for key scenes → `get_memory_summary` for documented decisions",
            "",
            "**\"Where is X used?\" / \"What would break if I remove X?\"**",
            "→ `search_by_name` to find it → `find_references_to` for all incoming references (both asset and C# code) → `trace_dependencies` for outgoing dependencies",
            "",
            "**\"How does [feature] work?\"**",
            "→ `search_by_name` for related scripts → `find_references_to` to see where they're used → `get_scene_summary` / `prefab_get_contents` to see how they're assembled → then read the actual code",
            "",
            "**\"I want to add/change [feature]\"**",
            "→ First understand what exists (graph queries above) → check `recall_memory` for relevant decisions/conventions → then propose the approach",
            "",
            "**\"Something is broken\"**",
            "→ `project_get_console_log` for errors → `hades_status` to verify graph is current → search the graph for related components → then investigate",
            "",
            "**\"Find all textures/models/audio in this folder\"**",
            "→ `search_by_name(path_prefix=\"Assets/Art\", type_filter=\"Texture\")` — filter by directory and asset type",
            "",
            "## Search tips",
            "",
            "- `search_by_name(name_pattern, type_filter, path_prefix, match_mode)` — `match_mode` supports `contains` (default), `exact`, `startswith`",
            "- `find_references_to` returns both **asset** references (prefabs, scenes, materials) and **C# code** references (field types, method params, constructors, casts, inheritance)",
            "- `trace_dependencies` follows references recursively — use `max_depth` to control how deep",
            "",
            "## Project memory",
            "",
            "This project may have documented decisions, patterns, and conventions in `.arcforge/memory/`. Use `recall_memory` to search for relevant context before making architectural suggestions. Use `propose_memory_update` to suggest documenting new decisions — never edit memory files directly.",
            "",
            "## Modifying the project",
            "",
            "When you need to change scenes, prefabs, components, or assets — use the MCP tools, not file editing. Unity assets are binary or complex YAML that should not be hand-edited:",
            "",
            "- **Scenes**: `scene_create_gameobject`, `scene_setup`, `component_add`, `component_set_properties`",
            "- **Prefabs**: `prefab_create`, `prefab_edit_property`, `prefab_open_editing` / `prefab_save_editing`",
            "- **Materials**: `material_create`, `material_set_property`, `material_assign`",
            "- **Animation**: `animation_create_controller`, `animation_assign_clip`",
            "- **References**: `reference_set` (for wiring up object references between components)",
            "",
            "For C# scripts: write and edit code files normally with your editor tools. Use `BeginScriptEditing` / `EndScriptEditing` to batch multiple script changes before triggering recompilation.",
            "",
            "## Available commands",
            "",
            "- `/hades:status` — graph state, server status, memory summary",
            "- `/hades:rebuild-graph` — regenerate knowledge graph from current project state",
            "- `/hades:show-traces` — inspect recent tool call traces (observability)",
            "- `/hades:validate-memory` — check memory files against graph",
            "- `/hades:show-proposals` — review pending memory update proposals",
            "- `/hades:export-traces` — export traces for analysis",
        });

        /// <summary>
        /// Writes or updates CLAUDE.md at the Unity project root for Claude Code agent guidance.
        /// Non-destructive: if a CLAUDE.md already exists, appends a marked Hades section.
        /// </summary>
        static void WriteProjectClaudeMd()
        {
            try
            {
                var projectRoot = PathSandbox.ProjectRoot;
                var claudeMdPath = Path.Combine(projectRoot, "CLAUDE.md");
                var hadesBlock = HadesMarkerStart + "\n" + HadesClaudeMdContent + "\n" + HadesMarkerEnd;

                if (File.Exists(claudeMdPath))
                {
                    var existing = File.ReadAllText(claudeMdPath);

                    var startIdx = existing.IndexOf(HadesMarkerStart, StringComparison.Ordinal);
                    var endIdx = existing.IndexOf(HadesMarkerEnd, StringComparison.Ordinal);

                    if (startIdx >= 0 && endIdx > startIdx)
                    {
                        // Replace existing Hades section
                        var updated = existing.Substring(0, startIdx)
                            + hadesBlock
                            + existing.Substring(endIdx + HadesMarkerEnd.Length);
                        AtomicWrite(claudeMdPath, updated);
                    }
                    else
                    {
                        // Append Hades section
                        var separator = existing.EndsWith("\n") ? "\n" : "\n\n";
                        AtomicWrite(claudeMdPath, existing + separator + hadesBlock + "\n");
                    }
                }
                else
                {
                    // Create new file with just the Hades content (no markers needed for a fresh file)
                    AtomicWrite(claudeMdPath, HadesClaudeMdContent + "\n");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Failed to write project CLAUDE.md: {ex.Message}");
            }
        }

        /// <summary>
        /// Installs Hades skills so a Claude client can discover them. Runs on every startup to
        /// keep them in sync with the installed package version.
        ///
        /// Local scope targets &lt;projectRoot&gt;/.claude/skills/, which Claude Code reads —
        /// nothing leaves the workspace. Global scope targets ~/.claude/skills/, which is the only
        /// location Claude Desktop reads.
        /// </summary>
        static void InstallSkills(HadesScope scope)
        {
            try
            {
                var skillsRoot = scope == HadesScope.Global
                    ? Path.Combine(HadesPaths.HomeDir, ".claude", "skills")
                    : Path.Combine(PathSandbox.ProjectRoot, ".claude", "skills");

                var packageSkillsDir = FindPackageSkillsDir();
                if (packageSkillsDir == null) return;

                foreach (var skillDir in Directory.GetDirectories(packageSkillsDir))
                {
                    var skillName = Path.GetFileName(skillDir);
                    var skillFile = Path.Combine(skillDir, "SKILL.md");
                    if (!File.Exists(skillFile)) continue;

                    var targetDir = Path.Combine(skillsRoot, "hades-" + skillName);
                    if (!Directory.Exists(targetDir))
                        Directory.CreateDirectory(targetDir);

                    File.Copy(skillFile, Path.Combine(targetDir, "SKILL.md"), true);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Failed to install skills: {ex.Message}");
            }
        }

        /// <summary>
        /// The installed package root, whatever the install channel — embedded (Packages/),
        /// registry, git URL, or local disk all resolve through PackageInfo. Falls back to the
        /// project root, which is correct when running Hades from a source checkout.
        ///
        /// Do NOT reintroduce a hardcoded "Packages/com.arcforge.hades" guess: a git-URL install
        /// lands in Library/PackageCache/com.arcforge.hades@&lt;hash&gt; and the guess silently
        /// misses, leaving the launcher uncopied and no .mcp.json written.
        /// </summary>
        static string PackageRoot()
        {
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo
                    .FindForAssembly(typeof(MCPClientConfig).Assembly);
                if (info != null && !string.IsNullOrEmpty(info.resolvedPath)
                    && Directory.Exists(info.resolvedPath))
                    return info.resolvedPath;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hades] Package path resolution failed: {ex.Message}");
            }

            return PathSandbox.ProjectRoot;
        }

        static string FindPackageSkillsDir()
        {
            var skillsDir = Path.Combine(PackageRoot(), "skills");
            return Directory.Exists(skillsDir) ? skillsDir : null;
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
            var launcherDir = Path.Combine(PackageRoot(), "Bridge~", "launcher");
            return Directory.Exists(launcherDir) ? launcherDir : null;
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
            => AtomicFile.Write(filePath, content);
    }
}
