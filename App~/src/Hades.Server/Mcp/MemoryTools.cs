using System.ComponentModel;
using System.Text.Json.Serialization;
using Hades.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Hades.Server.Mcp;

public sealed record MemoryDocumentHit
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("sizeBytes")] public required long SizeBytes { get; init; }
    [JsonPropertyName("lastReviewed")] public string? LastReviewed { get; init; }
}

public sealed record MemorySummaryResult
{
    [JsonPropertyName("hasMemory")] public required bool HasMemory { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("documents")] public required IReadOnlyList<MemoryDocumentHit> Documents { get; init; }
}

public sealed record MemoryRecallHit
{
    [JsonPropertyName("document")] public required string Document { get; init; }
    [JsonPropertyName("excerpt")] public required string Excerpt { get; init; }
    [JsonPropertyName("score")] public required double Score { get; init; }
}

public sealed record MemoryRecallResult
{
    [JsonPropertyName("results")] public required IReadOnlyList<MemoryRecallHit> Results { get; init; }
    [JsonPropertyName("truncated")] public required bool Truncated { get; init; }
    [JsonPropertyName("totalReturned")] public required int TotalReturned { get; init; }
}

public sealed record MemoryProposalResult
{
    [JsonPropertyName("fileName")] public required string FileName { get; init; }
}

public sealed record MemoryValidationHit
{
    [JsonPropertyName("document")] public required string Document { get; init; }
    [JsonPropertyName("scriptPath")] public required string ScriptPath { get; init; }
}

public sealed record MemoryValidationResult
{
    [JsonPropertyName("results")] public required IReadOnlyList<MemoryValidationHit> Results { get; init; }
    [JsonPropertyName("truncated")] public required bool Truncated { get; init; }
    [JsonPropertyName("totalReturned")] public required int TotalReturned { get; init; }
}

/// <summary>
/// Memory tools: get_memory_summary, recall_memory, propose_memory_update, validate_memory. Same
/// conventions as HadesTools/GraphTools throughout - see HadesTools' class doc comment for why
/// project routing uses an explicit handle rather than MCP roots.
///
/// The design boundary every one of these four respects: memory/*.md (the top-level AUTHORED
/// documents) is what a human wrote and only a human edits; memory/proposals/ is where an agent's
/// suggestions live until a human accepts one by hand. get_memory_summary, recall_memory, and
/// validate_memory only ever look at the authored side - a caller citing recall_memory as
/// "guidance" must never be handed an unaccepted proposal. propose_memory_update is the only
/// writer of the four, and it is structurally confined to proposals/ - see
/// <see cref="Hades.Core.Memory.MemoryProposals"/>.
/// </summary>
[McpServerToolType]
public sealed class MemoryTools(ProjectService projects)
{
    [McpServerTool(Name = "get_memory_summary", Title = "Memory Summary", ReadOnly = true, UseStructuredContent = true)]
    [Description("Overview of a project's authored memory: every top-level document under memory/ "
               + "(conventions, decisions, glossary, intent, patterns, pitfalls, or any custom "
               + "name a human created) with its size and last-reviewed date. Explicitly reports "
               + "when a project has no memory recorded yet, rather than an empty list a caller "
               + "would have to interpret. Does not include memory/proposals/ - pending, "
               + "unaccepted suggestions are not part of this overview.")]
    public async Task<MemorySummaryResult> GetMemorySummary(
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
    {
        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);

        var summary = projects.GetMemorySummary(productGuid)
            ?? throw new McpException($"Project {productGuid} is known but has no memory store yet.");

        return new MemorySummaryResult
        {
            HasMemory = summary.HasMemory,
            Message = summary.HasMemory
                ? $"{summary.Documents.Count} memory document(s) recorded."
                : "Nothing recorded yet for this project.",
            Documents = summary.Documents.Select(d => new MemoryDocumentHit
            {
                Name = d.Name,
                SizeBytes = d.SizeBytes,
                LastReviewed = d.LastReviewed,
            }).ToList(),
        };
    }

    [McpServerTool(Name = "recall_memory", Title = "Recall Memory", ReadOnly = true, UseStructuredContent = true)]
    [Description("Full-text search over a project's authored memory documents, ranked by "
               + "relevance - a document matching more of the query outranks one matching less. "
               + "Each hit names its source document, so a caller can cite where guidance came "
               + "from. The query is matched as literal words, never as a query language - special "
               + "characters and words like OR/NEAR/AND/* have no special meaning here. Searches "
               + "only top-level authored documents, never memory/proposals/.")]
    public async Task<MemoryRecallResult> RecallMemory(
        [Description("Free-text search query, e.g. \"render pipeline\" - matched as literal words")] string query,
        [Description("Maximum excerpts to return (1-50, default 10)")] int limit = 10,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new McpException(
                "recall_memory needs a non-empty 'query' — the text to search for, e.g. "
                + "{\"query\": \"render pipeline\"}. Add it and call again.");
        }

        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);

        var hits = projects.RecallMemory(productGuid, query, limit + 1);
        var truncated = hits.Count > limit;
        var results = hits.Take(limit)
            .Select(h => new MemoryRecallHit { Document = h.Name, Excerpt = h.Excerpt, Score = h.Score })
            .ToList();

        return new MemoryRecallResult { Results = results, Truncated = truncated, TotalReturned = results.Count };
    }

    [McpServerTool(Name = "propose_memory_update", Title = "Propose Memory Update", ReadOnly = false, UseStructuredContent = true)]
    [Description("Proposes a new or updated piece of project memory for human review. Writes ONLY "
               + "to memory/proposals/ - never to an authored document (conventions.md, "
               + "decisions.md, etc.). Agents propose; only a human editing the authored document "
               + "directly accepts a proposal into it. Call get_memory_summary first to see what "
               + "authored documents already exist.")]
    public async Task<MemoryProposalResult> ProposeMemoryUpdate(
        [Description("Plain basename of the authored document this proposal is about, e.g. \"patterns.md\" or \"patterns\" — not a path, and not empty.")] string targetFile,
        [Description("The proposed markdown text, to be reviewed and (if accepted) merged into targetFile by a human.")] string content,
        [Description("Why this is being proposed - evidence, reasoning, or context for the human reviewer.")] string? rationale = null,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
    {
        if (string.IsNullOrWhiteSpace(targetFile))
        {
            throw new McpException(
                "propose_memory_update needs a non-empty 'targetFile' — the authored document "
                + "this proposal is about, e.g. {\"targetFile\": \"patterns.md\"}. Add it and call "
                + "again.");
        }

        if (targetFile.Contains('/') || targetFile.Contains('\\'))
        {
            throw new McpException(
                $"'{targetFile}' is not a valid 'targetFile' — it must be a plain document name, "
                + "not a path, e.g. \"patterns.md\" rather than \"memory/patterns.md\".");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new McpException(
                "propose_memory_update needs non-empty 'content' — the markdown text being "
                + "proposed. Add it and call again.");
        }

        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);

        var result = projects.ProposeMemoryUpdate(productGuid, targetFile, content, rationale ?? "")
            ?? throw new McpException($"Project {productGuid} is known but could not accept a proposal.");

        return new MemoryProposalResult { FileName = result.FileName };
    }

    [McpServerTool(Name = "validate_memory", Title = "Validate Memory", ReadOnly = true, UseStructuredContent = true)]
    [Description("Cross-checks authored memory against the live project graph: reports every "
               + "backtick-quoted script path mentioned in a memory document (e.g. "
               + "`Assets/Scripts/Foo.cs`) that no longer exists in the graph. Read-only - it "
               + "reports drift, it never edits a memory document; only a human fixes what this "
               + "finds. Only explicit, backtick-quoted project-relative .cs paths are recognised, "
               + "not bare class names or prose mentions." + ToolSupport.SavedStateClause)]
    public async Task<MemoryValidationResult> ValidateMemory(
        [Description("Maximum findings to return (1-500, default 100)")] int limit = 100,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
    {
        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);

        var found = projects.ValidateMemory(productGuid, limit + 1);
        var truncated = found.Count > limit;
        var hits = found.Take(limit)
            .Select(f => new MemoryValidationHit { Document = f.Document, ScriptPath = f.ScriptPath })
            .ToList();

        return new MemoryValidationResult { Results = hits, Truncated = truncated, TotalReturned = hits.Count };
    }
}
