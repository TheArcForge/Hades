using ArcForge.Hades.Editor.MCP;

namespace ArcForge.Hades.Editor.Tools
{
    public static class DiagnosticTools
    {
        [MCPTool("hades_ping", "Returns a diagnostic message confirming Hades is running. Use this to verify the MCP connection is working.")]
        public static MCPToolResult Ping()
        {
            return MCPToolResult.Success(new
            {
                status = "ok",
                message = "Hades is alive",
                version = "0.1.0"
            });
        }
    }
}
