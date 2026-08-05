using System.ComponentModel;
using System.Text.Json.Serialization;
using Hades.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Hades.Server.Mcp;

public sealed record TraceHit
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("depth")] public required int Depth { get; init; }
}

public sealed record TraceDependenciesResult
{
    [JsonPropertyName("root")] public required string Root { get; init; }
    [JsonPropertyName("results")] public required IReadOnlyList<TraceHit> Results { get; init; }
    [JsonPropertyName("truncated")] public required bool Truncated { get; init; }
    [JsonPropertyName("totalReturned")] public required int TotalReturned { get; init; }
}

/// <summary>
/// Relationship queries: walks the `references` edges outward from one asset - the outward-facing
/// twin of <see cref="HadesTools.FindReferencesTo"/>. Same conventions as HadesTools throughout -
/// see its class doc comment for why project routing uses an explicit handle rather than MCP roots.
///
/// Plan 10 Task 6 removed this file's other five MCP tools (find_prefabs_with_component,
/// find_components_using_pattern, find_orphan_scripts, component_find, event_find_all - all folded
/// into graph_query/find_unset_references, see QueryTools.cs's and InspectTool.cs's own class doc
/// comments for the exact filter-combination each one maps to). trace_dependencies answers a
/// genuinely different question from all of them (a recursive walk, not a single-hop filter) and was
/// never part of that consolidation, so it - and this file - stay.
/// </summary>
[McpServerToolType]
public sealed class GraphTools(ProjectService projects)
{
    [McpServerTool(Name = "trace_dependencies", Title = "Trace Dependencies", ReadOnly = true, UseStructuredContent = true)]
    [Description("Everything a Unity asset depends on, walking `references` edges outward up to "
               + "a depth — the outward-facing twin of find_references_to. Only `references` "
               + "edges count as a dependency; instantiating a prefab (`instance_of`) or standing "
               + "in for a nested prefab's object (`corresponds_to`) are not included. Terminates "
               + "safely on reference cycles, which Unity projects contain routinely (a prefab "
               + "referencing a manager that references the prefab back). Takes the "
               + "project-relative path exactly as search_by_name returns it." + ToolSupport.SavedStateClause)]
    public TraceDependenciesResult TraceDependencies(
        [Description("Project-relative asset path to trace outward from, as returned by search_by_name")] string assetPath,
        [Description("How many hops outward to follow (1-10, default 3)")] int maxDepth = 3,
        [Description("Maximum dependency hits to return (1-500, default 200)")] int limit = 200,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            throw new McpException(
                "trace_dependencies needs an 'assetPath' — the project-relative path to trace "
                + "outward from, e.g. {\"assetPath\": \"Assets/Prefabs/Enemy.prefab\"}. "
                + "search_by_name returns paths in exactly this form.");
        }

        var productGuid = ToolSupport.ResolveProject(projects, project);

        var hits = projects.TraceDependencies(productGuid, assetPath, maxDepth)
            ?? throw new McpException(
                $"'{assetPath}' is not in the graph, so its dependencies cannot be traced. Check "
                + "the path with search_by_name — it must be project-relative (\"Assets/...\" or "
                + "\"Packages/...\"), not absolute. Note that an asset with no .meta file cannot "
                + "be resolved.");

        var clampedLimit = Math.Clamp(limit, 1, 500);
        var limited = hits.Take(clampedLimit).ToList();

        return new TraceDependenciesResult
        {
            Root = assetPath,
            Results = limited.Select(h => new TraceHit { Path = h.Path, Depth = h.Depth }).ToList(),
            Truncated = hits.Count > limited.Count,
            TotalReturned = limited.Count,
        };
    }
}
