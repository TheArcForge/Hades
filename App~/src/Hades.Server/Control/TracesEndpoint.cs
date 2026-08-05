using System.Text.Json;
using System.Text.Json.Serialization;
using Hades.Core;
using Hades.Core.Tracing;
using Hades.Server.Mcp;
using ModelContextProtocol;

namespace Hades.Server.Control;

/// <summary>A trace or sequence's resolved outcome - decided here, never something the shell
/// infers from a raw "ok"/"error" status string. Same rule as <see cref="ControlSeverity"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TraceOutcome
{
    [JsonStringEnumMemberName("ok")] Ok,
    [JsonStringEnumMemberName("error")] Error,
}

/// <summary>One grouped sequence - see <see cref="TracesEndpoint"/>'s own class doc comment for
/// exactly what makes a sequence. <see cref="Pattern"/> is the complete, human-readable, already
/// arrow-joined tool sequence to print verbatim - the same "resolved string, not raw fields for the
/// shell to format" rule every other Control response follows.</summary>
public sealed record TraceSequenceRow
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("tools")] public required IReadOnlyList<string> Tools { get; init; }
    [JsonPropertyName("pattern")] public required string Pattern { get; init; }
    [JsonPropertyName("callCount")] public required int CallCount { get; init; }
    [JsonPropertyName("startUtcMs")] public required long StartUtcMs { get; init; }
    [JsonPropertyName("endUtcMs")] public required long EndUtcMs { get; init; }
    [JsonPropertyName("durationMs")] public required long DurationMs { get; init; }
    [JsonPropertyName("outcome")] public required TraceOutcome Outcome { get; init; }
    [JsonPropertyName("traceIds")] public required IReadOnlyList<string> TraceIds { get; init; }
}

/// <summary>The full <c>GET /control/traces/sequences</c> response.</summary>
public sealed record TraceSequencesResult
{
    [JsonPropertyName("sequences")] public required IReadOnlyList<TraceSequenceRow> Sequences { get; init; }

    /// <summary>True when the underlying trace fetch hit its own limit - older sequences may exist
    /// beyond what is returned here. Same truncated/totalReturned idiom
    /// <see cref="Hades.Server.Mcp.MemoryRecallResult"/> already uses.</summary>
    [JsonPropertyName("truncated")] public required bool Truncated { get; init; }
}

/// <summary>One already-fetched trace, the input <see cref="TracesEndpoint.GroupIntoSequences"/>
/// groups - plain data so that method is testable with no live <see cref="TraceStore"/> at all,
/// same two-layer reasoning as every other Control endpoint's own snapshot type.</summary>
public sealed record TraceRecordSnapshot
{
    public required string TraceId { get; init; }
    public required string Tool { get; init; }
    public required long StartUtcMs { get; init; }
    public long? EndUtcMs { get; init; }
    public string? Status { get; init; }
}

/// <summary>One already-rendered <c>{key, valueDisplay}</c> pair, flattened out of a span's
/// <c>attributes</c>/<c>events</c> JSON tree - see <see cref="SpanRow"/>'s own doc comment for why
/// this exists instead of handing the shell raw nested JSON. <see cref="Key"/> is built purely from
/// the JSON's own structure (object keys dot-joined, array indices bracketed) - never invented text.
/// <see cref="ValueDisplay"/> is the exact text this leaf reads as: a JSON string's own decoded text
/// verbatim, or - for every other JSON scalar (number, bool, null) - the literal JSON token exactly
/// as written, never re-formatted, rounded, or given a locale. This is what closes the Plan 13 Task 5
/// gap: Swift's own (now-retired) <c>ControlJSONValue.stringLeaves()</c> could only ever surface a
/// <c>.string</c> leaf, so <c>resultSizeBytes</c>/<c>timeUtcMs</c>/every other numeric or boolean
/// value was silently invisible - stringifying a number client-side would be Swift deciding how it
/// reads, exactly what spec #3 §1 forbids. See <see cref="TracesEndpoint.FlattenJsonToDisplayRows"/>
/// for where this decision is made, once, server-side.</summary>
public sealed record SpanAttributeRow
{
    [JsonPropertyName("key")] public required string Key { get; init; }
    [JsonPropertyName("valueDisplay")] public required string ValueDisplay { get; init; }
}

/// <summary>One span, wire-shaped - see <see cref="TraceDetailResult"/>. <see cref="Attributes"/>/
/// <see cref="Events"/> are pre-rendered, flattened <see cref="SpanAttributeRow"/> lists (never the
/// raw nested JSON <see cref="Hades.Core.Tracing.SpanRecord"/> itself stores as a JSON-text column) -
/// see <see cref="SpanAttributeRow"/>'s own doc comment for exactly what "pre-rendered" means and why.
/// Null exactly when the underlying column is null (nothing recorded); an empty list is a
/// theoretically possible but practically unseen "recorded, but nothing in it" state - the two are
/// kept distinct rather than collapsed, matching every other optional-vs-empty distinction in this
/// API.</summary>
public sealed record SpanRow
{
    [JsonPropertyName("spanId")] public required string SpanId { get; init; }
    [JsonPropertyName("parentSpanId")] public string? ParentSpanId { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("startUtcMs")] public required long StartUtcMs { get; init; }
    [JsonPropertyName("endUtcMs")] public long? EndUtcMs { get; init; }

    /// <summary>Resolved here so a span-detail view (a waterfall/flamegraph - the natural rendering
    /// for nested spans) never has to subtract endUtcMs-startUtcMs itself to size a span's bar - the
    /// same reasoning TraceSequenceRow/TraceDetailResult already carry a resolved durationMs
    /// alongside their own raw timestamps for (Plan 11 Task 7's own no-logic audit). Null exactly
    /// when <see cref="EndUtcMs"/> is - a span with no recorded end has no duration to report,
    /// not a guessed one.</summary>
    [JsonPropertyName("durationMs")] public long? DurationMs { get; init; }

    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("attributes")] public IReadOnlyList<SpanAttributeRow>? Attributes { get; init; }
    [JsonPropertyName("events")] public IReadOnlyList<SpanAttributeRow>? Events { get; init; }
}

/// <summary>The full <c>GET /control/traces/{traceId}</c> response - one trace with every span it
/// owns.</summary>
public sealed record TraceDetailResult
{
    [JsonPropertyName("traceId")] public required string TraceId { get; init; }
    [JsonPropertyName("tool")] public required string Tool { get; init; }
    [JsonPropertyName("startUtcMs")] public required long StartUtcMs { get; init; }
    [JsonPropertyName("endUtcMs")] public long? EndUtcMs { get; init; }
    [JsonPropertyName("durationMs")] public long? DurationMs { get; init; }
    [JsonPropertyName("outcome")] public required TraceOutcome Outcome { get; init; }
    [JsonPropertyName("spans")] public required IReadOnlyList<SpanRow> Spans { get; init; }
}

public sealed record SlowToolRow
{
    [JsonPropertyName("tool")] public required string Tool { get; init; }
    [JsonPropertyName("callCount")] public required int CallCount { get; init; }
    [JsonPropertyName("averageDurationMs")] public required double AverageDurationMs { get; init; }
    [JsonPropertyName("maxDurationMs")] public required long MaxDurationMs { get; init; }
}

/// <summary>The full <c>GET /control/traces/slow</c> response.</summary>
public sealed record SlowToolsResult
{
    [JsonPropertyName("tools")] public required IReadOnlyList<SlowToolRow> Tools { get; init; }
}

/// <summary>One failed call - see <see cref="FailedCallsResult"/>. <see cref="Error"/> is the
/// triggering exception's own message, read back off the trace's root span (see
/// <see cref="TracesEndpoint.ExtractErrorMessage"/>) - held to the same "specific and actionable"
/// standard as every other failure surface in this API (Operations, ProjectsEndpoint's own action
/// results).</summary>
public sealed record FailedCallRow
{
    [JsonPropertyName("traceId")] public required string TraceId { get; init; }
    [JsonPropertyName("tool")] public required string Tool { get; init; }
    [JsonPropertyName("startUtcMs")] public required long StartUtcMs { get; init; }
    [JsonPropertyName("durationMs")] public long? DurationMs { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>The full <c>GET /control/traces/failures</c> response.</summary>
public sealed record FailedCallsResult
{
    [JsonPropertyName("failures")] public required IReadOnlyList<FailedCallRow> Failures { get; init; }
}

/// <summary>
/// The Traces surface (spec #3 §3.3), sequence-first: <c>GET /control/traces/sequences</c> returns
/// GROUPED sequences, never raw spans - doubly load-bearing per the plan, since the tool-
/// consolidation backlog was blocked on not knowing which call sequences actually occur, and "did
/// consolidation reduce round trips" is only answerable if sequences are legible. Also
/// <c>GET /control/traces/{traceId}</c> (span detail), <c>GET /control/traces/slow</c>, and
/// <c>GET /control/traces/failures</c> - all four map 1:1 onto <see cref="TraceStore"/>'s own
/// existing query methods (<see cref="TraceStore.RecentTraces"/>, <see cref="TraceStore.GetTrace"/>,
/// <see cref="TraceStore.SlowestTools"/>, <see cref="TraceStore.Failures"/> - the plan's own
/// explicit list), never a second read of the database.
///
/// <b>What makes a sequence.</b> A maximal run of one project's tool calls, ordered by start time,
/// where each call starts within <see cref="DefaultSequenceGapMs"/> (30 seconds) of the latest
/// call-end seen so far in the run - see <see cref="GroupIntoSequences"/>'s own doc comment for the
/// exact algorithm and <see cref="TracesGroupIntoSequencesTests"/> (test project) for why 30s: long
/// enough to bridge the ordinary pause between two calls in one agentic burst, short enough that a
/// real context-switch reliably starts a new one. There is no session/conversation id anywhere in
/// <see cref="Hades.Core.Tracing.TraceStore"/>'s schema to group by instead - traces only carry a
/// tool name and a timestamp - so a time-based gap is the only signal available, and also the one
/// that actually answers the motivating question: which calls happened together, in what order.
///
/// <b>Traces are per-project</b> (each known project owns its own <c>traces.db</c> - see
/// <see cref="Hades.Core.Storage.AppPaths.TracesDb"/>), so every action here resolves to exactly
/// ONE project via <see cref="ToolSupport.ResolveProject"/> - the SAME auto-resolve-when-only-one-
/// known, name-or-id, explicit-error-when-ambiguous convention every project-scoped MCP tool
/// already uses (<see cref="Hades.Server.Mcp.MemoryTools"/> included) - rather than a fan-out/merge
/// across every known project the way <c>/control/summary</c>/<c>/control/projects</c>/
/// <c>/control/editors</c> do. Those three exist specifically to list ACROSS projects; traces do
/// not need a second, novel cross-project design when this one is already proven and every caller
/// already expects it. <see cref="ToolSupport.ResolveProject"/> throws <see cref="McpException"/>
/// on an unknown/ambiguous handle - caught here and turned into a resolved 400, never left to
/// propagate as an unhandled 500 (this API's JSON error shape, not MCP's JSON-RPC envelope - see
/// <see cref="ControlAuth"/>'s own doc comment on why the two must not be conflated).
///
/// <b>Filtering happens AFTER grouping, never before.</b> <see cref="GetSequences"/> fetches
/// every recent trace unfiltered, groups the complete set into sequences, and only then applies
/// tool/outcome/duration filters to the resulting sequences (<see cref="ResolveSequences"/>).
/// Filtering individual traces first would corrupt grouping: removing one call from the middle of a
/// real burst would fabricate a gap that never existed, silently splitting one true sequence into
/// two. The unit of this endpoint's response is a sequence, so its filters describe which
/// SEQUENCES the caller wants, not which raw rows.
/// </summary>
public static class TracesEndpoint
{
    /// <summary>Idle gap, in milliseconds, that ends one sequence and starts the next - see this
    /// class's own doc comment for why 30 seconds.</summary>
    public const long DefaultSequenceGapMs = 30_000;

    const int DefaultLimit = 200;
    const int MaxLimit = 500; // matches TraceStore's own MaxResults

    // ------------------------------------------------------------------------------------- GET

    public static IResult GetSequences(ProjectService projects, string? project,
        string? tool, string? outcome, long? minDurationMs, long? maxDurationMs, int limit = DefaultLimit)
    {
        if (!TryResolveProject(projects, project, out var productGuid, out var projectError)) return projectError!;
        if (!TryParseOutcome(outcome, out var outcomeFilter, out var outcomeError)) return outcomeError!;

        var clampedLimit = Math.Clamp(limit, 1, MaxLimit);

        using var store = TraceStore.Open(projects.Paths.TracesDb(productGuid));
        var recent = store.RecentTraces(clampedLimit);

        var snapshots = recent.Select(t => new TraceRecordSnapshot
        {
            TraceId = t.TraceId,
            Tool = t.ToolName,
            StartUtcMs = t.StartUtcMs,
            EndUtcMs = t.EndUtcMs,
            Status = t.Status,
        }).ToList();

        var sequences = GroupIntoSequences(snapshots);
        var result = ResolveSequences(sequences, tool, outcomeFilter, minDurationMs, maxDurationMs, truncated: recent.Count >= clampedLimit);

        return Results.Json(result);
    }

    public static IResult GetTraceDetail(ProjectService projects, string? project, string traceId)
    {
        if (!TryResolveProject(projects, project, out var productGuid, out var projectError)) return projectError!;

        using var store = TraceStore.Open(projects.Paths.TracesDb(productGuid));
        var detail = store.GetTrace(traceId);

        if (detail is null)
        {
            return Results.Json(new { error = $"Unknown trace '{traceId}'." }, statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(new TraceDetailResult
        {
            TraceId = detail.Trace.TraceId,
            Tool = detail.Trace.ToolName,
            StartUtcMs = detail.Trace.StartUtcMs,
            EndUtcMs = detail.Trace.EndUtcMs,
            DurationMs = detail.Trace.DurationMs,
            Outcome = ResolveOutcome(detail.Trace.Status),
            Spans = detail.Spans.Select(ToSpanRow).ToList(),
        });
    }

    public static IResult GetSlowTools(ProjectService projects, string? project, int limit = 20)
    {
        if (!TryResolveProject(projects, project, out var productGuid, out var projectError)) return projectError!;

        using var store = TraceStore.Open(projects.Paths.TracesDb(productGuid));
        var slow = store.SlowestTools(Math.Clamp(limit, 1, MaxLimit));

        return Results.Json(new SlowToolsResult
        {
            Tools = slow.Select(s => new SlowToolRow
            {
                Tool = s.ToolName,
                CallCount = s.CallCount,
                AverageDurationMs = s.AverageDurationMs,
                MaxDurationMs = s.MaxDurationMs,
            }).ToList(),
        });
    }

    public static IResult GetFailures(ProjectService projects, string? project, int limit = 50)
    {
        if (!TryResolveProject(projects, project, out var productGuid, out var projectError)) return projectError!;

        using var store = TraceStore.Open(projects.Paths.TracesDb(productGuid));
        var failures = store.Failures(Math.Clamp(limit, 1, MaxLimit));

        var rows = failures.Select(f => new FailedCallRow
        {
            TraceId = f.TraceId,
            Tool = f.ToolName,
            StartUtcMs = f.StartUtcMs,
            DurationMs = f.DurationMs,
            Error = ExtractErrorMessage(store, f.TraceId),
        }).ToList();

        return Results.Json(new FailedCallsResult { Failures = rows });
    }

    // --------------------------------------------------------------------------------- pure core

    /// <summary>
    /// Groups <paramref name="traces"/> into sequences - see this class's own doc comment for the
    /// exact rule. Sorts its own input by <see cref="TraceRecordSnapshot.StartUtcMs"/> first, so a
    /// caller handing this newest-first (as <see cref="TraceStore.RecentTraces"/> does) or in any
    /// other order never has to remember to reorder it correctly beforehand.
    /// </summary>
    public static IReadOnlyList<TraceSequenceRow> GroupIntoSequences(
        IReadOnlyList<TraceRecordSnapshot> traces, long gapThresholdMs = DefaultSequenceGapMs)
    {
        var chronological = traces.OrderBy(t => t.StartUtcMs).ToList();

        var sequences = new List<TraceSequenceRow>();
        List<TraceRecordSnapshot>? current = null;
        var latestEndSoFar = long.MinValue;

        foreach (var trace in chronological)
        {
            if (current is not null && trace.StartUtcMs - latestEndSoFar > gapThresholdMs)
            {
                sequences.Add(BuildSequence(current));
                current = null;
            }

            (current ??= []).Add(trace);
            latestEndSoFar = Math.Max(latestEndSoFar, trace.EndUtcMs ?? trace.StartUtcMs);
        }

        if (current is not null) sequences.Add(BuildSequence(current));

        return sequences;
    }

    static TraceSequenceRow BuildSequence(IReadOnlyList<TraceRecordSnapshot> traces)
    {
        var first = traces[0];
        var endUtcMs = traces.Max(t => t.EndUtcMs ?? t.StartUtcMs);
        var anyError = traces.Any(t => !string.Equals(t.Status, "ok", StringComparison.Ordinal));

        return new TraceSequenceRow
        {
            Id = first.TraceId,
            Tools = traces.Select(t => t.Tool).ToList(),
            Pattern = string.Join(" → ", traces.Select(t => t.Tool)),
            CallCount = traces.Count,
            StartUtcMs = first.StartUtcMs,
            EndUtcMs = endUtcMs,
            DurationMs = Math.Max(0, endUtcMs - first.StartUtcMs),
            Outcome = anyError ? TraceOutcome.Error : TraceOutcome.Ok,
            TraceIds = traces.Select(t => t.TraceId).ToList(),
        };
    }

    /// <summary>Filters, sorts (most recent first), and reports truncation over an already-grouped
    /// sequence list - see this class's own doc comment for why filtering never happens before
    /// grouping.</summary>
    public static TraceSequencesResult ResolveSequences(IReadOnlyList<TraceSequenceRow> sequences,
        string? tool, TraceOutcome? outcome, long? minDurationMs, long? maxDurationMs, bool truncated)
    {
        var filtered = sequences
            .Where(s => tool is null || s.Tools.Any(t => t.Contains(tool, StringComparison.OrdinalIgnoreCase)))
            .Where(s => outcome is null || s.Outcome == outcome)
            .Where(s => minDurationMs is null || s.DurationMs >= minDurationMs)
            .Where(s => maxDurationMs is null || s.DurationMs <= maxDurationMs)
            .OrderByDescending(s => s.EndUtcMs)
            .ToList();

        return new TraceSequencesResult { Sequences = filtered, Truncated = truncated };
    }

    // ------------------------------------------------------------------------------------ helpers

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

    static bool TryParseOutcome(string? raw, out TraceOutcome? outcome, out IResult? error)
    {
        if (string.IsNullOrEmpty(raw)) { outcome = null; error = null; return true; }
        if (string.Equals(raw, "ok", StringComparison.OrdinalIgnoreCase)) { outcome = TraceOutcome.Ok; error = null; return true; }
        if (string.Equals(raw, "error", StringComparison.OrdinalIgnoreCase)) { outcome = TraceOutcome.Error; error = null; return true; }

        outcome = null;
        error = Results.Json(new { error = $"Unknown 'outcome' filter '{raw}' — must be 'ok' or 'error'." }, statusCode: StatusCodes.Status400BadRequest);
        return false;
    }

    static TraceOutcome ResolveOutcome(string? status) =>
        string.Equals(status, "ok", StringComparison.Ordinal) ? TraceOutcome.Ok : TraceOutcome.Error;

    static SpanRow ToSpanRow(SpanRecord span) => new()
    {
        SpanId = span.SpanId,
        ParentSpanId = span.ParentSpanId,
        Name = span.Name,
        Kind = span.Kind,
        StartUtcMs = span.StartUtcMs,
        EndUtcMs = span.EndUtcMs,
        DurationMs = span.EndUtcMs is { } endUtcMs ? Math.Max(0, endUtcMs - span.StartUtcMs) : null,
        Status = span.Status,
        Attributes = FlattenJsonOrNull(span.Attributes),
        Events = FlattenJsonOrNull(span.Events),
    };

    static IReadOnlyList<SpanAttributeRow>? FlattenJsonOrNull(string? json)
    {
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        return FlattenJsonToDisplayRows(document.RootElement);
    }

    /// <summary>Flattens any JSON tree into <see cref="SpanAttributeRow"/> leaves - see that type's
    /// own doc comment for exactly what "flattened" and "pre-rendered" mean and the gap this closes.
    /// An object's keys are sorted alphabetically before descending into them, for a stable,
    /// deterministic row order (a bare <c>Dictionary</c>/<c>JsonElement</c> enumeration order has
    /// none) - the same ordering Swift's own retired <c>ControlJSONValue.stringLeaves()</c> used to
    /// apply client-side, now done once, here.</summary>
    public static IReadOnlyList<SpanAttributeRow> FlattenJsonToDisplayRows(JsonElement element)
    {
        var rows = new List<SpanAttributeRow>();
        FlattenInto(element, prefix: "", rows);
        return rows;
    }

    static void FlattenInto(JsonElement element, string prefix, List<SpanAttributeRow> rows)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    FlattenInto(property.Value, prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}", rows);
                }
                return;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    FlattenInto(item, $"{prefix}[{index}]", rows);
                    index++;
                }
                return;

            case JsonValueKind.String:
                // The decoded text itself, never the raw quoted/escaped JSON token - matches
                // exactly what a .string leaf already read as under the retired stringLeaves().
                rows.Add(new SpanAttributeRow { Key = prefix, ValueDisplay = element.GetString() ?? "" });
                return;

            default:
                // Number, True, False, Null: the literal JSON token IS already the exact display
                // text - GetRawText() reads back exactly what was written, with no re-serialization,
                // no rounding, no locale. This is the one line that actually closes the gap: every
                // non-string scalar, previously dropped entirely, is now visible.
                rows.Add(new SpanAttributeRow { Key = prefix, ValueDisplay = element.GetRawText() });
                return;
        }
    }

    /// <summary>Reads the triggering exception's message back off a failed trace's root span
    /// events (see <see cref="Hades.Core.Tracing.TraceStore.RecordToolCall"/>'s own
    /// OpenTelemetry-style "exception" event) - null for any shape this does not recognise (no
    /// spans, no events, not the expected exception-event shape) rather than throwing, since a
    /// missing error message must never turn an otherwise-valid failures list into a 500.</summary>
    internal static string? ExtractErrorMessage(TraceStore store, string traceId)
    {
        var rootSpan = store.GetTrace(traceId)?.Spans.FirstOrDefault();
        if (rootSpan?.Events is not { } eventsJson) return null;

        try
        {
            using var document = JsonDocument.Parse(eventsJson);
            foreach (var evt in document.RootElement.EnumerateArray())
            {
                if (evt.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    && name.GetString() == "exception"
                    && evt.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString();
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
