using System.Text.Json.Serialization;
using Hades.Core;
using Hades.Core.Memory;
using Hades.Server.Mcp;
using ModelContextProtocol;

namespace Hades.Server.Control;

/// <summary>One authored document's listing entry - see <see cref="MemoryResult"/>.</summary>
public sealed record MemoryDocumentRow
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("sizeBytes")] public required long SizeBytes { get; init; }

    /// <summary>Plan 11 Task 7 audit fix: without this, a document-list view (a file browser column,
    /// exactly what spec #3 §3.4's Memory/Asphodel surface needs) had to convert
    /// <see cref="SizeBytes"/> to KB/MB itself - the same unit-selection computation FormatAge
    /// already avoids for durations elsewhere in this API, now avoided here too for byte counts. See
    /// <see cref="MemoryEndpoint.FormatSize"/>.</summary>
    [JsonPropertyName("sizeDisplay")] public required string SizeDisplay { get; init; }

    [JsonPropertyName("lastReviewed")] public string? LastReviewed { get; init; }
}

/// <summary>One pending (or past - the queue is never filtered by status here, so the shell can
/// show accepted/deferred history too) proposal - see <see cref="MemoryResult"/>.</summary>
public sealed record MemoryProposalRow
{
    [JsonPropertyName("fileName")] public required string FileName { get; init; }
    [JsonPropertyName("targetFile")] public required string TargetFile { get; init; }
    [JsonPropertyName("createdAtUtc")] public DateTimeOffset? CreatedAtUtc { get; init; }

    /// <summary>Plan 11 Task 7 audit fix: <see cref="CreatedAtUtc"/> alone forced a review-queue view
    /// (spec #3 §3.4 - exactly where users triage by age, the same way a PR list shows "opened 3
    /// days ago") to subtract a raw timestamp from "now" itself. Null exactly when
    /// <see cref="CreatedAtUtc"/> is - see <see cref="MemoryEndpoint.FormatRelativeAge"/>.</summary>
    [JsonPropertyName("createdAgo")] public string? CreatedAgo { get; init; }

    [JsonPropertyName("rationale")] public required string Rationale { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("content")] public required string Content { get; init; }
}

/// <summary>The full <c>GET /control/memory</c> response - documents AND the proposal queue
/// together, since spec #3 §3.4 is one shell view (Memory/Asphodel) showing both at once; a shell
/// rendering that view must never need a second round trip just to draw itself.</summary>
public sealed record MemoryResult
{
    [JsonPropertyName("documents")] public required IReadOnlyList<MemoryDocumentRow> Documents { get; init; }
    [JsonPropertyName("proposals")] public required IReadOnlyList<MemoryProposalRow> Proposals { get; init; }
}

/// <summary>The full <c>GET /control/memory/document</c> response - one document's complete raw
/// text (frontmatter and all), exactly as <see cref="MemoryFile.RawText"/> holds it, so the shell's
/// own editor round-trips it losslessly on save.</summary>
public sealed record MemoryDocumentResult
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("content")] public required string Content { get; init; }
}

/// <summary>Body of <c>POST /control/memory/document</c>.</summary>
public sealed record WriteMemoryDocumentRequest
{
    [JsonPropertyName("content")] public required string Content { get; init; }
}

/// <summary>
/// The Memory surface (spec #3 §3.4): list documents, read one, write one directly (the shell's own
/// text-editor save path - a HUMAN editing memory, exactly what the whole memory design anticipates
/// and requires), and the proposal queue with Accept / Dismiss / Defer.
///
/// <b>Filenames are validated as basenames on every write path.</b> Every action here reaches the
/// filesystem exclusively through <see cref="ProjectService"/>'s own memory methods
/// (<see cref="ProjectService.ReadMemoryDocument"/>, <see cref="ProjectService.WriteMemoryDocument"/>,
/// <see cref="ProjectService.ReadMemoryProposal"/>, <see cref="ProjectService.SetMemoryProposalStatus"/>,
/// <see cref="ProjectService.DeleteMemoryProposal"/>), every one of which routes a caller-supplied
/// name through <see cref="Memory.MemoryStore.ValidatedChildPath"/> (directly, or via
/// <see cref="Memory.MemoryProposals"/>'s own use of it - see that method's own doc comment for why
/// it is <c>internal</c> rather than private). This closes the audit's "dashboard memory API does
/// FS writes/unlinks from URL path params": <see cref="TryRun"/> is the one place that ArgumentException
/// (thrown for "", whitespace, "..", a rooted path, or anything containing a path separator) is
/// caught and turned into a resolved 400 - there is no path here that reaches the filesystem without
/// going through that validation first, including <see cref="AcceptProposal"/>'s write to a proposal's
/// OWN <c>target_file</c> (frontmatter-authored, and only weakly checked - no path separator - at
/// proposal-creation time by <see cref="Memory.MemoryProposals.Write"/>'s own <c>ValidateTargetFile</c>) -
/// see that method's own body for why its write is inside the SAME try/catch as its read.
///
/// <b>Accepting a proposal is the only path that writes an authored document, and nothing deletes
/// without an explicit confirm flag.</b> Of the three proposal-queue actions, only
/// <see cref="AcceptProposal"/> ever writes to memory/*.md (Dismiss discards a proposal; Defer only
/// changes its own status) - see that method's own doc comment for the exact, deliberately
/// non-destructive merge it performs. <see cref="DismissProposal"/> is the only action anywhere in
/// this class that deletes anything, and only when its caller passes <c>confirm=true</c> explicitly;
/// without it, this refuses with a 400 naming exactly what to do, rather than silently no-op'ing or
/// (worse) deleting anyway. This is precisely the property the old package's memory-auto-deletion
/// defect (proposals silently deleted on Editor start) violated - memory is authored and
/// irreplaceable, and Plan 6's whole design rests on that never happening again.
///
/// <b>Design decision this task made that the plan did not specify: what "accept" actually does to
/// the target document's text.</b> <see cref="MemoryTools.ProposeMemoryUpdate"/>'s own description
/// says a proposal is "to be reviewed and (if accepted) merged into targetFile by a human" - it does
/// not say HOW. This endpoint appends the proposal's content to the end of the existing document
/// (creating it fresh if it does not exist yet), never overwrites: an automatic overwrite could
/// silently discard prior authored text, which is the one failure mode "irreplaceable" cannot
/// tolerate even once. A human still reviews the result afterward (via <c>write one</c>, this same
/// class's <see cref="WriteDocument"/>) exactly as they would review and clean up any other merge.
/// </summary>
public static class MemoryEndpoint
{
    // ------------------------------------------------------------------------------------- GET

    public static IResult Get(ProjectService projects, string? project, Func<DateTimeOffset> utcNow)
    {
        if (!TryResolveProject(projects, project, out var productGuid, out var error)) return error!;

        var summary = projects.GetMemorySummary(productGuid) ?? new MemorySummary { HasMemory = false, Documents = [] };
        var proposals = projects.ListMemoryProposals(productGuid);
        var now = utcNow();

        return Results.Json(new MemoryResult
        {
            Documents = summary.Documents.Select(d => new MemoryDocumentRow
            {
                Name = d.Name,
                SizeBytes = d.SizeBytes,
                SizeDisplay = FormatSize(d.SizeBytes),
                LastReviewed = d.LastReviewed,
            }).ToList(),
            Proposals = proposals.Select(p => ToProposalRow(p, now)).ToList(),
        });
    }

    public static IResult GetDocument(ProjectService projects, string? project, string? name) =>
        WithResolvedProject(projects, project, productGuid => TryRun(() =>
        {
            var file = projects.ReadMemoryDocument(productGuid, name!);
            if (file is null)
            {
                return Results.Json(new { error = $"'{name}' does not exist yet." }, statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(new MemoryDocumentResult { Name = name!, Content = file.RawText });
        }));

    // --------------------------------------------------------------------------------- write one

    public static IResult WriteDocument(ProjectService projects, string? project, string? name, WriteMemoryDocumentRequest request) =>
        WithResolvedProject(projects, project, productGuid => TryRun(() =>
        {
            projects.WriteMemoryDocument(productGuid, name!, request.Content);
            return Results.Json(new ActionResult { Success = true, Message = $"Saved {name}." });
        }));

    // --------------------------------------------------------------------------------- accept

    /// <summary>Appends the proposal's content into its own <c>target_file</c> (creating it if it
    /// does not exist), marks the proposal itself "accepted", and NEVER deletes the proposal file -
    /// see this class's own doc comment. The target-document read AND write are inside the SAME
    /// <see cref="TryRun"/> as the proposal read, so an unsafe <c>target_file</c> (see this class's
    /// own doc comment on why that is possible despite Task 6's own basename discipline) is caught
    /// and reported exactly like an unsafe <paramref name="fileName"/> would be, never left to
    /// throw past this method as an unhandled 500.</summary>
    public static IResult AcceptProposal(ProjectService projects, string? project, string? fileName) =>
        WithResolvedProject(projects, project, productGuid => TryRun(() =>
        {
            var proposal = projects.ReadMemoryProposal(productGuid, fileName!);
            if (proposal is null)
            {
                return Results.Json(new { error = $"Unknown proposal '{fileName}'." }, statusCode: StatusCodes.Status404NotFound);
            }

            var existing = projects.ReadMemoryDocument(productGuid, proposal.TargetFile);
            var merged = string.IsNullOrEmpty(existing?.RawText)
                ? proposal.Content
                : existing.RawText.TrimEnd() + "\n\n" + proposal.Content.TrimStart();

            projects.WriteMemoryDocument(productGuid, proposal.TargetFile, merged);
            projects.SetMemoryProposalStatus(productGuid, fileName!, "accepted");

            return Results.Json(new ActionResult
            {
                Success = true,
                Message = $"Accepted — merged into {proposal.TargetFile}.",
            });
        }));

    // -------------------------------------------------------------------------------- dismiss

    /// <summary>Deletes the proposal file - requires <paramref name="confirm"/> to be explicitly
    /// true; refuses with a 400 naming the requirement otherwise, never silently no-ops and never
    /// deletes anyway. See this class's own doc comment.</summary>
    public static IResult DismissProposal(ProjectService projects, string? project, string? fileName, bool confirm) =>
        WithResolvedProject(projects, project, productGuid => TryRun(() =>
        {
            if (!confirm)
            {
                return Results.Json(
                    new { error = "Dismissing a proposal deletes it. Pass confirm=true to proceed." },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (projects.ReadMemoryProposal(productGuid, fileName!) is null)
            {
                return Results.Json(new { error = $"Unknown proposal '{fileName}'." }, statusCode: StatusCodes.Status404NotFound);
            }

            projects.DeleteMemoryProposal(productGuid, fileName!);
            return Results.Json(new ActionResult { Success = true, Message = "Proposal dismissed." });
        }));

    // ---------------------------------------------------------------------------------- defer

    /// <summary>Marks the proposal "deferred" - pure bookkeeping: never deletes it, never writes an
    /// authored document. See this class's own doc comment.</summary>
    public static IResult DeferProposal(ProjectService projects, string? project, string? fileName) =>
        WithResolvedProject(projects, project, productGuid => TryRun(() =>
        {
            if (!projects.SetMemoryProposalStatus(productGuid, fileName!, "deferred"))
            {
                return Results.Json(new { error = $"Unknown proposal '{fileName}'." }, statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(new ActionResult { Success = true, Message = "Proposal deferred." });
        }));

    // ------------------------------------------------------------------------------------ helpers

    static MemoryProposalRow ToProposalRow(MemoryProposalInfo p, DateTimeOffset now) => new()
    {
        FileName = p.FileName,
        TargetFile = p.TargetFile,
        CreatedAtUtc = p.CreatedAt,
        CreatedAgo = p.CreatedAt is { } createdAt ? FormatRelativeAge(now - createdAt) : null,
        Rationale = p.Rationale,
        Status = p.Status,
        Content = p.Content,
    };

    /// <summary>"500 B" under 1 KB, "N.N KB" under 1 MB, "N.N MB" beyond - see
    /// <see cref="MemoryDocumentRow.SizeDisplay"/>'s own doc comment.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    /// <summary>"12s ago" under a minute, "Nm ago" under an hour, "Nh ago" under a day, "Nd ago"
    /// beyond - see <see cref="MemoryProposalRow.CreatedAgo"/>'s own doc comment. More tiers than
    /// SummaryEndpoint/ProjectsEndpoint's own private FormatAge (seconds/minutes only, never shared -
    /// see those methods' own doc comments): a reload lease is held for seconds, but a proposal can
    /// sit unreviewed for days, so reusing a seconds/minutes-only formatter here would print a real
    /// "4320m ago" rather than a hypothetical one.</summary>
    public static string FormatRelativeAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalMinutes < 1) return $"{age.TotalSeconds:F0}s ago";
        if (age.TotalHours < 1) return $"{age.TotalMinutes:F0}m ago";
        if (age.TotalDays < 1) return $"{age.TotalHours:F0}h ago";
        return $"{age.TotalDays:F0}d ago";
    }

    static bool TryResolveProject(ProjectService projects, string? project, out string productGuid, out IResult? error)
    {
        try
        {
            productGuid = ToolSupport.ResolveProject(projects, project);
            error = null;
            return true;
        }
        catch (McpException ex)
        {
            productGuid = "";
            error = Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            return false;
        }
    }

    static IResult WithResolvedProject(ProjectService projects, string? project, Func<string, IResult> action)
    {
        if (!TryResolveProject(projects, project, out var productGuid, out var error)) return error!;
        return action(productGuid);
    }

    /// <summary>Runs <paramref name="action"/>, turning an <see cref="ArgumentException"/> from an
    /// unsafe basename (see <see cref="Memory.MemoryStore.ValidatedChildPath"/>) into a resolved
    /// 400 - the one place in this class that translation happens, so every action above gets it
    /// uniformly rather than repeating a try/catch five times.</summary>
    static IResult TryRun(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (ArgumentException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
