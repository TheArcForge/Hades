using System.Text.Json.Serialization;

namespace Hades.Server.Control;

/// <summary>One long-running control-API action's state - see <see cref="OperationRegistry"/>'s
/// own class doc comment. The shell maps this straight to a spinner/checkmark/error icon and does
/// nothing else, same rule as <see cref="ControlIconState"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OperationState
{
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("done")] Done,
    [JsonStringEnumMemberName("failed")] Failed,
}

/// <summary>
/// One operation as <see cref="OperationRegistry"/> holds it internally - never serialized
/// directly (see <see cref="OperationResult"/>, the wire shape <see cref="Operations.Get"/> maps
/// this to, matching every other endpoint's own plain-internal-model/wire-DTO split).
/// </summary>
public sealed record OperationRecord
{
    public required string Id { get; init; }

    /// <summary>What kind of long action this is - "rebuild" today, the only one Plan 11 wires up.
    /// A plain string, not a closed enum, so a future long action (e.g. a bulk import) needs no
    /// change here to add a new value - same reservation pattern as <see cref="ProjectWarning.Code"/>.</summary>
    public required string Kind { get; init; }

    public required OperationState State { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? FinishedAtUtc { get; init; }

    /// <summary>Human-readable progress, when known. Nothing populates this today - no long
    /// operation Plan 11 wires up (just <c>rebuild</c>) has a finer-grained signal than
    /// running/done - but the field is real, not decorative: a future operation that DOES report
    /// incremental progress (e.g. "120 of 400 files") populates it with no wire-shape change.</summary>
    public string? Progress { get; init; }

    /// <summary>Set only when <see cref="State"/> is <see cref="OperationState.Failed"/> - the
    /// triggering exception's own message, held to the same "specific and actionable" standard
    /// every MCP tool's error text already is (see <see cref="OperationRegistry.Start"/>).</summary>
    public string? Error { get; init; }

    /// <summary>The operation's own resolved payload on success - e.g. <c>rebuild</c>'s node
    /// counts (see ProjectsEndpoint's own RebuildOperationResult). Whatever CLR object is stored
    /// here is serialized as-is by <see cref="Operations.Get"/>'s caller, so it must already be a
    /// wire-shaped type (JsonPropertyName-decorated), never a raw Hades.Core record - same
    /// discipline every other Control response follows.</summary>
    public object? Result { get; init; }
}

/// <summary>The wire shape of <c>GET /control/operations/{id}</c> - see <see cref="Operations"/>'s
/// own class doc comment.</summary>
public sealed record OperationResult
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("state")] public required OperationState State { get; init; }
    [JsonPropertyName("startedAtUtc")] public required DateTimeOffset StartedAtUtc { get; init; }
    [JsonPropertyName("finishedAtUtc")] public DateTimeOffset? FinishedAtUtc { get; init; }

    /// <summary>Plan 11 Task 7 audit fix: <see cref="StartedAtUtc"/>/<see cref="FinishedAtUtc"/>
    /// alone forced a shell showing progress ("running for Xs") to subtract a raw timestamp from
    /// "now" itself - exactly the violation TraceSequenceRow's own durationMs already avoids at the
    /// trace level (see that type's own doc comment). Resolved by <see cref="Operations.Get"/>:
    /// <c>(FinishedAtUtc ?? now) - StartedAtUtc</c>, whole seconds - grows while running, frozen the
    /// instant the operation completes.</summary>
    [JsonPropertyName("elapsedSeconds")] public required int ElapsedSeconds { get; init; }

    [JsonPropertyName("progress")] public string? Progress { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("result")] public object? Result { get; init; }
}

/// <summary>
/// In-memory tracker for long-running control-API actions - today, only <c>POST
/// /control/projects/{id}/rebuild</c> (see <see cref="Hades.Server.Control.ProjectsEndpoint.Rebuild"/>),
/// which used to return a freshly-minted <see cref="Guid"/> backed by nothing: the id went nowhere,
/// because <c>rebuild</c> ran on a bare, un-awaited <see cref="Task.Run(Action)"/> with no store
/// behind it (Plan 11 Task 3's own "design decisions" note). This closes that gap: <see cref="Start"/>
/// registers the work, runs it on a background task, and records where it landed so
/// <see cref="Get"/> (and, over HTTP, <c>GET /control/operations/{id}</c> - see
/// <see cref="Operations"/>) can answer honestly at any point afterward.
///
/// <b>Retention: 5 minutes past completion</b> (<see cref="DefaultRetention"/>). Long enough that a
/// shell polling at the ~1Hz the plan settles on (spec #3's SSE-is-a-later-optimisation stance)
/// comfortably survives a brief hiccup - the app backgrounded for a few seconds, a network blip -
/// and still gets a coherent final answer instead of a sudden, unexplained 404; short enough that a
/// long-running app session accumulates at most a few completed records at a time, never an
/// unbounded backlog. A RUNNING operation is never pruned regardless of age - only completion
/// starts the clock.
///
/// <b>Pruning is opportunistic, not a background timer</b> - swept once at the top of every
/// <see cref="Start"/> call, not on its own schedule. This is a deliberate, narrower trade-off than
/// <see cref="Hades.Core.Tracing.TraceStore"/>'s own dedicated retention <see cref="System.Threading.Timer"/>
/// (see Program.cs): traces persist across process restarts and accumulate regardless of whether
/// anyone is looking, so they need their own clock. Operations are purely in-memory, cheap (a
/// handful of strings each), and only ever created by a user-initiated action (today, clicking
/// "rebuild") - a session with no new operations for a while simply keeps its last few completed
/// records around harmlessly rather than needing a dedicated sweep thread to reclaim a few hundred
/// bytes nothing is otherwise threatened by.
///
/// Thread-safe: every method takes the same lock for the duration of its dictionary access, same
/// convention as <see cref="Hades.Core.Editors.LeaseRegistry"/>/<see cref="Hades.Core.Editors.EditorRegistry"/>.
/// </summary>
public sealed class OperationRegistry(Func<DateTimeOffset>? utcNow = null, TimeSpan? retention = null)
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromMinutes(5);

    readonly Dictionary<string, OperationRecord> _operations = [];
    readonly Dictionary<string, Task> _tasks = [];
    readonly Lock _gate = new();
    readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    readonly TimeSpan _retention = retention ?? DefaultRetention;

    /// <summary>
    /// Registers a new operation and returns its id immediately - <paramref name="work"/> runs on
    /// a background <see cref="Task.Run(Action)"/> and is never awaited here, so this call never
    /// blocks on it, the same start-then-poll shape <c>project_run_tests</c>/
    /// <c>project_get_test_results</c> already use for the plugin's own long actions. A synchronous
    /// <see cref="Func{TResult}"/> rather than an async one: the one caller this plan wires up
    /// (<see cref="ProjectService.RebuildGraph"/>) is itself synchronous/blocking, and a future
    /// async long action can still run inside a synchronous wrapper here without this signature
    /// needing to grow a second overload nobody has needed yet.
    /// </summary>
    public string Start(string kind, Func<object?> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(work);

        var id = Guid.NewGuid().ToString();

        lock (_gate)
        {
            PruneExpired();
            _operations[id] = new OperationRecord { Id = id, Kind = kind, State = OperationState.Running, StartedAtUtc = _utcNow() };
        }

        var task = Task.Run(() =>
        {
            try
            {
                var result = work();
                lock (_gate)
                {
                    _operations[id] = _operations[id] with { State = OperationState.Done, FinishedAtUtc = _utcNow(), Result = result };
                }
            }
            catch (Exception ex)
            {
                // The triggering exception's own message, verbatim - the same "specific and
                // actionable" standard every MCP tool's error text is held to (see this class's
                // own doc comment), never a bare "operation failed" the caller cannot act on.
                lock (_gate)
                {
                    _operations[id] = _operations[id] with { State = OperationState.Failed, FinishedAtUtc = _utcNow(), Error = ex.Message };
                }
            }
        });

        lock (_gate) { _tasks[id] = task; }

        return id;
    }

    /// <summary>The operation's current state, or null when <paramref name="id"/> is unknown -
    /// either it never existed, or it completed and was pruned (see this class's own doc comment
    /// on retention). Callers (see <see cref="Operations.Get"/>) turn null into an explicit
    /// "unknown operation" answer, never an empty/ambiguous one.</summary>
    public OperationRecord? Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate) { return _operations.GetValueOrDefault(id); }
    }

    /// <summary>Test-only observability hook: the background <see cref="Task"/> backing
    /// <paramref name="id"/>, so a test can <c>await</c> completion deterministically instead of
    /// polling/sleeping. Not used by any production caller - <see cref="Start"/> is deliberately
    /// fire-and-forget from its own caller's perspective. An unknown id returns a completed task
    /// rather than throwing, so a test racing pruning never faults on the lookup itself.</summary>
    public Task WhenComplete(string id)
    {
        lock (_gate) { return _tasks.GetValueOrDefault(id, Task.CompletedTask); }
    }

    /// <summary>Removes every FINISHED operation older than the retention window - see this
    /// class's own doc comment for why this runs opportunistically here rather than on a timer.
    /// Must be called with <see cref="_gate"/> already held.</summary>
    void PruneExpired()
    {
        var cutoff = _utcNow() - _retention;

        List<string>? expired = null;
        foreach (var (id, op) in _operations)
        {
            if (op.FinishedAtUtc is { } finishedAtUtc && finishedAtUtc < cutoff)
            {
                (expired ??= []).Add(id);
            }
        }

        if (expired is null) return;

        foreach (var id in expired)
        {
            _operations.Remove(id);
            _tasks.Remove(id);
        }
    }
}

/// <summary>
/// <c>GET /control/operations/{id}</c> - the poll side of every long control-API action. Thin on
/// purpose: <see cref="OperationRegistry"/> already holds the resolved state, so this only
/// translates it to the wire shape and to "unknown id" 404, never re-deriving anything.
/// </summary>
public static class Operations
{
    public static IResult Get(OperationRegistry operations, string id, Func<DateTimeOffset> utcNow)
    {
        var op = operations.Get(id);
        if (op is null)
        {
            // "Says so" rather than an empty result the shell would have to interpret as either
            // "finished" or "never existed" (this task's own required property) - same 404-with-a-
            // named-reason convention every unknown-id lookup elsewhere in this API already uses
            // (ProjectsEndpoint.Remove/Rebuild/..., EditorsEndpoint.ReleaseAsync).
            return Results.Json(
                new { error = $"Unknown operation '{id}'. It may have completed and been pruned, or the id is wrong." },
                statusCode: StatusCodes.Status404NotFound);
        }

        var elapsed = (op.FinishedAtUtc ?? utcNow()) - op.StartedAtUtc;

        return Results.Json(new OperationResult
        {
            Id = op.Id,
            Kind = op.Kind,
            State = op.State,
            StartedAtUtc = op.StartedAtUtc,
            FinishedAtUtc = op.FinishedAtUtc,
            ElapsedSeconds = Math.Max(0, (int)Math.Round(elapsed.TotalSeconds)),
            Progress = op.Progress,
            Error = op.Error,
            Result = op.Result,
        });
    }
}
