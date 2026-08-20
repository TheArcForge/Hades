using System.ComponentModel;
using System.Text.Json.Serialization;
using Hades.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Hades.Server.Mcp;

public sealed record TraceHit
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("depth")] public required int Depth { get; init; }
}

/// <summary>One dangling dependency — see <see cref="Hades.Core.Graph.DanglingDependency"/>'s own
/// class doc comment for exactly what this means and why it is reported rather than dropped
/// (F6-honesty). Wire-shaped twin of that Core type, same translation discipline every other
/// Result record in this tool surface follows.</summary>
public sealed record TraceDanglingHit
{
    [JsonPropertyName("fromPath")] public required string FromPath { get; init; }
    [JsonPropertyName("depth")] public required int Depth { get; init; }
    [JsonPropertyName("toGuid")] public required string ToGuid { get; init; }
    [JsonPropertyName("propertyPath")] public required string PropertyPath { get; init; }
}

public sealed record TraceDependenciesResult
{
    [JsonPropertyName("root")] public required string Root { get; init; }
    [JsonPropertyName("results")] public required IReadOnlyList<TraceHit> Results { get; init; }
    [JsonPropertyName("truncated")] public required bool Truncated { get; init; }
    [JsonPropertyName("totalReturned")] public required int TotalReturned { get; init; }

    /// <summary>
    /// F6-honesty, additive (every field above is unchanged from before it): dependencies whose
    /// target GUID resolves to no node anywhere in the graph — real `references` edges, just
    /// pointing somewhere this graph cannot resolve to a path (see <see cref="DanglingNote"/> for
    /// the two reasons that happens). Previously these vanished with no signal at all, making a
    /// material whose only dependencies were a texture and a shader read as "depends on nothing".
    /// </summary>
    [JsonPropertyName("dangling")] public required IReadOnlyList<TraceDanglingHit> Dangling { get; init; }

    /// <summary>How many dangling dependencies were found in total — the number that answers
    /// "is the list below complete", the same role <see cref="TotalReturned"/>/<see cref="Truncated"/>
    /// play for <see cref="Results"/>.</summary>
    [JsonPropertyName("danglingCount")] public required int DanglingCount { get; init; }

    [JsonPropertyName("danglingTruncated")] public required bool DanglingTruncated { get; init; }

    /// <summary>
    /// Authored server-side, rendered verbatim — never paraphrased or regenerated per call. Null
    /// when <see cref="DanglingCount"/> is 0: a measured report ties this note to what THIS query
    /// actually found, rather than appending a boilerplate caveat to every response regardless of
    /// relevance.
    /// </summary>
    [JsonPropertyName("danglingNote")] public string? DanglingNote { get; init; }
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
    /// <summary>Authored once, rendered verbatim into <see cref="TraceDependenciesResult.DanglingNote"/>
    /// — see that field's own doc comment for why this is measured (attached to a real per-call
    /// count) rather than a static disclaimer appended regardless of relevance.</summary>
    internal const string DanglingNote =
        "These dependencies exist as edges in the graph, but their target GUID has no node — "
        + "most often because the target lives outside every root Hades scans (a package "
        + "resolved into Library/PackageCache, such as a built-in shader or texture bundled "
        + "with a Unity package, is never walked regardless of its type), and occasionally "
        + "because its asset kind is one Hades does not yet index as a graph node. Either way "
        + "this does not mean the dependency is missing or safe to ignore — inspect_asset can "
        + "often still resolve the GUID directly from the referencing file's own properties.";

    [McpServerTool(Name = "trace_dependencies", Title = "Trace Dependencies", ReadOnly = true, UseStructuredContent = true)]
    [Description("Everything a Unity asset depends on, walking `references` and `instance_of` "
               + "edges outward up to a depth — the outward-facing twin of find_references_to, so "
               + "nested-prefab chains (a prefab instantiating another) are walked too. Only "
               + "`corresponds_to` is excluded: every nested instance that writes one also writes "
               + "an `instance_of` edge to the same file, so it would duplicate hits. Terminates "
               + "safely on reference cycles, which Unity projects contain routinely (a prefab "
               + "referencing a manager that references the prefab back). Takes the "
               + "project-relative path exactly as search_by_name returns it. A dependency whose "
               + "target lives outside every root Hades scans (e.g. a package resolved into "
               + "Library/PackageCache) or whose asset kind Hades does not yet index cannot appear "
               + "in 'results', but is not silently dropped either: it is reported under 'dangling' "
               + "instead, so an empty 'results' never masks a real, un-traceable dependency as "
               + "'depends on nothing'." + ToolSupport.SavedStateClause)]
    public async Task<TraceDependenciesResult> TraceDependencies(
        [Description("Project-relative asset path to trace outward from, as returned by search_by_name")] string assetPath,
        [Description("How many hops outward to follow (1-10, default 3)")] int maxDepth = 3,
        [Description("Maximum dependency hits to return (1-500, default 200)")] int limit = 200,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            throw new McpException(
                "trace_dependencies needs an 'assetPath' — the project-relative path to trace "
                + "outward from, e.g. {\"assetPath\": \"Assets/Prefabs/Enemy.prefab\"}. "
                + "search_by_name returns paths in exactly this form.");
        }

        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);

        var trace = projects.TraceDependencies(productGuid, assetPath, maxDepth)
            ?? throw new McpException(
                $"'{assetPath}' is not in the graph, so its dependencies cannot be traced. Check "
                + "the path with search_by_name — it must be project-relative (\"Assets/...\" or "
                + "\"Packages/...\"), not absolute. Note that an asset with no .meta file cannot "
                + "be resolved.");

        var clampedLimit = Math.Clamp(limit, 1, 500);
        var limitedHits = trace.Hits.Take(clampedLimit).ToList();
        var limitedDangling = trace.Dangling.Take(clampedLimit).ToList();

        return new TraceDependenciesResult
        {
            Root = assetPath,
            Results = limitedHits.Select(h => new TraceHit { Path = h.Path, Depth = h.Depth }).ToList(),
            Truncated = trace.Hits.Count > limitedHits.Count,
            TotalReturned = limitedHits.Count,
            Dangling = limitedDangling.Select(d => new TraceDanglingHit
            {
                FromPath = d.FromPath, Depth = d.Depth, ToGuid = d.ToGuid, PropertyPath = d.PropertyPath,
            }).ToList(),
            DanglingCount = trace.Dangling.Count,
            DanglingTruncated = trace.Dangling.Count > limitedDangling.Count,
            DanglingNote = trace.Dangling.Count > 0 ? DanglingNote : null,
        };
    }
}
