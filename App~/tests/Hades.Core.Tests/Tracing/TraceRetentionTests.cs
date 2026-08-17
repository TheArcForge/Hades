using Hades.Core.Tracing;
using Microsoft.Data.Sqlite;

namespace Hades.Core.Tests.Tracing;

/// <summary>
/// Task 6: pruning old traces, and the read-only query surface a future control API will expose
/// (recent traces, one trace with its spans, slowest tools, failures only). See TraceStoreTests
/// for Task 5's recording behaviour - this file only covers what was added on top of it.
/// </summary>
public class TraceRetentionTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    string DbPath => Path.Combine(_dir, "traces.db");

    TraceStore Open()
    {
        Directory.CreateDirectory(_dir);
        return TraceStore.Open(DbPath);
    }

    static ToolCallOutcome Outcome(string toolName = "search_by_name", long startUtcMs = 1_700_000_000_000,
        long endUtcMs = 1_700_000_000_250, string status = "ok", string? errorMessage = null) => new()
    {
        ToolName = toolName,
        StartUtcMs = startUtcMs,
        EndUtcMs = endUtcMs,
        Status = status,
        ErrorMessage = errorMessage,
    };

    // ---------------------------------------------------------------- retention

    [Fact]
    public void Prune_RemovesTracesOlderThanTheCutoff_ButKeepsNewerOnesUntouched()
    {
        using var store = Open();
        var oldId = store.RecordToolCall(Outcome(toolName: "old_call", startUtcMs: 1000, endUtcMs: 1050));
        var newId = store.RecordToolCall(Outcome(toolName: "new_call", startUtcMs: 5000, endUtcMs: 5050));

        var prunedCount = store.Prune(olderThanUtcMs: 3000);

        Assert.Equal(1, prunedCount);
        Assert.Null(store.GetTrace(oldId));
        Assert.NotNull(store.GetTrace(newId));
        var remaining = Assert.Single(store.RecentTraces());
        Assert.Equal("new_call", remaining.ToolName);
    }

    [Fact]
    public void Prune_WithNothingOlderThanTheCutoff_PrunesNothing()
    {
        using var store = Open();
        store.RecordToolCall(Outcome(startUtcMs: 5000, endUtcMs: 5050));

        var prunedCount = store.Prune(olderThanUtcMs: 1000);

        Assert.Equal(0, prunedCount);
        Assert.Single(store.RecentTraces());
    }

    [Fact]
    public void Prune_ATraceExactlyAtTheCutoff_Survives()
    {
        // "Older than" is a strict inequality - a trace exactly on the boundary is inside the
        // retention window, not outside it.
        using var store = Open();
        store.RecordToolCall(Outcome(startUtcMs: 3000, endUtcMs: 3050));

        store.Prune(olderThanUtcMs: 3000);

        Assert.Single(store.RecentTraces());
    }

    [Fact]
    public void Prune_AlsoDeletesTheSpansOfPrunedTraces_NotJustTheTraceRow()
    {
        // The trap this exists to catch: ON DELETE CASCADE in the spans table's schema requires
        // PRAGMA foreign_keys = ON, which SQLite defaults to OFF. A prune implementation that
        // just did "DELETE FROM traces" and trusted the declared cascade would leave every pruned
        // trace's spans behind forever - invisible to GetTrace (which already returns null for an
        // unknown trace id regardless), so this checks the spans TABLE directly, bypassing the
        // store's own query surface entirely, exactly as the plan calls for.
        using var store = Open();
        var traceId = store.RecordToolCall(Outcome(startUtcMs: 1000, endUtcMs: 1050));
        Assert.Equal(1, CountSpansForTrace(traceId));

        store.Prune(olderThanUtcMs: 3000);

        Assert.Equal(0, CountSpansForTrace(traceId));
    }

    [Fact]
    public void Prune_LeavesTheSpansOfSurvivingTracesAlone()
    {
        using var store = Open();
        var survivorId = store.RecordToolCall(Outcome(startUtcMs: 5000, endUtcMs: 5050));
        store.RecordToolCall(Outcome(startUtcMs: 1000, endUtcMs: 1050));

        store.Prune(olderThanUtcMs: 3000);

        Assert.Equal(1, CountSpansForTrace(survivorId));
    }

    [Fact]
    public void Prune_OnAnEmptyStore_ReturnsZeroWithoutErroring()
    {
        using var store = Open();

        var exception = Record.Exception(() => store.Prune(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        Assert.Null(exception);
    }

    [Fact]
    public void Prune_CanRunWhileACallIsBeingRecorded_WithoutEitherOperationFailing()
    {
        // Not a timing assertion (those are flaky) - a correctness one: retention is wired to run
        // on its own schedule, off the tool-call request path (see Program.cs), so it must be
        // able to interleave with concurrent RecordToolCall writes on the same database without
        // either side throwing. Each call opens its own connection, exactly as the real tracer and
        // the real retention timer do.
        Directory.CreateDirectory(_dir);
        using (var seed = TraceStore.Open(DbPath))
            seed.RecordToolCall(Outcome(startUtcMs: 1000, endUtcMs: 1050));

        var recordException = Record.Exception(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                using var store = TraceStore.Open(DbPath);
                store.RecordToolCall(Outcome(toolName: $"call_{i}", startUtcMs: 10_000 + i, endUtcMs: 10_050 + i));
            }
        });

        var pruneException = Record.Exception(() =>
        {
            using var store = TraceStore.Open(DbPath);
            store.Prune(olderThanUtcMs: 5000);
        });

        Assert.Null(recordException);
        Assert.Null(pruneException);
    }

    int CountSpansForTrace(string traceId)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM spans WHERE trace_id = $traceId;";
        command.Parameters.AddWithValue("$traceId", traceId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    // ---------------------------------------------------------------- query surface

    [Fact]
    public void RecentTraces_OrdersNewestFirst()
    {
        using var store = Open();
        store.RecordToolCall(Outcome(toolName: "first", startUtcMs: 1000, endUtcMs: 1100));
        store.RecordToolCall(Outcome(toolName: "second", startUtcMs: 2000, endUtcMs: 2100));
        store.RecordToolCall(Outcome(toolName: "third", startUtcMs: 3000, endUtcMs: 3100));

        var traces = store.RecentTraces();

        Assert.Equal(["third", "second", "first"], traces.Select(t => t.ToolName));
    }

    [Fact]
    public void RecentTraces_ExposesRowIdAsAMonotonicInsertionOrderTiebreaker()
    {
        // T3: start_time has only millisecond resolution, and a real burst can record two calls
        // inside one tick - three same-millisecond calls here, so start_time alone cannot order
        // them. RowId (SQLite's own rowid) is the deterministic tiebreaker: it only ever grows, so
        // it recovers true insertion order when timestamps tie. Newest-first overall means ties
        // resolve last-inserted-first here (gamma, then beta, then alpha) - see
        // TracesGroupIntoSequencesTests.SameMillisecondTies... for why GroupIntoSequences' own
        // ascending re-sort then needs RowId too, to turn this back into chronological order.
        using var store = Open();
        store.RecordToolCall(Outcome(toolName: "alpha", startUtcMs: 9000, endUtcMs: 9010));
        store.RecordToolCall(Outcome(toolName: "beta", startUtcMs: 9000, endUtcMs: 9020));
        store.RecordToolCall(Outcome(toolName: "gamma", startUtcMs: 9000, endUtcMs: 9030));

        var traces = store.RecentTraces();

        Assert.Equal(["gamma", "beta", "alpha"], traces.Select(t => t.ToolName));
        Assert.True(traces[0].RowId > traces[1].RowId, "gamma's RowId should be greater than beta's");
        Assert.True(traces[1].RowId > traces[2].RowId, "beta's RowId should be greater than alpha's");
    }

    [Fact]
    public void RecentTraces_RespectsLimit()
    {
        using var store = Open();
        for (var i = 0; i < 5; i++) store.RecordToolCall(Outcome(startUtcMs: 1000 + i, endUtcMs: 1100 + i));

        Assert.Equal(2, store.RecentTraces(limit: 2).Count);
    }

    [Fact]
    public void GetTrace_ReturnsTheTraceWithItsOneSpan()
    {
        using var store = Open();
        var traceId = store.RecordToolCall(Outcome(toolName: "get_project_summary", startUtcMs: 1000, endUtcMs: 1400));

        var detail = store.GetTrace(traceId);

        Assert.NotNull(detail);
        Assert.Equal("get_project_summary", detail!.Trace.ToolName);
        var span = Assert.Single(detail.Spans);
        Assert.Equal("get_project_summary", span.Name);
        Assert.Equal(traceId, span.TraceId);
    }

    [Fact]
    public void GetTrace_ForAnUnknownId_ReturnsNull()
    {
        using var store = Open();

        Assert.Null(store.GetTrace("does-not-exist"));
    }

    [Fact]
    public void Failures_ReturnsOnlyErrorStatusTraces()
    {
        using var store = Open();
        store.RecordToolCall(Outcome(toolName: "ok-one", status: "ok"));
        store.RecordToolCall(Outcome(toolName: "bad-one", status: "error", errorMessage: "boom"));
        store.RecordToolCall(Outcome(toolName: "ok-two", status: "ok"));

        var failures = store.Failures();

        var failure = Assert.Single(failures);
        Assert.Equal("bad-one", failure.ToolName);
    }

    [Fact]
    public void Failures_RespectsLimit()
    {
        using var store = Open();
        for (var i = 0; i < 5; i++)
            store.RecordToolCall(Outcome(toolName: $"bad_{i}", status: "error", errorMessage: "boom",
                startUtcMs: 1000 + i, endUtcMs: 1100 + i));

        Assert.Equal(2, store.Failures(limit: 2).Count);
    }

    [Fact]
    public void SlowestTools_OrdersByAverageDurationDescending()
    {
        using var store = Open();
        store.RecordToolCall(Outcome(toolName: "fast_tool", startUtcMs: 0, endUtcMs: 10));
        store.RecordToolCall(Outcome(toolName: "slow_tool", startUtcMs: 0, endUtcMs: 900));
        store.RecordToolCall(Outcome(toolName: "fast_tool", startUtcMs: 0, endUtcMs: 20));

        var stats = store.SlowestTools();

        Assert.Equal("slow_tool", stats[0].ToolName);
        Assert.Equal(1, stats[0].CallCount);
        Assert.Equal(900, stats[0].MaxDurationMs);
        Assert.Equal("fast_tool", stats[1].ToolName);
        Assert.Equal(2, stats[1].CallCount);
        Assert.Equal(15, stats[1].AverageDurationMs);
    }

    [Fact]
    public void SlowestTools_OnAnEmptyStoreReturnsEmptyNotAnError()
    {
        using var store = Open();

        Assert.Empty(store.SlowestTools());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
