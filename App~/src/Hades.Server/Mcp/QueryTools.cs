using System.ComponentModel;
using System.Text.Json.Serialization;
using Hades.Core;
using Hades.Core.Graph;
using Hades.Core.Unity;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Hades.Server.Mcp;

public sealed record GraphQueryHit
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("namespace")] public string? Namespace { get; init; }
    [JsonPropertyName("line")] public required int Line { get; init; }
    [JsonPropertyName("fileId")] public required long FileId { get; init; }
}

public sealed record GraphQueryResult
{
    [JsonPropertyName("results")] public required IReadOnlyList<GraphQueryHit> Results { get; init; }
    [JsonPropertyName("truncated")] public required bool Truncated { get; init; }
    [JsonPropertyName("totalReturned")] public required int TotalReturned { get; init; }
}

/// <summary>
/// graph_query: the one general-purpose, structured-filter query surface over the graph, kept
/// apart from GraphTools.cs (and from every other tool in the whole port) because it is the ONLY
/// one whose input is caller/model-authored query intent rather than a single known path or name.
/// The original package this is ported from took a query string straight to a database; this is
/// deliberately NOT a port of that shape. It is reimplemented as a fixed set of scalar filters -
/// kind, name pattern, path prefix, edge relationship - every one a bound SQLite parameter via
/// <see cref="Graph.GraphDatabase.QueryGraph"/>, never string-concatenated, so raw SQL from a
/// caller is structurally impossible rather than merely filtered or escaped. See QueryGraphTests
/// (Hades.Core.Tests) for the SQL-injection and LIKE-escaping proof at the database level, and this
/// file's own tests for the same proof through the live MCP endpoint.
///
/// If a caller needs a query this structured filter cannot express, the answer is a new named
/// tool - not a richer filter language or an escape hatch back to raw SQL here.
///
/// <para><b>Plan 10 Task 4/6: graph_query.</b> <see cref="GraphQuery"/> extends the SAME structured
/// filter (six additive parameters, five on <see cref="Graph.GraphDatabase.QueryGraph"/> itself
/// plus fileType's own separate file-identity code path - see that method's own doc comment) to
/// absorb five more single-purpose searches, replacing all six -
/// its own former standalone predecessor query_graph, find_prefabs_with_component,
/// find_components_using_pattern, find_orphan_scripts, component_find, and asset_find
/// (GraphTools.cs/SettingsTools.cs, before Task 6 removed them) - with one filter surface. The
/// original query_graph tool was left untouched, byte for byte, through Tasks 4-5 deliberately (its
/// signature/behaviour was never migrated in place, to avoid churn on code with a known, short
/// remaining lifetime) and is now removed wholesale by Task 6's hard cutover, capability audit
/// passed.
///
/// <list type="bullet">
/// <item><description><b>query_graph</b> - graph_query's kind/namePattern/pathPrefix/edgeKind/
/// edgeDirection are query_graph's own five filters, unchanged.</description></item>
/// <item><description><b>find_prefabs_with_component(scriptPath)</b> -
/// <c>edgeKind: "references", edgeTargetPath: scriptPath</c>. edgeTargetPath resolves the path to a
/// guid (ProjectService.QueryGraph) and restricts the edge-exists check to edges pointing at that
/// ONE asset - exactly find_prefabs_with_component's join, minus its "prefabs only, never scenes"
/// restriction (no single Unity YAML kind means "this whole file is a prefab" - see
/// asset_find below); a caller distinguishes by each hit's own path suffix instead.</description></item>
/// <item><description><b>find_components_using_pattern(namePattern)</b> -
/// <c>edgeKind: "references", edgeTargetNamePattern: namePattern, edgeTargetKind: "Class"</c>.
/// Matches the SAME join (any file with a component referencing a script whose name matches),
/// across prefabs and scenes alike, exactly like the tool it replaces - including its
/// <c>sn.kind = 'Class'</c> restriction (<see cref="Graph.GraphDatabase.ComponentsUsingPattern"/>),
/// which is why <c>edgeTargetKind: "Class"</c> is part of the mapped call, not optional: without
/// it, a same-named non-script asset (a Material, a ScriptableObject, ...) also reached by a
/// <c>references</c> edge is an indistinguishable false positive - a genuine correctness gap the
/// Plan 10 Task 6 capability audit found and initially only documented, closed for real once
/// <c>edgeTargetKind</c> existed to express it.</description></item>
/// <item><description><b>find_orphan_scripts()</b> -
/// <c>kind: "Class", edgeKind: "references", edgeDirection: "incoming", edgeAbsent: true</c>. The
/// SAME honest-superset caveat carries forward unchanged - see this method's own
/// [Description].</description></item>
/// <item><description><b>component_find(typeNamePattern)</b> - two branches, exactly like the tool
/// it replaces: a builtin kind (<c>kind: "Rigidbody"</c>, already exact-matchable with no
/// extension) or a MonoBehaviour resolved through its script (<c>kind: "MonoBehaviour", edgeKind:
/// "references", edgeTargetNamePattern: typeNamePattern, edgeTargetKind: "Class"</c>) - the same
/// <c>edgeTargetKind: "Class"</c> addition as find_components_using_pattern above, restoring
/// <see cref="Graph.GraphDatabase.ComponentsMatching"/>'s own <c>sn.kind = 'Class'</c> join
/// condition on its MonoBehaviour-resolution branch.</description></item>
/// <item><description><b>asset_find(type, pathPrefix)</b> - fully reachable via the SEPARATE
/// <c>fileType</c> filter (see <see cref="GraphQuery"/>'s own parameter list), not via <c>kind</c>.
/// This was flagged as a genuine gap when this class doc comment was first written: Script,
/// Material, and AnimatorController map to exactly one node <c>kind</c> per file (<c>"Class"</c>,
/// <c>"Material"</c>, <c>"AnimatorController"</c> respectively), but Scene/Prefab/ScriptableObject
/// do not - a scene or prefab is many per-object nodes with no single summarising one, and a
/// ScriptableObject instance shares MonoBehaviour's generic kind with an ordinary component - so no
/// per-NODE filter could ever classify those three. <c>fileType</c> closes the gap by answering a
/// different, file-identity question instead: it reads <c>file_state</c> (see
/// <see cref="Graph.GraphDatabase.DistinctFileStatePaths"/>), one row per FILE, and classifies by
/// extension exactly as <see cref="Unity.AssetType.FromPath"/> always has - the same mechanism
/// asset_find itself used internally. This also makes <c>fileType</c> the more faithful replacement
/// for Script/Material/AnimatorController too: unlike <c>kind: "Class"</c>, which reports one hit
/// per top-level class, <c>fileType: "Script"</c> reports one hit per FILE, matching what asset_find
/// itself always returned.
/// </description></item>
/// </list>
/// </para>
/// </summary>
[McpServerToolType]
public sealed class QueryTools(ProjectService projects)
{
    static readonly string[] ValidEdgeDirections = ["outgoing", "incoming"];

    [McpServerTool(Name = "graph_query", Title = "Query Graph (Extended)", ReadOnly = true, UseStructuredContent = true)]
    [Description("General-purpose structured search over the knowledge graph - the single "
               + "consolidated replacement for query_graph, find_prefabs_with_component, "
               + "find_components_using_pattern, find_orphan_scripts, component_find, and "
               + "asset_find. Base filters, AND-combined, at least one required: kind (exact node "
               + "kind, e.g. \"Class\", \"MonoBehaviour\", \"GameObject\", \"Rigidbody\", "
               + "\"Material\", \"AnimatorController\"), namePattern (case-insensitive substring on "
               + "the node's own name), pathPrefix (project-relative), edgeKind (restrict to nodes "
               + "that are one endpoint of an edge of this kind - \"references\", \"instance_of\", "
               + "or \"corresponds_to\" - in edgeDirection, default \"outgoing\": this node is the "
               + "edge's source; \"incoming\": this node is the edge's target). "
               + "Four more filters, each requiring edgeKind and refused without it: edgeTargetPath "
               + "(further restrict to edges pointing at this ONE asset, e.g. a script - reproduces "
               + "find_prefabs_with_component; a path nothing in the graph owns simply matches "
               + "nothing, same as any other filter, never an error), edgeTargetNamePattern "
               + "(further restrict to edges whose target's own name matches this pattern - "
               + "reproduces find_components_using_pattern, and component_find's MonoBehaviour-"
               + "resolution branch when combined with kind: \"MonoBehaviour\" - by itself this does "
               + "NOT check the target's own kind, so a same-named non-script asset, e.g. a Material "
               + "or ScriptableObject also reached by a references edge, matches too; combine with "
               + "edgeTargetKind for that), edgeTargetKind (further restrict to edges whose target's "
               + "own node kind matches EXACTLY, e.g. \"Class\" - combined with edgeTargetNamePattern "
               + "this restores find_components_using_pattern's and component_find's old guarantee "
               + "that a name match actually resolved to a script, not a coincidentally-same-named "
               + "asset of some other kind; usable alone too, e.g. edgeTargetKind: \"Class\" to find "
               + "everything referencing ANY script. Omit to match a target of any kind, same as "
               + "before this filter existed), and edgeAbsent "
               + "(true to require NO matching edge instead of at least one - reproduces "
               + "find_orphan_scripts: kind: \"Class\", edgeKind: \"references\", edgeDirection: "
               + "\"incoming\", edgeAbsent: true finds script classes nothing references - HONEST "
               + "SUPERSET, not confirmed dead code: a class only ever instantiated from C# "
               + "(AddComponent<T>(), new, a static utility) has no incoming reference either, "
               + "because code-level references are not tracked as graph edges yet, only Unity "
               + "YAML references are - treat a result as \"worth checking\", not \"confirmed "
               + "unused\"). "
               + "A SEPARATE filter, fileType, answers asset_find's whole-file classification "
               + "question instead of the node filters above: \"Script\", \"Scene\", \"Prefab\", "
               + "\"Material\", \"AnimatorController\", \"ScriptableObject\", or \"Asset\" (anything "
               + "else indexed but not classified further) - one hit per FILE, exactly what "
               + "asset_find always returned, including for Scene/Prefab/ScriptableObject, which no "
               + "node-kind filter can classify (a scene or prefab is many per-object nodes with no "
               + "single summarising one; a ScriptableObject instance shares MonoBehaviour's generic "
               + "kind with an ordinary component). fileType combines only with pathPrefix - refused "
               + "together with kind/kindPattern/namePattern/edgeKind, which filter graph NODES, a "
               + "different question. "
               + "kindPattern is 'kind', but a case-insensitive SUBSTRING instead of an exact match - "
               + "reproduces component_find's builtin-kind branch (e.g. \"Collider\" matches "
               + "\"BoxCollider\"/\"SphereCollider\"/\"CapsuleCollider\" alike), which exact 'kind' "
               + "cannot express. Refused together with 'kind' (same axis, pick one). "
               + "This is a fixed set of scalar filters, not a query language - there is no way to "
               + "pass raw SQL through any of them, by construction." + ToolSupport.SavedStateClause)]
    public GraphQueryResult GraphQuery(
        [Description("Exact node kind, e.g. \"Class\", \"MonoBehaviour\", \"GameObject\", \"Rigidbody\", \"Material\", \"AnimatorController\". Refused together with 'kindPattern'.")] string? kind = null,
        [Description("Substring to match against the node name, case-insensitive")] string? namePattern = null,
        [Description("Project-relative path prefix, e.g. \"Assets/Scripts\". Combines with 'fileType' too.")] string? pathPrefix = null,
        [Description("Restrict to nodes with at least one edge of this kind: \"references\", \"instance_of\", or \"corresponds_to\"")] string? edgeKind = null,
        [Description("\"outgoing\" (default) - this node is the edge's source; \"incoming\" - this node is the edge's target. Only meaningful together with edgeKind.")] string edgeDirection = "outgoing",
        [Description("Further restrict edgeKind to edges pointing at this project-relative asset path, e.g. a script. Requires edgeKind. A path nothing owns just matches nothing.")] string? edgeTargetPath = null,
        [Description("Further restrict edgeKind to edges whose target's own name matches this substring, case-insensitive. Requires edgeKind. Does NOT by itself check the target's own kind - a same-named non-script asset (Material, ScriptableObject, ...) reached by a matching edge matches too. Combine with edgeTargetKind to guarantee the target is actually a script.")] string? edgeTargetNamePattern = null,
        [Description("Further restrict edgeKind to edges whose target's own node kind matches EXACTLY, e.g. \"Class\". Requires edgeKind. Combine with edgeTargetKind: \"Class\" and edgeTargetNamePattern to restore find_components_using_pattern's/component_find's old guarantee that a name match resolved to an actual script, not a coincidentally-same-named asset of some other kind. Usable alone too. Omit to match a target of any kind.")] string? edgeTargetKind = null,
        [Description("True to require NO matching edge instead of at least one (finds orphans). Requires edgeKind. Default false.")] bool edgeAbsent = false,
        [Description("Whole-FILE classification by extension, from file identity rather than graph nodes - reproduces asset_find: \"Script\", \"Scene\", \"Prefab\", \"Material\", \"AnimatorController\", \"ScriptableObject\", or \"Asset\". One hit per file. Combines only with 'pathPrefix'; refused together with 'kind', 'kindPattern', 'namePattern', or 'edgeKind'.")] string? fileType = null,
        [Description("Substring to match against the node kind, case-insensitive - e.g. \"Collider\" matches \"BoxCollider\"/\"SphereCollider\"/\"CapsuleCollider\". Reproduces component_find's builtin-kind branch. Refused together with 'kind'.")] string? kindPattern = null,
        [Description("Maximum nodes to return (1-500, default 100)")] int limit = 100,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null)
    {
        // Clamped to graph_query's own documented maximum BEFORE either branch below adds its "+1"
        // sentinel - see InspectTool.FindUnsetReferences' identical clampedLimit pattern. Without
        // this, a caller-supplied limit above 500 skips the clamp entirely, and 'truncated' - built
        // from the UNCLAMPED limit - can go right on reporting false while real matches beyond the
        // documented max are silently cut (or, in the fileType branch, delivered utterly unbounded:
        // FindAssetsByFileState's own ceiling is a 20,000-row constant unrelated to graph_query's
        // documented range).
        var clampedLimit = Math.Clamp(limit, 1, 500);

        if (!string.IsNullOrWhiteSpace(kind) && !string.IsNullOrWhiteSpace(kindPattern))
        {
            throw new McpException(
                "graph_query's 'kind' (exact match) and 'kindPattern' (substring match) both filter "
                + "the same thing - a node's own kind - so only one may be given at a time. Use "
                + "'kind' when you know the exact kind, 'kindPattern' for a fuzzy search across "
                + "related kinds (e.g. \"Collider\").");
        }

        if (!string.IsNullOrWhiteSpace(fileType))
        {
            if (Array.IndexOf(AssetType.KnownTypes, fileType) < 0)
            {
                throw new McpException(
                    $"'{fileType}' is not a recognised graph_query 'fileType' - use one of: "
                    + $"{string.Join(", ", AssetType.KnownTypes)}.");
            }

            if (!string.IsNullOrWhiteSpace(kind) || !string.IsNullOrWhiteSpace(kindPattern)
                || !string.IsNullOrWhiteSpace(namePattern) || !string.IsNullOrWhiteSpace(edgeKind)
                || !string.IsNullOrWhiteSpace(edgeTargetPath) || !string.IsNullOrWhiteSpace(edgeTargetNamePattern)
                || !string.IsNullOrWhiteSpace(edgeTargetKind) || edgeAbsent)
            {
                throw new McpException(
                    "graph_query's 'fileType' answers a whole-FILE identity question (from file "
                    + "identity, not graph nodes), so it cannot combine with 'kind', 'kindPattern', "
                    + "'namePattern', 'edgeKind', 'edgeTargetPath', 'edgeTargetNamePattern', or "
                    + "'edgeTargetKind' - those filter NODES, a different question. Call again with "
                    + "just 'fileType' and, optionally, 'pathPrefix'.");
            }

            var fileTypeProject = ToolSupport.ResolveProject(projects, project);
            var assets = projects.FindAssetsByFileState(fileTypeProject, fileType, pathPrefix, clampedLimit + 1);
            return BuildFileTypeResult(assets, clampedLimit);
        }

        if (string.IsNullOrWhiteSpace(kindPattern))
        {
            // kindPattern alone already satisfies "at least one filter" - only reach the shared
            // (query_graph-shared, byte-for-byte-preserved) check when it is absent too.
            ValidateFilters(kind, namePattern, pathPrefix, edgeKind, "graph_query");
        }
        ValidateEdgeDirection(edgeDirection);

        if (string.IsNullOrEmpty(edgeKind)
            && (!string.IsNullOrEmpty(edgeTargetPath) || !string.IsNullOrEmpty(edgeTargetNamePattern)
                || !string.IsNullOrEmpty(edgeTargetKind) || edgeAbsent))
        {
            throw new McpException(
                "graph_query's 'edgeTargetPath', 'edgeTargetNamePattern', 'edgeTargetKind', and "
                + "'edgeAbsent' only narrow an edge-existence check, so they need 'edgeKind' too - "
                + "e.g. {\"edgeKind\": \"references\", \"edgeTargetPath\": \"Assets/Scripts/Health.cs\"}. "
                + "Add 'edgeKind' and call again.");
        }

        var productGuid = ToolSupport.ResolveProject(projects, project);

        var found = projects.QueryGraph(productGuid, kind, namePattern, pathPrefix, edgeKind,
            edgeDirection.ToLowerInvariant(), clampedLimit + 1, edgeTargetPath, edgeTargetNamePattern, edgeAbsent,
            kindPattern, edgeTargetKind);

        return BuildResult(found, clampedLimit);
    }

    static void ValidateFilters(string? kind, string? namePattern, string? pathPrefix, string? edgeKind, string toolName)
    {
        if (string.IsNullOrWhiteSpace(kind) && string.IsNullOrWhiteSpace(namePattern)
            && string.IsNullOrWhiteSpace(pathPrefix) && string.IsNullOrWhiteSpace(edgeKind))
        {
            throw new McpException(
                $"{toolName} needs at least one filter - 'kind', 'namePattern', 'pathPrefix', or "
                + "'edgeKind' - or it would have to return the entire graph. Add one and call "
                + "again, e.g. {\"kind\": \"MonoBehaviour\"} or {\"namePattern\": \"Health\"}.");
        }
    }

    static void ValidateEdgeDirection(string edgeDirection)
    {
        if (!ValidEdgeDirections.Contains(edgeDirection, StringComparer.OrdinalIgnoreCase))
        {
            throw new McpException(
                $"'{edgeDirection}' is not a recognised 'edgeDirection' - use \"outgoing\" or "
                + "\"incoming\".");
        }
    }

    static GraphQueryResult BuildResult(IReadOnlyList<GraphNode> found, int limit)
    {
        var truncated = found.Count > limit;

        var hits = found.Take(limit).Select(n => new GraphQueryHit
        {
            Kind = n.Kind,
            Name = n.Name,
            Path = n.Path,
            Namespace = n.Namespace,
            Line = n.Line,
            FileId = n.FileId,
        }).ToList();

        return new GraphQueryResult { Results = hits, Truncated = truncated, TotalReturned = hits.Count };
    }

    /// <summary>
    /// Maps <see cref="ProjectService.FindAssetsByFileState"/>'s file-identity hits into the SAME
    /// <see cref="GraphQueryResult"/>/<see cref="GraphQueryHit"/> shape <see cref="BuildResult"/>
    /// returns for a node-based query, so a caller handles both branches of graph_query uniformly.
    /// A file hit has no per-object Unity fileID and no single line to report, so <c>FileId</c> and
    /// <c>Line</c> both take 0 - the same "not applicable" convention a script's own node already
    /// uses for FileId (see <see cref="GraphNode.FileId"/>'s own doc comment: "0 for script nodes,
    /// which have no fileID"). <c>Kind</c> is the resolved <see cref="Unity.AssetType"/> string
    /// (e.g. "Prefab") rather than a graph node kind - an honest reuse of the field for "what is
    /// this hit", exactly as truthful here as it is for a node hit.
    /// </summary>
    static GraphQueryResult BuildFileTypeResult(IReadOnlyList<AssetMatch> found, int limit)
    {
        var truncated = found.Count > limit;

        var hits = found.Take(limit).Select(a => new GraphQueryHit
        {
            Kind = a.Type,
            Name = Path.GetFileName(a.Path),
            Path = a.Path,
            Namespace = null,
            Line = 0,
            FileId = 0,
        }).ToList();

        return new GraphQueryResult { Results = hits, Truncated = truncated, TotalReturned = hits.Count };
    }
}
