using Hades.Core;
using Hades.Core.Projects;

namespace Hades.Server.Mcp;

/// <summary>Shared across every tool. The saved-state clause in particular must be identical
/// everywhere — a tool that quietly omits it is claiming freshness it does not have.</summary>
public static class ToolSupport
{
    /// <summary>Appended to every read-through tool's description. Callers act on these answers;
    /// they need to know the answer predates any unsaved Editor changes.</summary>
    public const string SavedStateClause =
        " Reflects the last saved state on disk — unsaved Editor changes are not visible.";

    /// <summary>Appended to every class-4 (live-state read) tool's description — the mirror of
    /// <see cref="SavedStateClause"/> above, for the three tools that clause could never honestly
    /// apply to. project_get_console_log, project_get_test_results, and inspector_inspect answer
    /// from the attached Editor's own live memory (the console scrollback, an in-progress test
    /// run, a GameObject's current serialized state) — there is no file on disk holding that
    /// answer for a read-through tool to parse instead, which is exactly why these three need a
    /// live Editor connection at all, unlike every SavedStateClause tool.</summary>
    public const string LiveStateClause =
        " Reflects only the attached Editor's live, in-memory state — there is no saved-file equivalent this could be read from instead.";

    /// <summary>
    /// Resolves an explicit handle, or falls back to the sole known project - a one-line forward
    /// to <see cref="ProjectResolver.Resolve"/>, the ONE implementation of this logic (also used
    /// directly by <c>Hades.Core.Editors.EditorProxy</c>, which cannot call this Server-side copy
    /// — see that class's own doc comment for why the logic itself lives in Hades.Core). Kept
    /// here, under this name, purely so none of the existing MCP tool call sites
    /// (<c>ToolSupport.ResolveProject(projects, project)</c>) had to change.
    /// </summary>
    public static string ResolveProject(ProjectService projects, string? handle) =>
        ProjectResolver.Resolve(projects, handle);
}
