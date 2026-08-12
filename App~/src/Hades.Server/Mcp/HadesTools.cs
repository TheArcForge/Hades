using System.ComponentModel;
using System.Text.Json.Serialization;
using Hades.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Hades.Server.Mcp;

public sealed record KnownProject
{
    [JsonPropertyName("project")] public required string Project { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
}

public sealed record SearchHit
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("namespace")] public string? Namespace { get; init; }
    [JsonPropertyName("line")] public required int Line { get; init; }
}

public sealed record SearchResult
{
    [JsonPropertyName("results")] public required IReadOnlyList<SearchHit> Results { get; init; }
    [JsonPropertyName("truncated")] public required bool Truncated { get; init; }
    [JsonPropertyName("totalReturned")] public required int TotalReturned { get; init; }
}

public sealed record ReferencingFileHit
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("references")] public required int References { get; init; }
    [JsonPropertyName("relationships")] public required IReadOnlyList<string> Relationships { get; init; }
    [JsonPropertyName("sampleVia")] public required string SampleVia { get; init; }
}

public sealed record ReferencesResult
{
    [JsonPropertyName("asset")] public required string Asset { get; init; }

    /// <summary>Individual references across every file — usually much larger than the file count.</summary>
    [JsonPropertyName("totalReferences")] public required int TotalReferences { get; init; }

    /// <summary>Distinct files, which is what "how widely is this used" actually means.</summary>
    [JsonPropertyName("referencingFiles")] public required int ReferencingFiles { get; init; }

    [JsonPropertyName("truncated")] public required bool Truncated { get; init; }
    [JsonPropertyName("files")] public required IReadOnlyList<ReferencingFileHit> Files { get; init; }
}

public sealed record StatusResult
{
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("knownProjects")] public required IReadOnlyList<KnownProject> KnownProjects { get; init; }
    [JsonPropertyName("defaultProject")] public string? DefaultProject { get; init; }
}

/// <summary>
/// The tool surface. Static and complete: the spec forbids the tool list varying per connection,
/// and Hades serves every project from one endpoint, so "is an Editor attached" can never gate
/// what appears here. Availability is reported as a tool-execution error instead.
///
/// Project routing uses the spec's explicit-handle pattern rather than MCP roots. Roots are
/// deprecated as of revision 2026-07-28 (SEP-2577) and the SDK marks the API obsolete, so
/// building routing on them would mean suppressing that warning to depend on a feature with an
/// announced expiry. Instead, per the spec's "Stateful Tools" guidance — "return an explicit
/// handle from a creation tool and accept that handle as an argument on subsequent calls" —
/// <see cref="Status"/> reports the handles and every other tool accepts one.
/// </summary>
[McpServerToolType]
public sealed class HadesTools(ProjectService projects)
{
    public const string ServerVersion = "2.0.0-dev";

    [McpServerTool(Name = "hades_status", Title = "Hades Status", ReadOnly = true, UseStructuredContent = true)]
    [Description("Hades server state and the list of projects it knows, each with the 'project' "
               + "handle to pass to other tools. Call this first, or whenever a tool reports that "
               + "the project is ambiguous or unknown.")]
    public StatusResult Status()
    {
        var known = projects.KnownProjects();

        return new StatusResult
        {
            Version = ServerVersion,
            KnownProjects = known.Select(p => new KnownProject
            {
                Project = p.ProductGuid,
                Name = p.Name,
                Path = p.Path,
            }).ToList(),
            DefaultProject = known.Count == 1 ? known[0].ProductGuid : null,
        };
    }

    [McpServerTool(Name = "search_by_name", Title = "Search by Name", ReadOnly = true, UseStructuredContent = true)]
    [Description("Find C# types in the project graph by name (case-insensitive substring). Use "
               + "this instead of grep: it is indexed and understands the project structure."
               + ToolSupport.SavedStateClause)]
    public SearchResult SearchByName(
        [Description("Substring to match, case-insensitive")] string namePattern,
        [Description("Optional declaration-kind filter: Class, Struct, Interface, Enum, Record")] string? kind = null,
        [Description("Maximum results to return (1-200, default 50)")] int limit = 50,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(namePattern))
        {
            throw new McpException(
                "search_by_name needs a non-empty 'namePattern' — the substring to look for, "
                + "e.g. {\"namePattern\": \"PlayerController\"}. Add it and call again.");
        }

        var productGuid = ToolSupport.ResolveProject(projects, project);

        // Clamped to this tool's own documented maximum BEFORE the "+1" below - see
        // InspectTool.FindUnsetReferences' identical clampedLimit pattern. Without this, a
        // caller-supplied limit above 200 skips the clamp entirely: the raw limit + 1 is what
        // reaches the database's own shared ceiling (GraphDatabase.MaxSearchFetch), and
        // 'truncated' below - computed against the UNCLAMPED limit - can go right on reporting
        // false while thousands of real matches are silently cut.
        var clampedLimit = Math.Clamp(limit, 1, 200);

        // One more than the cap, so truncation is reported honestly rather than the agent
        // silently believing it has seen everything.
        var found = projects.Search(productGuid, namePattern, kind, clampedLimit + 1);
        var truncated = found.Count > clampedLimit;

        var hits = found.Take(clampedLimit).Select(node => new SearchHit
        {
            Name = node.Name,
            Kind = node.Kind,
            Path = node.Path,
            Namespace = node.Namespace,
            Line = node.Line,
        }).ToList();

        return new SearchResult { Results = hits, Truncated = truncated, TotalReturned = hits.Count };
    }

    [McpServerTool(Name = "find_references_to", Title = "Find References To", ReadOnly = true, UseStructuredContent = true)]
    [Description("Find everything that references a Unity asset or C# script — which scenes "
               + "instantiate a prefab, which prefabs use a script, what would break if it were "
               + "removed. Results are grouped by file, most-used first, with a count per file. "
               + "Takes the project-relative path exactly as search_by_name returns it, "
               + "e.g. \"Assets/Scripts/PlayerController.cs\"." + ToolSupport.SavedStateClause)]
    public ReferencesResult FindReferencesTo(
        [Description("Project-relative asset path, as returned by search_by_name")] string assetPath,
        [Description("Maximum FILES to return (1-500, default 100)")] int limit = 100,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            throw new McpException(
                "find_references_to needs an 'assetPath' — the project-relative path of the thing "
                + "to look up, e.g. {\"assetPath\": \"Assets/Scripts/PlayerController.cs\"}. "
                + "search_by_name returns paths in exactly this form.");
        }

        var productGuid = ToolSupport.ResolveProject(projects, project);

        var result = projects.FindReferencesTo(productGuid, assetPath, limit)
            ?? throw new McpException(
                $"'{assetPath}' is not in the graph, so nothing can be said about what references "
                + "it. Check the path with search_by_name — it must be project-relative "
                + "(\"Assets/...\" or \"Packages/...\"), not absolute. Note that an asset with "
                + "no .meta file cannot be referenced by anything and will not resolve.");

        return new ReferencesResult
        {
            Asset = result.AssetPath,
            TotalReferences = result.TotalReferences,
            ReferencingFiles = result.ReferencingFileCount,
            Truncated = result.Truncated,
            Files = result.Files.Select(f => new ReferencingFileHit
            {
                Path = f.Path,
                References = f.References,
                Relationships = f.Relationships,
                SampleVia = f.SampleVia,
            }).ToList(),
        };
    }

    [McpServerTool(Name = "get_project_summary", Title = "Project Summary", ReadOnly = true, UseStructuredContent = true)]
    [Description("Structured overview of a Unity project: node counts by kind, index freshness, "
               + "and where the project lives." + ToolSupport.SavedStateClause
               + " appliedDefines lists the C# preprocessor symbols indexing applied when "
               + "evaluating #if (UNITY_EDITOR, the Unity-version ladder from ProjectVersion.txt, "
               + "this project's own scriptingDefineSymbols, and every asmdef versionDefine whose "
               + "named package resolves to a satisfying version) - the SAME set project-wide, for "
               + "every file, which is an approximation: Unity's real compiler uses a DIFFERENT "
               + "set per assembly (asmdef), and Hades does not yet track file-to-asmdef "
               + "membership. Code gated on a symbol outside this list - a platform define, a "
               + "csc.rsp-only symbol, or a versionDefine keyed to a built-in Unity module rather "
               + "than an installed package - is not in the graph at all.")]
    public ProjectSummary GetProjectSummary(
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null)
    {
        var productGuid = ToolSupport.ResolveProject(projects, project);

        return projects.Summary(productGuid)
            ?? throw new McpException(
                $"Project {productGuid} is known but has no graph yet. It may still be indexing.");
    }
}
