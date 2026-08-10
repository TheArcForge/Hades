namespace Hades.Core.Mcp;

/// <summary>
/// The MCP endpoint's fixed, documented port - see <c>Hades.Server.Control.SettingsEndpoint</c>'s
/// own class doc comment for why 7823 is chosen deliberately rather than left to Kestrel's bare
/// default. This is a fact about the app's identity (every client that dials in statically -
/// <c>Plugin-ClaudeCode~/.mcp.json</c>, and v1.2's leftover install that <c>V12Cleanup</c> warns
/// about conflicting with it - expects to find it on this port), not something specific to the
/// ASP.NET Core hosting layer, so it lives in Hades.Core rather than Hades.Server.
///
/// <b>Why here and not duplicated:</b> Hades.Core cannot reference Hades.Server (see that
/// project's own EnsureHeadless build guard - the dependency runs the other way, Server references
/// Core), so before this type existed, <c>V12Cleanup</c> (Hades.Core) carried its own copy of the
/// literal 7823 with a comment explaining why - a duplication that could drift silently if the
/// port were ever renumbered in only one place. This is now the single source of truth: both
/// <c>Hades.Server.Control.SettingsEndpoint.McpPort</c> and <c>V12Cleanup</c> reference
/// <see cref="Port"/> directly.
/// </summary>
public static class McpDefaults
{
    public const int Port = 7823;
}
