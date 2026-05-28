using ArcForge.Hades.Editor.MCP;
using UnityEditor.PackageManager;

namespace ArcForge.Hades.Editor.Tools
{
    public static class DiagnosticTools
    {
        static string PackageVersion =>
            PackageInfo.FindForAssembly(typeof(DiagnosticTools).Assembly)?.version ?? "unknown";

        [MCPTool("hades_ping", "Returns a diagnostic message confirming Hades is running. Use this to verify the MCP connection is working.")]
        public static MCPToolResult Ping()
        {
            return MCPToolResult.Success(new
            {
                status = "ok",
                message = "Hades is alive",
                version = PackageVersion
            });
        }
    }
}
