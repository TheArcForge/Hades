using Hades.Core;
using Hades.Core.Projects;

namespace Hades.Server.Mcp;

public sealed record RootResolution
{
    public string? ProductGuid { get; init; }
    public string? Error { get; init; }
    public bool IsResolved => ProductGuid is not null;

    public static RootResolution Resolved(string productGuid) => new() { ProductGuid = productGuid };
    public static RootResolution Failed(string error) => new() { Error = error };
}

/// <summary>
/// Maps a set of filesystem paths onto exactly one known project, adopting any that turn out to
/// be Unity projects. Used for path-driven entry points — startup seeding today, and the control
/// API's "add this folder" later.
///
/// NOT used for per-call routing: MCP roots are deprecated as of spec revision 2026-07-28
/// (SEP-2577), so tools resolve their project from an explicit handle instead — see
/// <see cref="HadesTools"/>.
///
/// Deliberately has no "only one project is known, so use it" fallback. That heuristic exists in
/// the v1.2 hub only because the launcher could not identify its own project, and when it
/// guesses wrong it silently routes work to the wrong project — which is worse than failing.
/// </summary>
public sealed class RootsRouter(ProjectService projects)
{
    public RootResolution Resolve(IReadOnlyList<string> roots)
    {
        if (roots.Count == 0)
            return RootResolution.Failed("No workspace roots were reported by the client.");

        var matches = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var root in roots)
        {
            if (ProjectIdentity.FindProjectRoot(root) is not { } projectRoot) continue;

            // Adopt, never index: routing runs on every tool call and must not trigger a scan.
            var project = projects.Adopt(projectRoot);
            if (project is not null) matches[project.ProductGuid] = project.Path;
        }

        if (matches.Count == 1) projects.EnsureIndexed(matches.Keys.First());

        return matches.Count switch
        {
            1 => RootResolution.Resolved(matches.Keys.First()),
            0 => RootResolution.Failed(
                $"No Unity project found in the current workspace. Roots checked: {string.Join(", ", roots)}"),
            _ => RootResolution.Failed(
                "Ambiguous workspace — several Unity projects are in scope: "
                + string.Join(", ", matches.Values.Select(Path.GetFileName))
                + ". Open Claude Code in a single project directory."),
        };
    }

}
