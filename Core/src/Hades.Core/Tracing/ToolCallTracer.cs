using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Hades.Core.Tracing;

/// <summary>
/// Wraps a tool call with trace recording. The one property that matters, and the only one worth
/// documenting at this level: recording NEVER affects the call's own outcome. A tool wrapped by
/// <see cref="Trace{T}"/> returns exactly what it would have returned, or throws exactly what it
/// would have thrown, with no <see cref="TraceStore"/> in the picture at all - whether the trace
/// database is healthy, unwritable, or the disk behind it is gone. See
/// <c>ToolCallTracerTests.Trace_WhenTheTraceDatabasePathIsUnwritable_TheToolCallStillSucceeds</c>
/// for the proof.
///
/// Opens a fresh <see cref="TraceStore"/> connection per call rather than holding one open for the
/// lifetime of this object - the same per-call-open, dispose-immediately idiom
/// <c>ProjectService</c> already uses for <see cref="Graph.GraphDatabase"/> and
/// <see cref="Memory.MemoryIndex"/> (see its OpenGraph/OpenMemoryIndex). It is also the only way a
/// failure to even OPEN the store - an unwritable path - ends up inside this class's own safety
/// net rather than escaping before a <see cref="ToolCallTracer"/> exists to catch it: constructing
/// this class never touches the database at all.
/// </summary>
public sealed class ToolCallTracer(string databasePath)
{
    /// <summary>
    /// Times <paramref name="call"/>, records its outcome, and returns (or rethrows) exactly what
    /// <paramref name="call"/> produced. The only reason recording can be reasoned about as safe:
    /// everything from the tracer's own bookkeeping runs strictly after <paramref name="call"/>
    /// has already produced its result or exception, in a region that never rethrows anything of
    /// its own.
    /// </summary>
    /// <param name="toolName">Recorded as the trace's root span name.</param>
    /// <param name="argumentsJson">The call's arguments, pre-serialised to JSON text by the
    /// caller, or null. Recorded verbatim - see <see cref="ToolCallOutcome.ArgumentsJson"/>.</param>
    /// <param name="call">The tool call itself.</param>
    public T Trace<T>(string toolName, string? argumentsJson, Func<T> call)
    {
        var startUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stopwatch = Stopwatch.StartNew();

        T result;
        try
        {
            result = call();
        }
        catch (Exception ex)
        {
            RecordSafely(toolName, argumentsJson, startUtcMs, stopwatch.ElapsedMilliseconds,
                ok: false, ex.Message, result: null);
            throw;
        }

        RecordSafely(toolName, argumentsJson, startUtcMs, stopwatch.ElapsedMilliseconds,
            ok: true, errorMessage: null, result);
        return result;
    }

    /// <summary>
    /// Records a call's outcome that the caller already determined for itself, rather than one
    /// this tracer observed by invoking the call directly. Exists for the MCP server: a tool
    /// failure there is a normal, non-throwing <c>CallToolResult</c> with <c>IsError = true</c>,
    /// not a thrown exception (the SDK converts a tool's exception before it reaches a request
    /// filter), so there is nothing for <see cref="Trace{T}"/>'s own try/catch to observe. Same
    /// never-fails guarantee as <see cref="Trace{T}"/> - see the class doc comment.
    /// </summary>
    public void RecordOutcome(string toolName, string? argumentsJson, long startUtcMs, long durationMs,
        bool ok, string? errorMessage, object? result = null) =>
        RecordSafely(toolName, argumentsJson, startUtcMs, durationMs, ok, errorMessage, result);

    /// <summary>
    /// The entire safety net lives here: nothing this method does is allowed to escape it,
    /// deliberately including exception types the rest of this codebase does not normally catch
    /// broadly (see this class's doc comment). An operation a caller actually asked for can have a
    /// real reason to let an unusual exception type surface rather than swallow it; tracing never
    /// has that reason, because it is not the operation the caller asked for, only a bystander to
    /// it - see <see cref="Hades.Core.Observation.ObservationService.Sync"/> for that contrast from
    /// the other side: it now ALSO catches unconditionally, but for its own reason (a background
    /// thread's unhandled exception is process death for every project, not just a failed trace),
    /// not this method's "nothing here was ever the caller's request" one.
    /// </summary>
    void RecordSafely(string toolName, string? argumentsJson, long startUtcMs, long durationMs, bool ok,
        string? errorMessage, object? result)
    {
        try
        {
            string? resultType = null;
            long? resultSizeBytes = null;

            if (result is not null)
            {
                resultType = result.GetType().Name;
                resultSizeBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result));
            }

            using var store = TraceStore.Open(databasePath);
            store.RecordToolCall(new ToolCallOutcome
            {
                ToolName = toolName,
                StartUtcMs = startUtcMs,
                EndUtcMs = startUtcMs + durationMs,
                Status = ok ? "ok" : "error",
                ErrorMessage = errorMessage,
                ArgumentsJson = argumentsJson,
                ResultType = resultType,
                ResultSizeBytes = resultSizeBytes,
            });
        }
        catch
        {
            // Tracing must never fail the call it traces - see class doc comment. Deliberately
            // unconditional: an unwritable path throws InvalidOperationException (WAL refused) or
            // a SqliteException, neither of which is IOException/UnauthorizedAccessException, so
            // the narrower "catch (Exception ex) when (ex is IOException or ...)" pattern used
            // elsewhere in this codebase would not actually cover the case this exists for.
        }
    }
}
