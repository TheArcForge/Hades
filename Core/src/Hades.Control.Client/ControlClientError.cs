namespace Hades.Control.Client;

/// <summary>
/// Every way a control-API call can fail to hand back a decoded DTO. Mirrors Swift's
/// <c>ControlClientError</c> (Mac/HadesControl/Sources/HadesControl/ControlClient.swift) case for
/// case, adapted to C#'s exception-based error handling: Swift's <c>encoding(EncodingError)</c>
/// case has no equivalent here because <see cref="ControlClient"/>, as ported so far, only ever
/// GETs - it sends no request body, so there is nothing for this client to fail to encode.
/// </summary>
public enum ControlClientError
{
    /// <summary>
    /// The token this client presented was rejected (HTTP 401). Deliberately its own case, not
    /// folded into <see cref="Server"/>: a 401 here means specifically that the token this client
    /// was built with is stale, almost always because the core restarted and wrote a fresh
    /// discovery file - the correct, and only, recovery is to re-read the discovery file (see
    /// <c>Discovery.Read</c>) and build a new <see cref="ControlClient"/>. Retrying the same
    /// request with the same token fails identically forever. Making this a distinct case (rather
    /// than a status code every caller has to compare by hand) is what makes it actionable in the
    /// type - the exact reasoning the Swift original's own doc comment gives for keeping this case
    /// separate from a generic <c>.server(status: 401, ...)</c>.
    /// </summary>
    StaleToken,

    /// <summary>
    /// A non-2xx, non-401 response. <see cref="ControlClientException.Message"/> is the server's
    /// own <c>"error"</c> field when the body carried one (every Control endpoint's error
    /// responses do - see <c>ControlAuth</c>, <c>ProjectsEndpoint</c>, <c>EditorsEndpoint</c>),
    /// never text invented client-side.
    /// </summary>
    Server,

    /// <summary>The response body did not decode into the expected DTO shape.</summary>
    Decoding,

    /// <summary>
    /// The request never got a response to check the status of - the core is not running, this
    /// connection's port is stale, the request timed out, and so on.
    /// </summary>
    Transport,
}

/// <summary>
/// Thrown by every <see cref="ControlClient"/> call that fails - carries which of the four
/// <see cref="ControlClientError"/> cases occurred and a human-readable message. Where Swift's
/// typed-throws original returns a `ControlClientError` value the caller must switch over, this
/// port raises one exception type with an <see cref="Error"/> tag: idiomatic C# has no equivalent
/// to Swift's per-function `throws(ControlClientError)` clause, so the case analysis moves from
/// the type system into this single property instead.
/// </summary>
public sealed class ControlClientException : Exception
{
    /// <summary>Which of the four ways this call failed.</summary>
    public ControlClientError Error { get; }

    /// <summary>
    /// The HTTP status, for <see cref="ControlClientError.Server"/> only; null for every other case,
    /// which never had a status to report.
    ///
    /// Swift's original case is <c>.server(status:message:)</c> and callers switch on the status -
    /// this port dropped it, which made one real caller impossible to write: the Projects section
    /// has to tell a 404 ("unknown operation - it may have completed and been pruned") apart from
    /// any other server error, because a pruned operation is an ORDINARY outcome for a rebuild that
    /// finished a while ago, not a failure. Without the status the two are indistinguishable.
    /// </summary>
    public int? StatusCode { get; }

    public ControlClientException(
        ControlClientError error,
        string message,
        Exception? innerException = null,
        int? statusCode = null)
        : base(message, innerException)
    {
        Error = error;
        StatusCode = statusCode;
    }
}
