using ModelContextProtocol;

namespace Hades.Core.Projects;

/// <summary>
/// Resolves an explicit project handle, or falls back to the sole known project - the ONE
/// implementation of this logic, so every caller that turns a "which project?" handle into a
/// productGuid agrees on both the algorithm and the wording of every failure. Two callers today:
/// <c>Hades.Server.Mcp.ToolSupport.ResolveProject</c>, kept as a one-line forward so none of the
/// existing MCP tool call sites had to change, and <see cref="Editors.EditorProxy"/>, which needs
/// the identical behaviour from Hades.Core itself - it cannot call the Server-side copy, since
/// Hades.Core must not reference Hades.Server (see Hades.Core.csproj's EnsureHeadless guard; the
/// dependency only ever runs the other way). This is why the logic lives here and Server forwards
/// to it, not the reverse.
///
/// Every failure names what to do next - Anthropic's tool guidance asks for "specific and
/// actionable improvements, rather than opaque error codes". A handle may be the project id or,
/// when unambiguous, the project name: the id is what hades_status hands out, but a model that has
/// seen a name will reach for it.
/// </summary>
public static class ProjectResolver
{
    public static string Resolve(ProjectService projects, string? handle)
    {
        var known = projects.KnownProjects();

        if (!string.IsNullOrWhiteSpace(handle))
        {
            var byId = known.FirstOrDefault(p =>
                p.ProductGuid.Equals(handle, StringComparison.OrdinalIgnoreCase));
            if (byId is not null) return byId.ProductGuid;

            var byName = known
                .Where(p => p.Name.Equals(handle, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (byName.Count == 1) return byName[0].ProductGuid;

            if (byName.Count > 1)
            {
                throw new McpException(
                    $"'{handle}' matches {byName.Count} projects by name. Pass the project id "
                    + $"instead: {string.Join(", ", byName.Select(p => p.ProductGuid))}.");
            }

            throw new McpException($"Unknown project '{handle}'. {Catalogue(known)}");
        }

        if (known.Count == 1) return known[0].ProductGuid;

        if (known.Count == 0)
        {
            throw new McpException(
                "Hades does not know about any project yet. Start the server with a project path, "
                + "e.g. `dotnet run --project src/Hades.Server -- /path/to/UnityProject`.");
        }

        throw new McpException(
            $"Hades knows {known.Count} projects, so this call needs a 'project' argument. {Catalogue(known)}");
    }

    static string Catalogue(IReadOnlyList<UnityProject> known) =>
        known.Count == 0
            ? "Call hades_status for details."
            : "Known projects: "
              + string.Join("; ", known.Select(p => $"{p.Name} ({p.ProductGuid})"))
              + ". Call hades_status for details.";
}
