using System.Linq;
using UnityEditor.Compilation;
using UnityEngine;
using ArcForge.Hades.Editor.MCP;

namespace ArcForge.Hades.Editor.MCP.Tools
{
    public static class DomainReloadTools
    {
        [MCPTool("BeginScriptEditing", "Lock domain reload before modifying scripts in a batch. Call EndScriptEditing when done.")]
        public static MCPToolResult BeginScriptEditing()
        {
            var strategy = MCPServer.Instance?.ActiveReloadStrategy as ManualReloadStrategy;
            if (strategy == null)
                return MCPToolResult.Success("Domain reload strategy is Auto — lock is managed automatically. No action needed.");

            strategy.Lock();
            return MCPToolResult.Success("Domain reload locked. Modify scripts freely, then call EndScriptEditing.");
        }

        [MCPTool("EndScriptEditing", "Unlock domain reload and trigger compilation. Returns compile result.")]
        public static MCPToolResult EndScriptEditing()
        {
            var strategy = MCPServer.Instance?.ActiveReloadStrategy as ManualReloadStrategy;
            if (strategy == null)
            {
                // Auto mode: force-unlock in case auto-lock is still held, then trigger refresh
                UnityEditor.EditorApplication.UnlockReloadAssemblies();
                UnityEditor.AssetDatabase.Refresh();
                return MCPToolResult.Success("Domain reload unlocked. Scripts will recompile now.");
            }

            strategy.Unlock();

            CompilationPipeline.RequestScriptCompilation();

            if (UnityEditor.EditorUtility.scriptCompilationFailed)
            {
                var recentLogs = ConsoleLogBuffer.GetRecent(50);
                var errors = recentLogs
                    .Where(l => l.type == LogType.Error &&
                                (l.message.Contains("error CS") || l.message.Contains("CompilerError")))
                    .Select(l => l.message)
                    .ToArray();

                return MCPToolResult.Error(
                    $"Compilation failed ({errors.Length} error(s)):\n" +
                    string.Join("\n", errors.Length > 0 ? errors : new[] { "Check Unity console for details." }));
            }

            return MCPToolResult.Success("Domain reload unlocked. Compilation triggered — no errors detected.");
        }

        [MCPTool("project_recompile_scripts", "Force script recompilation and return compilation status with any errors")]
        public static MCPToolResult RecompileScripts()
        {
            UnityEditor.EditorApplication.UnlockReloadAssemblies();
            UnityEditor.AssetDatabase.Refresh(UnityEditor.ImportAssetOptions.ForceUpdate);

            if (UnityEditor.EditorUtility.scriptCompilationFailed)
            {
                var recentLogs = ConsoleLogBuffer.GetRecent(50);
                var errors = recentLogs
                    .Where(l => l.type == LogType.Error &&
                                (l.message.Contains("error CS") || l.message.Contains("CompilerError")))
                    .Select(l => l.message)
                    .ToArray();

                return MCPToolResult.Error(
                    $"Compilation failed ({errors.Length} error(s)):\n" +
                    string.Join("\n", errors.Length > 0 ? errors : new[] { "Check Unity console for details." }));
            }

            return MCPToolResult.Success("Scripts compiled successfully. No errors.");
        }
    }
}
