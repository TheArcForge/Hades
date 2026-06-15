namespace ArcForge.Hades.Editor.Charon
{
    /// <summary>
    /// Canonical Charon span-attribute keys. Single source of truth shared by the emitter
    /// (MCPDispatcher) and the readers (Asphodel inference analyzers) so they cannot silently
    /// drift apart — a `tool.name` (emitted) vs `tool_name` (read) mismatch previously left
    /// every inference analyzer dead on arrival.
    /// </summary>
    public static class SpanAttributes
    {
        public const string ToolName = "tool.name";
        public const string ToolInput = "tool.input";
    }
}
