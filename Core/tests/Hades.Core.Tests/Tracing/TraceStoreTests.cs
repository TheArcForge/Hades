using Hades.Core.Tracing;
using Microsoft.Data.Sqlite;

namespace Hades.Core.Tests.Tracing;

public class TraceStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    TraceStore Open()
    {
        Directory.CreateDirectory(_dir);
        return TraceStore.Open(Path.Combine(_dir, "traces.db"));
    }

    static ToolCallOutcome Outcome(string toolName = "search_by_name", long startUtcMs = 1_700_000_000_000,
        long endUtcMs = 1_700_000_000_250, string status = "ok", string? errorMessage = null,
        string? argumentsJson = null, string? resultType = null, long? resultSizeBytes = null) => new()
    {
        ToolName = toolName,
        StartUtcMs = startUtcMs,
        EndUtcMs = endUtcMs,
        Status = status,
        ErrorMessage = errorMessage,
        ArgumentsJson = argumentsJson,
        ResultType = resultType,
        ResultSizeBytes = resultSizeBytes,
    };

    // ---------------------------------------------------------------- schema

    [Fact]
    public void Open_CreatesAnEmptyUsableStore()
    {
        using var store = Open();

        Assert.Empty(store.RecentTraces());
    }

    [Fact]
    public void Open_MigratesFromAnOlderSchemaVersion_DiscardingOldData()
    {
        // Traces are derived data - see the class this store backs. A version bump means
        // "drop and recreate", not "alter in place", mirroring GraphSchema's own migration
        // test: build a database stamped with a version this store does not recognise, then
        // prove opening it self-heals rather than crashing or reading garbage.
        Directory.CreateDirectory(_dir);
        var dbPath = Path.Combine(_dir, "traces.db");
        using (var raw = new SqliteConnection(TestSqlite.ConnectionString(dbPath)))
        {
            raw.Open();
            using (var cmd = raw.CreateCommand())
            {
                cmd.CommandText = "CREATE TABLE traces (trace_id TEXT PRIMARY KEY, root_span_name TEXT);";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = raw.CreateCommand())
            {
                cmd.CommandText = "PRAGMA user_version = 999999;";
                cmd.ExecuteNonQuery();
            }
        }

        SqliteConnection.ClearAllPools();

        using var store = TraceStore.Open(dbPath);
        var exception = Record.Exception(() => store.RecordToolCall(Outcome()));

        Assert.Null(exception);
        Assert.Single(store.RecentTraces());
    }

    // ---------------------------------------------------------------- recording

    [Fact]
    public void RecordToolCall_ForASuccessfulCall_WritesOneTraceWithNameDurationAndOkStatus()
    {
        using var store = Open();

        store.RecordToolCall(Outcome(toolName: "search_by_name", startUtcMs: 1000, endUtcMs: 1250, status: "ok"));

        var trace = Assert.Single(store.RecentTraces());
        Assert.Equal("search_by_name", trace.ToolName);
        Assert.Equal("ok", trace.Status);
        Assert.Equal(250, trace.DurationMs);
    }

    [Fact]
    public void RecordToolCall_ForAFailedCall_RecordsTheFailureAndItsMessage()
    {
        // A trace store that only remembers successes is useless for the thing traces exist for.
        using var store = Open();

        var traceId = store.RecordToolCall(Outcome(toolName: "find_references_to", status: "error",
            errorMessage: "Project unknown."));

        var trace = Assert.Single(store.Failures());
        Assert.Equal(traceId, trace.TraceId);
        Assert.Equal("error", trace.Status);

        var detail = store.GetTrace(traceId);
        Assert.NotNull(detail);
        var span = Assert.Single(detail!.Spans);
        Assert.Contains("Project unknown.", span.Events);
    }

    [Fact]
    public void RecordToolCall_WritesExactlyOneSpanAsTheRootSpan()
    {
        using var store = Open();

        var traceId = store.RecordToolCall(Outcome(toolName: "hades_status"));

        var detail = store.GetTrace(traceId);
        var span = Assert.Single(detail!.Spans);
        Assert.Equal("hades_status", span.Name);
        Assert.Null(span.ParentSpanId);
    }

    [Fact]
    public void RecordToolCall_TimestampsAreUtcEpochMillisecondsNotSomeOtherUnit()
    {
        // Matches the file_state.mtime_utc convention exactly - a raw Unix-epoch millisecond
        // integer round-tripping through the INTEGER column unchanged, not ticks, not seconds,
        // not an ISO-8601 string.
        using var store = Open();
        const long startUtcMs = 1_700_000_000_123;
        const long endUtcMs = 1_700_000_000_456;

        var traceId = store.RecordToolCall(Outcome(startUtcMs: startUtcMs, endUtcMs: endUtcMs));

        var trace = Assert.Single(store.RecentTraces());
        Assert.Equal(startUtcMs, trace.StartUtcMs);
        Assert.Equal(endUtcMs, trace.EndUtcMs);

        var detail = store.GetTrace(traceId);
        Assert.Equal(startUtcMs, detail!.Spans[0].StartUtcMs);
        Assert.Equal(endUtcMs, detail.Spans[0].EndUtcMs);
    }

    [Fact]
    public void RecordToolCall_RecordsArgumentsOnTheSpan()
    {
        using var store = Open();

        var traceId = store.RecordToolCall(Outcome(argumentsJson: """{"namePattern":"PlayerController"}"""));

        var detail = store.GetTrace(traceId);
        Assert.Contains("PlayerController", detail!.Spans[0].Attributes);
    }

    [Fact]
    public void RecordToolCall_RecordsAResultSizeButNeverTheResultItself()
    {
        // Results include file contents and whole hierarchies - a trace store that grows with
        // result size would dwarf the graph. Only a size/summary is recorded, never the payload.
        using var store = Open();
        var hugeResultMarker = new string('x', 50_000);

        var traceId = store.RecordToolCall(Outcome(resultType: "SearchResult", resultSizeBytes: 50_000));

        var detail = store.GetTrace(traceId);
        var attributes = detail!.Spans[0].Attributes!;
        Assert.Contains("50000", attributes);
        Assert.True(attributes.Length < 500, $"attributes blob was {attributes.Length} chars - looks like a full result got stored, not a summary.");
        Assert.DoesNotContain(hugeResultMarker, attributes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

/// <summary>
/// <see cref="ToolCallTracer"/> is the safety wrapper around <see cref="TraceStore"/>: the class
/// under test here is what actually stands between a broken trace database and a real tool call,
/// so its own tests are kept in this same file, right alongside the store it wraps.
/// </summary>
public class ToolCallTracerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    string DbPath => Path.Combine(_dir, "traces.db");

    TraceStore OpenStoreDirectly()
    {
        Directory.CreateDirectory(_dir);
        return TraceStore.Open(DbPath);
    }

    // ---------------------------------------------------------------- the central guarantee

    [Fact]
    public void Trace_WhenTheTraceDatabasePathIsUnwritable_TheToolCallStillSucceeds()
    {
        // "Unwritable" here means the database can never even be OPENED: a directory sits where
        // the .db file would go, which reliably fails a SQLite open cross-platform (the same
        // trick GraphDatabaseTests uses to break the graph db). This is the property that decides
        // whether tracing is safe to leave on by default - proved, not assumed.
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(DbPath);

        var tracer = new ToolCallTracer(DbPath);

        var result = tracer.Trace("search_by_name", null, () => "actual tool result");

        Assert.Equal("actual tool result", result);
    }

    [Fact]
    public void Trace_WhenTheTraceDatabaseIsUnwritable_AndTheWrappedCallThrows_TheOriginalExceptionStillPropagates()
    {
        // Tracing must be invisible on the failure path too: a broken trace store must not mask
        // the real failure behind some unrelated tracing exception, or swallow it entirely.
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(DbPath);

        var tracer = new ToolCallTracer(DbPath);

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            tracer.Trace<string>("find_references_to", null, () => throw new InvalidOperationException("real failure")));

        Assert.Equal("real failure", thrown.Message);
    }

    [Fact]
    public void Trace_OnAHealthyStore_StillReturnsTheWrappedCallsResult()
    {
        // The unwritable-path tests above would trivially "pass" if Trace always swallowed the
        // wrapped call's own result too - this is the control that proves it does not.
        var tracer = new ToolCallTracer(DbPath);

        var result = tracer.Trace("hades_status", null, () => 42);

        Assert.Equal(42, result);
    }

    // ---------------------------------------------------------------- recording behaviour

    [Fact]
    public void Trace_OnSuccess_RecordsOneOkTrace()
    {
        var tracer = new ToolCallTracer(DbPath);

        tracer.Trace("search_by_name", """{"namePattern":"Foo"}""", () => "result");

        using var store = OpenStoreDirectly();
        var trace = Assert.Single(store.RecentTraces());
        Assert.Equal("search_by_name", trace.ToolName);
        Assert.Equal("ok", trace.Status);
    }

    [Fact]
    public void Trace_WhenTheCallThrows_RecordsAnErrorTraceWithTheExceptionMessage()
    {
        var tracer = new ToolCallTracer(DbPath);

        Assert.Throws<InvalidOperationException>(() =>
            tracer.Trace<string>("find_references_to", null, () => throw new InvalidOperationException("bad path")));

        using var store = OpenStoreDirectly();
        var trace = Assert.Single(store.Failures());
        Assert.Equal("find_references_to", trace.ToolName);

        var detail = store.GetTrace(trace.TraceId);
        Assert.Contains("bad path", detail!.Spans[0].Events);
    }

    [Fact]
    public void Trace_NeverRecordsTheFullResultOnlyASizeSummary()
    {
        var tracer = new ToolCallTracer(DbPath);
        var hugePayload = new string('y', 20_000);

        tracer.Trace("get_project_summary", null, () => hugePayload);

        using var store = OpenStoreDirectly();
        var traceId = store.RecentTraces()[0].TraceId;
        var detail = store.GetTrace(traceId);
        var attributes = detail!.Spans[0].Attributes ?? "";

        Assert.DoesNotContain(hugePayload, attributes);
        Assert.True(attributes.Length < 500,
            $"attributes blob was {attributes.Length} chars - looks like the full result got stored.");
    }

    [Fact]
    public void RecordOutcome_LetsACallerClassifyAGracefulNonThrowingFailure()
    {
        // The MCP server sees tool failures as a normal CallToolResult with IsError = true, not
        // as a thrown exception (McpException is caught and converted before it reaches a
        // request filter) - so the tracer needs a way to record a failure the caller already
        // determined itself, without forcing one through the throw/catch path.
        var tracer = new ToolCallTracer(DbPath);

        tracer.RecordOutcome("propose_memory_update", null, startUtcMs: 1000, durationMs: 50,
            ok: false, errorMessage: "targetFile not found");

        using var store = OpenStoreDirectly();
        var trace = Assert.Single(store.Failures());
        Assert.Equal("propose_memory_update", trace.ToolName);
    }

    [Fact]
    public void RecordOutcome_AlsoNeverFailsWhenTheStoreIsUnwritable()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(DbPath);
        var tracer = new ToolCallTracer(DbPath);

        var exception = Record.Exception(() =>
            tracer.RecordOutcome("hades_status", null, startUtcMs: 1000, durationMs: 5, ok: true, errorMessage: null));

        Assert.Null(exception);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
