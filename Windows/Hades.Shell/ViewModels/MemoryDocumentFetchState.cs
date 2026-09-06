using Hades.Control.Client.Dtos;

namespace Hades.Shell.ViewModels;

public enum MemoryDocumentFetchKind
{
    /// <summary>No document selected yet - the ordinary starting state, not an error.</summary>
    NotSelected,

    /// <summary>The full raw content for the most recently selected document.</summary>
    Loaded,

    /// <summary>The fetch answered with a server error; the message is the server's own.</summary>
    Failed,
}

/// <summary>
/// One selected document's fetch state. The port of
/// <c>Mac/HadesApp/Sources/HadesApp/MainWindow/MemoryDocumentFetchState.swift</c>, and the same shape
/// <see cref="TraceDetailFetchState"/> already established.
///
/// Only <see cref="MemoryViewModel.SelectDocumentAsync"/> changes this, and nothing polls it: a
/// document opened to read or edit is a fixed snapshot for as long as someone is looking at it, not
/// a live value a tick should silently overwrite out from under an in-progress edit.
/// </summary>
public readonly record struct MemoryDocumentFetchState
{
    public MemoryDocumentFetchKind Kind { get; }

    /// <summary>Set when <see cref="Kind"/> is Loaded.</summary>
    public MemoryDocumentResult? Document { get; }

    /// <summary>Set when <see cref="Kind"/> is Failed - e.g. "'{name}' does not exist yet.",
    /// verbatim.</summary>
    public string? Message { get; }

    MemoryDocumentFetchState(MemoryDocumentFetchKind kind, MemoryDocumentResult? document, string? message)
    {
        Kind = kind;
        Document = document;
        Message = message;
    }

    public static readonly MemoryDocumentFetchState NotSelected =
        new(MemoryDocumentFetchKind.NotSelected, null, null);

    public static MemoryDocumentFetchState Loaded(MemoryDocumentResult document) =>
        new(MemoryDocumentFetchKind.Loaded, document, null);

    public static MemoryDocumentFetchState Failed(string message) =>
        new(MemoryDocumentFetchKind.Failed, null, message);
}
