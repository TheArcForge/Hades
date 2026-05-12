// Editor/MCP/Tools/CharonTools.cs
using ArcForge.Hades.Editor.Charon;
using ArcForge.Hades.Editor.Graph.Models;
using ArcForge.Hades.Editor.MCP;

namespace ArcForge.Hades.Editor.MCP.Tools
{
    public static class CharonTools
    {
        [MCPTool("hades_charon_status", "Returns Charon tracing status including whether collection is enabled and buffer state")]
        public static MCPToolResult CharonStatus()
        {
            var status = new
            {
                enabled = CharonEmitter.IsEnabled,
                buffer_count = CharonEmitter.BufferCount
            };

            return MCPToolResult.SuccessWithConfidence(
                status,
                ConfidenceBlock.High());
        }
    }
}
