using Hades.Control.Client.Dtos;

namespace Hades.Shell.ViewModels;

public enum TraceDetailFetchKind
{
    /// <summary>No call selected yet - the ordinary starting state, not an error.</summary>
    NotSelected,

    /// <summary>The full span detail for the most recently selected call.</summary>
    Loaded,

    /// <summary>The fetch answered with a server error; the message is the server's own.</summary>
    Failed,
}

/// <summary>
/// One selected call's span-detail fetch state. The port of
/// <c>Mac/HadesApp/Sources/HadesApp/MainWindow/TraceDetailFetchState.swift</c>.
///
/// Only <see cref="TracesViewModel.SelectTraceAsync"/> changes this, and nothing polls it: a traceId
/// the user clicked is a fixed historical record, not a live value that changes tick to tick the way
/// a running rebuild's <see cref="OperationProgress"/> does.
///
/// <see cref="TraceDetailFetchKind.Failed"/> covers a 404 ("Unknown trace '{traceId}'.") the way
/// <see cref="OperationProgressKind.Pruned"/> covers an unknown operation id - the server's own
/// message, verbatim. Unlike a pruned operation it is NOT framed as an ordinary outcome: there is no
/// retention-window story here, so the message text itself is the only thing that decides how it
/// reads, and this renders it exactly as sent either way.
/// </summary>
public readonly record struct TraceDetailFetchState
{
    public TraceDetailFetchKind Kind { get; }

    /// <summary>Set when <see cref="Kind"/> is Loaded.</summary>
    public TraceDetailResult? Detail { get; }

    /// <summary>Set when <see cref="Kind"/> is Failed. The server's own text.</summary>
    public string? Message { get; }

    TraceDetailFetchState(TraceDetailFetchKind kind, TraceDetailResult? detail, string? message)
    {
        Kind = kind;
        Detail = detail;
        Message = message;
    }

    public static readonly TraceDetailFetchState NotSelected =
        new(TraceDetailFetchKind.NotSelected, null, null);

    public static TraceDetailFetchState Loaded(TraceDetailResult detail) =>
        new(TraceDetailFetchKind.Loaded, detail, null);

    public static TraceDetailFetchState Failed(string message) =>
        new(TraceDetailFetchKind.Failed, null, message);
}
