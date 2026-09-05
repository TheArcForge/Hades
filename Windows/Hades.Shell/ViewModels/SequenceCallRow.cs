using Hades.Control.Client.Dtos;

namespace Hades.Shell.ViewModels;

/// <summary>
/// One call inside a selected sequence, resolved far enough to be READ rather than merely clicked.
///
/// <para><b>Why this type exists.</b> A sequence row carries only parallel <c>Tools</c>/<c>TraceIds</c>
/// arrays - no per-call outcome, duration or start time. So a list built from a sequence alone can
/// show names and nothing else: it cannot say which call failed, how long any of them took, or where
/// in the sequence they happened. Those come from <c>GET /control/traces/{traceId}</c>, one fetch per
/// call, which <see cref="TracesViewModel.SelectSequenceAsync"/> does once on selection.</para>
///
/// <para><see cref="OffsetMs"/> is computed here rather than served: it is this call's start minus
/// the SEQUENCE's start, which is a presentation question ("where in this sequence did it happen")
/// the API has no opinion about. Every other field is the core's own.</para>
/// </summary>
/// <param name="Position">1-based, matching how the sequence reads left to right.</param>
/// <param name="Tool">The core's own tool name, verbatim.</param>
/// <param name="TraceId">What span detail is fetched with.</param>
/// <param name="Outcome">This call's own outcome - the whole point of the row.</param>
/// <param name="OffsetMs">Milliseconds after the sequence started.</param>
/// <param name="DurationMs">How long this call took, or null when the core did not record one -
/// carried through as null rather than coerced to zero, which would claim a measurement that was
/// never taken. The duration converter renders it as blank.</param>
public sealed record SequenceCallRow(
    int Position,
    string Tool,
    string TraceId,
    TraceOutcome Outcome,
    long OffsetMs,
    long? DurationMs);
