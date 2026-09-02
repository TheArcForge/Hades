namespace Hades.Shell.ViewModels;

public enum SequenceCallsFetchKind
{
    /// <summary>No sequence selected - the ordinary starting state.</summary>
    NotSelected,

    /// <summary>Fetching the per-call detail. Shown because it is N requests, not one.</summary>
    Loading,

    Loaded,

    /// <summary>The server's own message.</summary>
    Failed,
}

/// <summary>
/// The selected sequence's per-call breakdown. Separate from
/// <see cref="TraceDetailFetchState"/> because they are two different levels of the same drill-down
/// and both are on screen at once: this is "which calls does this sequence contain", that is "what
/// spans does one call contain".
///
/// <para>It has a <see cref="SequenceCallsFetchKind.Loading"/> state where the single-call fetch does
/// not, and that is deliberate: this resolves one request PER CALL, so a nineteen-call sequence is
/// nineteen round trips. Fast on localhost, but not instant, and a pane that sat blank meanwhile
/// would look like a sequence with no calls - which is the exact confusion this whole view is being
/// fixed to end.</para>
/// </summary>
public readonly record struct SequenceCallsFetchState
{
    public SequenceCallsFetchKind Kind { get; }
    public IReadOnlyList<SequenceCallRow> Calls { get; }
    public string? Message { get; }

    SequenceCallsFetchState(SequenceCallsFetchKind kind, IReadOnlyList<SequenceCallRow> calls, string? message)
    {
        Kind = kind;
        Calls = calls;
        Message = message;
    }

    public static SequenceCallsFetchState NotSelected { get; } =
        new(SequenceCallsFetchKind.NotSelected, [], null);

    public static SequenceCallsFetchState Loading { get; } =
        new(SequenceCallsFetchKind.Loading, [], null);

    public static SequenceCallsFetchState Loaded(IReadOnlyList<SequenceCallRow> calls) =>
        new(SequenceCallsFetchKind.Loaded, calls, null);

    public static SequenceCallsFetchState Failed(string message) =>
        new(SequenceCallsFetchKind.Failed, [], message);
}
