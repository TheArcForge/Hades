using Hades.Control.Client.Dtos;

namespace Hades.Shell.ViewModels;

public enum OperationProgressKind
{
    /// <summary>The operation is still trackable; <see cref="OperationProgress.Result"/> holds it.</summary>
    Tracked,

    /// <summary>GET /control/operations/{id} answered 404 - see <see cref="OperationProgress"/>.</summary>
    Pruned,
}

/// <summary>
/// One tracked rebuild operation's display state. The port of
/// <c>Mac/HadesApp/Sources/HadesApp/MainWindow/OperationProgress.swift</c>.
///
/// <see cref="OperationProgressKind.Pruned"/> is what an unknown-id 404 becomes, and it is an
/// ORDINARY outcome rather than a failure: the operation registry keeps completed operations for
/// five minutes, so a rebuild that finished a while ago is simply gone. The server's own
/// explanation - "Unknown operation '{id}'. It may have completed and been pruned, or the id is
/// wrong." - is carried verbatim rather than replaced with text papering over the fact that this
/// looks identical to a wrong id.
///
/// A view renders <see cref="Result"/> verbatim: <c>State</c> as an icon only (via
/// <see cref="StatusGlyph"/>, never invented display text), <c>ElapsedSeconds</c> as the whole
/// seconds the core already computed rather than re-derived from the timestamps, and whichever of
/// <c>Progress</c>/<c>Error</c>/<c>Result</c> the current state populated.
/// </summary>
public readonly record struct OperationProgress
{
    public OperationProgressKind Kind { get; }

    /// <summary>Set when <see cref="Kind"/> is Tracked.</summary>
    public OperationResult? Result { get; }

    /// <summary>Set when <see cref="Kind"/> is Pruned. The server's own text.</summary>
    public string? Message { get; }

    OperationProgress(OperationProgressKind kind, OperationResult? result, string? message)
    {
        Kind = kind;
        Result = result;
        Message = message;
    }

    public static OperationProgress Tracked(OperationResult result) =>
        new(OperationProgressKind.Tracked, result, null);

    public static OperationProgress Pruned(string message) =>
        new(OperationProgressKind.Pruned, null, message);
}
