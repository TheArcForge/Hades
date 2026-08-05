using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Hades.Core.Tracing;

/// <summary>One tool call's outcome, as <see cref="ToolCallTracer"/> hands it to
/// <see cref="TraceStore.RecordToolCall"/> - everything needed to write one trace and its one
/// root span.</summary>
public sealed record ToolCallOutcome
{
    public required string ToolName { get; init; }
    public required long StartUtcMs { get; init; }
    public required long EndUtcMs { get; init; }

    /// <summary>"ok" or "error" - see <see cref="TraceStore"/>'s class doc comment.</summary>
    public required string Status { get; init; }

    /// <summary>Set only when <see cref="Status"/> is "error". Recorded as a span event, not a
    /// bare column, so the shape matches how an exception would be recorded if this ever grows
    /// nested spans.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The call's arguments, already serialised to JSON text by the caller. Recorded in
    /// full - unlike the result, arguments are small, and are the one part of a historical call
    /// that cannot be reconstructed by calling the tool again.</summary>
    public string? ArgumentsJson { get; init; }

    /// <summary>The CLR type name of the result, or null on failure/no result. Never the result
    /// itself - see <see cref="TraceStore"/>'s class doc comment.</summary>
    public string? ResultType { get; init; }

    /// <summary>UTF-8 byte size of the result had it been serialised - a size, never the payload.</summary>
    public long? ResultSizeBytes { get; init; }
}

/// <summary>One row from <see cref="TraceStore.RecentTraces"/> / <see cref="TraceStore.Failures"/> -
/// trace-level columns only, no span detail.</summary>
public sealed record TraceSummary
{
    public required string TraceId { get; init; }
    public required string ToolName { get; init; }
    public required long StartUtcMs { get; init; }
    public long? EndUtcMs { get; init; }
    public string? Status { get; init; }
    public long? DurationMs { get; init; }
}

/// <summary>One row of <c>spans</c>. <see cref="Attributes"/> and <see cref="Events"/> are raw
/// JSON text, exactly as stored - callers that need structure parse it themselves, the same
/// loosely-typed "attributes bag" convention OpenTelemetry spans use.</summary>
public sealed record SpanRecord
{
    public required string SpanId { get; init; }
    public required string TraceId { get; init; }
    public string? ParentSpanId { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required long StartUtcMs { get; init; }
    public long? EndUtcMs { get; init; }
    public string? Status { get; init; }
    public string? Attributes { get; init; }
    public string? Events { get; init; }
}

/// <summary>One trace with every span it owns - what <see cref="TraceStore.GetTrace"/> returns.</summary>
public sealed record TraceDetail
{
    public required TraceSummary Trace { get; init; }
    public required IReadOnlyList<SpanRecord> Spans { get; init; }
}

/// <summary>One tool's aggregate timing, from <see cref="TraceStore.SlowestTools"/>.</summary>
public sealed record SlowToolStat
{
    public required string ToolName { get; init; }
    public required int CallCount { get; init; }
    public required double AverageDurationMs { get; init; }
    public required long MaxDurationMs { get; init; }
}

/// <summary>
/// SQLite-backed tool-call traces for one project. Entirely DERIVED: nothing else in Hades reads
/// it back to function, so it is corruptible without loss and deletable at any time to force an
/// empty rebuild - the same guarantee <see cref="Graph.GraphDatabase"/> and
/// <see cref="Memory.MemoryIndex"/> make for their own tables, just without a source of truth to
/// resync FROM afterward, because there is none: a deleted trace's history is simply gone, same as
/// deleting application log files.
///
/// Every tool call writes exactly one trace with exactly one root span (<c>parent_span_id</c> is
/// always null today) - <see cref="ToolCallTracer"/> does not yet instrument anything inside a
/// tool call, so nested spans are schema-ready but unused until something produces them.
///
/// Never write directly to this store from a tool call without going through
/// <see cref="ToolCallTracer"/> - its whole job is making sure a failure here (an unwritable
/// path, a full disk) cannot fail the call being traced. This class makes no such promise on its
/// own: <see cref="Open"/> and <see cref="RecordToolCall"/> throw exactly like
/// <see cref="Graph.GraphDatabase"/> does.
/// </summary>
public sealed class TraceStore : IDisposable
{
    // v1: first shape. Traces are derived data (see class doc comment), so - exactly like
    // GraphSchema - a version bump means "drop and recreate", not an in-place ALTER.
    public const int SchemaVersion = 1;

    readonly SqliteConnection _connection;

    TraceStore(SqliteConnection connection) => _connection = connection;

    public static TraceStore Open(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        try
        {
            connection.Open();

            // See Graph.GraphDatabase.Open's identical block: the RESULTING mode must be read
            // back and checked, not assumed - a refused WAL conversion (another connection
            // holding the file, or a filesystem that does not support it) must be visible, not
            // silently eaten.
            using (var journalMode = connection.CreateCommand())
            {
                journalMode.CommandText = "PRAGMA journal_mode = WAL;";
                var resulting = journalMode.ExecuteScalar() as string;

                if (!string.Equals(resulting, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Could not put '{databasePath}' into WAL mode (it reports '{resulting}'). "
                        + "Another process may hold the database, or it may be on a filesystem "
                        + "that does not support WAL, such as a network share.");
                }
            }

            using (var synchronous = connection.CreateCommand())
            {
                synchronous.CommandText = "PRAGMA synchronous = NORMAL;";
                synchronous.ExecuteNonQuery();
            }

            ApplySchema(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return new TraceStore(connection);
    }

    /// <summary>
    /// Records one tool call: one <c>traces</c> row plus its one root <c>span</c>, in a single
    /// transaction. Returns the generated trace id, so a caller (or a test) can look the trace
    /// back up via <see cref="GetTrace"/> without a separate query.
    ///
    /// Span <c>kind</c> is always "tool_call" today - the only kind of span this store's one
    /// producer (<see cref="ToolCallTracer"/>) ever creates. Arguments and a result size/type land
    /// in the span's <c>attributes</c> as a small JSON object; a failure's message lands in
    /// <c>events</c> as an OpenTelemetry-style exception event - never in a bare column, so a
    /// later nested-span producer has somewhere consistent to also put its own events.
    /// </summary>
    public string RecordToolCall(ToolCallOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var traceId = Guid.NewGuid().ToString("n");
        var spanId = Guid.NewGuid().ToString("n");
        var durationMs = outcome.EndUtcMs - outcome.StartUtcMs;

        using var transaction = _connection.BeginTransaction();

        using (var insertTrace = _connection.CreateCommand())
        {
            insertTrace.Transaction = transaction;
            insertTrace.CommandText = """
                INSERT INTO traces (trace_id, root_span_name, start_time, end_time, status, total_duration_ms, span_count, attributes)
                VALUES ($traceId, $name, $start, $end, $status, $duration, 1, NULL);
                """;
            insertTrace.Parameters.AddWithValue("$traceId", traceId);
            insertTrace.Parameters.AddWithValue("$name", outcome.ToolName);
            insertTrace.Parameters.AddWithValue("$start", outcome.StartUtcMs);
            insertTrace.Parameters.AddWithValue("$end", outcome.EndUtcMs);
            insertTrace.Parameters.AddWithValue("$status", outcome.Status);
            insertTrace.Parameters.AddWithValue("$duration", durationMs);
            insertTrace.ExecuteNonQuery();
        }

        using (var insertSpan = _connection.CreateCommand())
        {
            insertSpan.Transaction = transaction;
            insertSpan.CommandText = """
                INSERT INTO spans (span_id, trace_id, parent_span_id, name, kind, start_time, end_time, status, attributes, events)
                VALUES ($spanId, $traceId, NULL, $name, 'tool_call', $start, $end, $status, $attributes, $events);
                """;
            insertSpan.Parameters.AddWithValue("$spanId", spanId);
            insertSpan.Parameters.AddWithValue("$traceId", traceId);
            insertSpan.Parameters.AddWithValue("$name", outcome.ToolName);
            insertSpan.Parameters.AddWithValue("$start", outcome.StartUtcMs);
            insertSpan.Parameters.AddWithValue("$end", outcome.EndUtcMs);
            insertSpan.Parameters.AddWithValue("$status", outcome.Status);
            insertSpan.Parameters.AddWithValue("$attributes", (object?)BuildAttributesJson(outcome) ?? DBNull.Value);
            insertSpan.Parameters.AddWithValue("$events", (object?)BuildEventsJson(outcome) ?? DBNull.Value);
            insertSpan.ExecuteNonQuery();
        }

        transaction.Commit();
        return traceId;
    }

    /// <summary>Most recent traces, newest first - the default "what has Hades been doing" view.</summary>
    public IReadOnlyList<TraceSummary> RecentTraces(int limit = 50)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT trace_id, root_span_name, start_time, end_time, status, total_duration_ms
            FROM traces
            ORDER BY start_time DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxResults));
        return ReadTraceSummaries(command);
    }

    /// <summary>Failed calls only, newest first - what a "what's been going wrong" view reads.</summary>
    public IReadOnlyList<TraceSummary> Failures(int limit = 50)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT trace_id, root_span_name, start_time, end_time, status, total_duration_ms
            FROM traces
            WHERE status = 'error'
            ORDER BY start_time DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxResults));
        return ReadTraceSummaries(command);
    }

    /// <summary>One trace with every span it owns, or null when the id is unknown.</summary>
    public TraceDetail? GetTrace(string traceId)
    {
        ArgumentNullException.ThrowIfNull(traceId);

        TraceSummary? trace;
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT trace_id, root_span_name, start_time, end_time, status, total_duration_ms
                FROM traces WHERE trace_id = $traceId;
                """;
            command.Parameters.AddWithValue("$traceId", traceId);
            trace = ReadTraceSummaries(command).SingleOrDefault();
        }

        if (trace is null) return null;

        var spans = new List<SpanRecord>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT span_id, trace_id, parent_span_id, name, kind, start_time, end_time, status, attributes, events
                FROM spans WHERE trace_id = $traceId ORDER BY start_time;
                """;
            command.Parameters.AddWithValue("$traceId", traceId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                spans.Add(new SpanRecord
                {
                    SpanId = reader.GetString(0),
                    TraceId = reader.GetString(1),
                    ParentSpanId = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Name = reader.GetString(3),
                    Kind = reader.GetString(4),
                    StartUtcMs = reader.GetInt64(5),
                    EndUtcMs = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    Status = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Attributes = reader.IsDBNull(8) ? null : reader.GetString(8),
                    Events = reader.IsDBNull(9) ? null : reader.GetString(9),
                });
            }
        }

        return new TraceDetail { Trace = trace, Spans = spans };
    }

    /// <summary>
    /// Tools ranked by average call duration, slowest first - the performance-triage view. Ties in
    /// average order by tool name for a deterministic result.
    /// </summary>
    public IReadOnlyList<SlowToolStat> SlowestTools(int limit = 20)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT root_span_name, COUNT(*), AVG(total_duration_ms), MAX(total_duration_ms)
            FROM traces
            GROUP BY root_span_name
            ORDER BY AVG(total_duration_ms) DESC, root_span_name
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxResults));

        var results = new List<SlowToolStat>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SlowToolStat
            {
                ToolName = reader.GetString(0),
                CallCount = reader.GetInt32(1),
                AverageDurationMs = reader.GetDouble(2),
                MaxDurationMs = reader.GetInt64(3),
            });
        }

        return results;
    }

    /// <summary>
    /// Deletes every trace that started before <paramref name="olderThanUtcMs"/>, along with its
    /// spans, and returns how many traces were removed. Spans are deleted explicitly, in the same
    /// transaction as their traces - NOT left to the schema's <c>ON DELETE CASCADE</c>, because
    /// that requires <c>PRAGMA foreign_keys = ON</c>, which SQLite defaults to OFF. Relying on it
    /// would silently orphan every pruned trace's spans forever, exactly the bug
    /// TraceRetentionTests' cascade test exists to catch. See
    /// <see cref="Graph.GraphDatabase.DeleteNodesForPath"/> for the same explicit-multi-table-
    /// delete pattern used for the same reason.
    /// </summary>
    public int Prune(long olderThanUtcMs)
    {
        using var transaction = _connection.BeginTransaction();

        using (var deleteSpans = _connection.CreateCommand())
        {
            deleteSpans.Transaction = transaction;
            deleteSpans.CommandText = """
                DELETE FROM spans WHERE trace_id IN (SELECT trace_id FROM traces WHERE start_time < $cutoff);
                """;
            deleteSpans.Parameters.AddWithValue("$cutoff", olderThanUtcMs);
            deleteSpans.ExecuteNonQuery();
        }

        int deletedTraces;
        using (var deleteTraces = _connection.CreateCommand())
        {
            deleteTraces.Transaction = transaction;
            deleteTraces.CommandText = "DELETE FROM traces WHERE start_time < $cutoff;";
            deleteTraces.Parameters.AddWithValue("$cutoff", olderThanUtcMs);
            deletedTraces = deleteTraces.ExecuteNonQuery();
        }

        transaction.Commit();
        return deletedTraces;
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>Upper bound on every query method's <c>limit</c> - same token-budget reasoning as
    /// <see cref="Graph.GraphDatabase"/>'s MaxSearchLimit.</summary>
    const int MaxResults = 500;

    static List<TraceSummary> ReadTraceSummaries(SqliteCommand command)
    {
        var results = new List<TraceSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new TraceSummary
            {
                TraceId = reader.GetString(0),
                ToolName = reader.GetString(1),
                StartUtcMs = reader.GetInt64(2),
                EndUtcMs = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                Status = reader.IsDBNull(4) ? null : reader.GetString(4),
                DurationMs = reader.IsDBNull(5) ? null : reader.GetInt64(5),
            });
        }

        return results;
    }

    static string? BuildAttributesJson(ToolCallOutcome outcome)
    {
        if (outcome.ArgumentsJson is null && outcome.ResultType is null && outcome.ResultSizeBytes is null)
            return null;

        var attributes = new JsonObject();

        if (outcome.ArgumentsJson is not null)
        {
            // Embed as a real JSON value, not a nested string, so a caller can read
            // attributes.arguments.namePattern directly rather than parsing twice. Malformed
            // input (should not happen - ToolCallTracer only ever passes JsonSerializer output)
            // still degrades to a plain string rather than losing the value or throwing.
            attributes["arguments"] = JsonNode.Parse(outcome.ArgumentsJson) is { } parsed
                ? parsed
                : outcome.ArgumentsJson;
        }

        if (outcome.ResultType is not null) attributes["resultType"] = outcome.ResultType;
        if (outcome.ResultSizeBytes is not null) attributes["resultSizeBytes"] = outcome.ResultSizeBytes.Value;

        return attributes.ToJsonString();
    }

    static string? BuildEventsJson(ToolCallOutcome outcome)
    {
        if (outcome.ErrorMessage is null) return null;

        var events = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "exception",
                ["message"] = outcome.ErrorMessage,
                ["timeUtcMs"] = outcome.EndUtcMs,
            },
        };

        return events.ToJsonString();
    }

    /// <summary>
    /// Double-checked-lock migration, identical in shape to <see cref="Graph.GraphSchema.Apply"/> -
    /// see that class's doc comment for the full reasoning (unlocked fast path for the overwhelming
    /// "no migration needed" case; re-check inside the write lock because another connection may
    /// have migrated while this one waited for it).
    /// </summary>
    static void ApplySchema(SqliteConnection connection)
    {
        if (!NeedsMigration(connection, transaction: null)) return;

        using var transaction = connection.BeginTransaction(deferred: false);

        if (!NeedsMigration(connection, transaction))
        {
            transaction.Commit();
            return;
        }

        Execute(connection, transaction, "DROP TABLE IF EXISTS spans;");
        Execute(connection, transaction, "DROP TABLE IF EXISTS traces;");

        Execute(connection, transaction, """
            CREATE TABLE traces (
                trace_id          TEXT PRIMARY KEY,
                root_span_name    TEXT NOT NULL,
                start_time        INTEGER NOT NULL,
                end_time          INTEGER,
                status             TEXT,
                total_duration_ms  INTEGER,
                span_count         INTEGER,
                attributes         TEXT
            );
            """);
        Execute(connection, transaction, "CREATE INDEX idx_traces_start_time ON traces (start_time);");
        Execute(connection, transaction, "CREATE INDEX idx_traces_status ON traces (status);");

        Execute(connection, transaction, """
            CREATE TABLE spans (
                span_id        TEXT PRIMARY KEY,
                trace_id       TEXT NOT NULL REFERENCES traces(trace_id) ON DELETE CASCADE,
                parent_span_id TEXT,
                name           TEXT NOT NULL,
                kind           TEXT NOT NULL,
                start_time     INTEGER NOT NULL,
                end_time       INTEGER,
                status         TEXT,
                attributes     TEXT,
                events         TEXT
            );
            """);
        Execute(connection, transaction, "CREATE INDEX idx_spans_trace_id ON spans (trace_id);");

        Execute(connection, transaction, $"PRAGMA user_version = {SchemaVersion};");

        transaction.Commit();
    }

    /// <summary>True when the version stamp differs from <see cref="SchemaVersion"/>, or when it
    /// already matches but a table is missing - the signature of a process killed mid-migration.
    /// See <see cref="Graph.GraphSchema"/>'s identical method for the full reasoning.</summary>
    static bool NeedsMigration(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.Transaction = transaction;
            versionCommand.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(versionCommand.ExecuteScalar()) != SchemaVersion) return true;
        }

        using var tableCommand = connection.CreateCommand();
        tableCommand.Transaction = transaction;
        tableCommand.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('traces','spans');";
        return Convert.ToInt32(tableCommand.ExecuteScalar()) < 2;
    }

    static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
