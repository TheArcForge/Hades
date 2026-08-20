using Hades.Contract.Wire;
using Hades.Core.Projects;
using ModelContextProtocol;

namespace Hades.Core.Editors;

/// <summary>Tri-state answer to "did the interrupted mutation actually apply" - see
/// <see cref="AckGapVerifier"/> and <see cref="EditorProxy.SendCommandAsync"/>'s class doc
/// comment for the ack gap (spec #2 §5.4) this resolves.</summary>
public enum AckGapOutcome
{
    /// <summary>Looked, and confirmed the mutation took effect.</summary>
    Applied,

    /// <summary>Looked, and confirmed it did NOT take effect - safe to retry.</summary>
    NotApplied,

    /// <summary>Looked (or could not look at all) and still cannot tell either way.</summary>
    Ambiguous,
}

/// <summary>One verification attempt's outcome - what an <see cref="AckGapVerifier"/> returns.</summary>
public sealed record AckGapVerification
{
    public required AckGapOutcome Outcome { get; init; }

    /// <summary>Only consulted when <see cref="Outcome"/> is <see cref="AckGapOutcome.Applied"/> -
    /// a reconstructed stand-in for the plugin's own lost response, so a caller's ordinary
    /// result-mapping code has something to read. Leave null to let
    /// <see cref="EditorProxy.SendCommandAsync"/> substitute a minimal marker object; either way,
    /// SendCommandAsync always stamps its own verified-not-acknowledged note on top of whatever
    /// comes back here.</summary>
    public JsonValue? Result { get; init; }
}

/// <summary>
/// A tool-specific strategy for resolving the ack gap (spec #2 §5.4) - invoked by
/// <see cref="EditorProxy.SendCommandAsync"/> ONLY when the request to the plugin was interrupted
/// by the connection dropping mid-flight, after a genuine response (success, plugin error, or
/// timeout) has already been ruled out. Receives the SAME <see cref="ProjectService"/>
/// SendCommandAsync itself uses - never a second, divergent instance - and the already-resolved
/// productGuid, so a verifier never needs its own copy of either. "Verify, don't bookkeep": a
/// typical verifier calls <see cref="ProjectService.SyncChanges"/> then
/// <see cref="ProjectService.Search"/>/<see cref="ProjectService.FindReferencesTo"/> to re-index
/// and look at the affected asset, or - for a file just written, before any reindex - reads it
/// directly via <see cref="Reading.ReadThrough"/>. Whichever it uses, it looks at project state;
/// it never consults or maintains a record of what was previously sent.
/// </summary>
public delegate Task<AckGapVerification> AckGapVerifier(ProjectService projects, string productGuid, CancellationToken cancellationToken);

/// <summary>
/// The one path every Editor-dependent MCP tool takes to reach the plugin: resolve a project
/// handle, find its attached Editor, send one JSON-RPC command, and map whatever comes back (or
/// doesn't) onto an <see cref="McpException"/> a tool caller can act on. Fifty-two tools (the "52
/// Editor tools" plan) all go through here instead of each re-implementing this - fifty-two
/// copies of this logic would be fifty-two chances for it to diverge.
///
/// Every user-facing failure mode maps to <see cref="McpException"/>, the same convention
/// <see cref="ProjectResolver.Resolve"/> (and, through it, every existing MCP tool via
/// <c>Hades.Server.Mcp.ToolSupport.ResolveProject</c>) already uses: the MCP SDK turns a thrown
/// McpException into a clean, non-throwing tool-call error, so this is what makes a failure here
/// read the same way to a model as any other tool's failure. A null or blank <c>method</c> is the
/// one exception: that is this codebase calling itself incorrectly, not a runtime condition a
/// caller needs to react to, so it throws a plain <see cref="ArgumentException"/> instead - same
/// convention <see cref="EditorSession.SendRequestAsync"/> already uses for the same argument.
///
/// Not-attached vs busy vs a real response is decided by
/// <see cref="ProjectService.GetCharonStatus"/> BEFORE the actual command is ever sent - the exact
/// three-state distinction <c>hades_charon_status</c> itself reports (see the release-paths /
/// visibility plan), consumed here rather than re-implemented. A second, slightly different busy
/// probe would be one more way for the two to disagree about whether Unity is actually responsive
/// right now, and the whole point of that three-state distinction was for exactly this proxy to
/// consume it.
///
/// <para><b>The ack gap (spec #2 §5.4).</b> The plugin can execute a mutation, write its response,
/// and have the connection die before this call reads it - a domain reload's exact failure shape,
/// and mutations frequently trigger one. That surfaces here as <see cref="IOException"/> (the
/// pending request fails once the read loop notices the peer is gone - see
/// <c>EditorSession.FailAllPending</c>) or <see cref="ObjectDisposedException"/> (a session already
/// torn down throws straight out of <c>EditorSession.WriteLine</c> on the next send - the shape
/// measured directly against a live Editor). Retrying blindly could double-apply; failing outright
/// could report a false negative on work that actually landed. So: <see cref="SendCommandAsync"/>
/// resolves this by VERIFICATION, not bookkeeping - an optional caller-supplied
/// <see cref="AckGapVerifier"/> re-checks project state through <see cref="ProjectService"/>, the
/// same façade every other query already goes through. No idempotency ledger, no request-id replay
/// table, no pending-mutation journal exists here or anywhere in this class: there is nothing to
/// remember, because the app can always just look. A caller that supplies no verifier (every one of
/// the 52 tools, as of this writing) - or whose verifier itself cannot decide - gets the same honest
/// answer: "interrupted, state unverified, re-query before retrying".</para>
/// </summary>
public sealed class EditorProxy(ProjectService projects, EditorRegistry registry)
{
    /// <summary>
    /// How long <see cref="SendCommandAsync"/> waits for the plugin's answer to the ACTUAL
    /// command, once <see cref="ProjectService.GetCharonStatus"/> has already confirmed the Editor
    /// is attached and not busy. Deliberately independent of
    /// <see cref="ProjectService.CharonProbeTimeout"/>, which bounds only the earlier busy probe -
    /// a command legitimately allowed to run long (an asset import, a test run) must not share a
    /// budget with a probe meant to answer in well under a second.
    /// </summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Resolves <paramref name="project"/> via the synchronous, non-roots-aware
    /// <see cref="ProjectResolver.Resolve"/> - an explicit handle, or the sole known project when
    /// exactly one is known; anything else (several known projects, no handle) throws the ordinary
    /// "needs a 'project' argument" <see cref="McpException"/>, since this call alone has no way to
    /// ask a live MCP session's roots for the answer. A caller that wants THAT disambiguation must
    /// resolve first, via <c>Hades.Server.Mcp.ToolSupport.ResolveProjectAsync</c> (roots-capable,
    /// but needs the live <c>RequestContext</c> this call does not have), and pass the
    /// already-resolved productGuid here as <paramref name="project"/> instead of a raw handle -
    /// <see cref="ProjectResolver.Resolve"/> accepts a productGuid exactly as readily as a typed
    /// handle (see its own <c>byId</c> branch), so passing one through needs no separate "already
    /// resolved" mode on this side. Confirms an Editor is attached and responsive, and sends
    /// <paramref name="method"/> to it - returning the plugin's raw result. Throws
    /// <see cref="McpException"/> for every runtime failure: an unresolvable project handle (same
    /// wording as any other tool), no Editor attached, an Editor that is attached but busy, the
    /// plugin's own reported error, this call's own timeout, or - see the class doc comment's "ack
    /// gap" section - a connection that dropped before the response arrived, which is resolved via
    /// <paramref name="verifyIfInterrupted"/> when the caller supplies one.
    /// </summary>
    /// <param name="verifyIfInterrupted">Consulted ONLY on the ack gap (the connection dropped
    /// mid-flight, after a real response was ruled out) - never on a clean success, a plugin error,
    /// or a plain timeout with the connection still alive. Omit when a tool has no sensible way to
    /// check whether it applied (see the class doc comment); every caller that omits it gets the
    /// same honest "interrupted, state unverified, re-query before retrying" answer that an
    /// inconclusive verifier would also produce.</param>
    public async Task<JsonValue> SendCommandAsync(string? project, string method, JsonValue? @params = null,
        AckGapVerifier? verifyIfInterrupted = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var productGuid = ProjectResolver.Resolve(projects, project);

        var status = await projects.GetCharonStatus(productGuid).ConfigureAwait(false);
        if (status is null || !status.Attached) throw NotAttachedError(productGuid, method);
        if (status.Busy) throw BusyError(productGuid, status, method);

        // GetCharonStatus just confirmed a registration exists and answered a probe - but a
        // disconnect can still land in the gap between that call returning and this one running.
        // From this call's point of view that is indistinguishable from "never attached", so it
        // gets the same error rather than a bespoke, rarely-seen third message.
        var editor = registry.Get(productGuid) ?? throw NotAttachedError(productGuid, method);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(CommandTimeout);

        JsonRpcResponse response;
        try
        {
            response = await editor.Session.SendRequestAsync(method, @params, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw TimeoutError(method, @params);
        }
        catch (Exception e) when (IsConnectionDrop(e))
        {
            return await ResolveAckGapAsync(method, productGuid, verifyIfInterrupted, e, cancellationToken).ConfigureAwait(false);
        }

        if (response.IsError)
        {
            // The plugin's own message, not a wrapped stack trace - see HadesClient.DescribeFailure
            // on the plugin side, which is what puts a thrown exception's Message (nothing more)
            // into response.Error.Message in the first place.
            throw new McpException($"'{method}' failed: {response.Error!.Message}");
        }

        return response.Result ?? JsonValue.Null;
    }

    /// <summary>The transport-failure shape a dropped connection actually takes - see
    /// <c>EditorSession.FailAllPending</c> (an <see cref="IOException"/>, raised once the receive
    /// loop notices the peer is gone, after the request was already pending) and
    /// <c>EditorSession.WriteLine</c> (whatever the underlying stream throws writing to an
    /// already-dead connection - typically <see cref="IOException"/> or, measured directly against
    /// a live Editor, <see cref="ObjectDisposedException"/> when the session itself was already
    /// torn down). Deliberately narrower than a bare <c>catch (Exception)</c>: only a transport-level
    /// failure means "we do not know what happened to the request", so only these two are treated
    /// as an ack gap - anything else (a plugin bug throwing something stranger) still surfaces as an
    /// ordinary uncaught failure rather than being quietly reinterpreted as one.</summary>
    static bool IsConnectionDrop(Exception e) => e is IOException or ObjectDisposedException;

    /// <summary>
    /// The ack gap itself (spec #2 §5.4): <paramref name="cause"/> is why this call cannot tell
    /// whether <paramref name="method"/> ran. With no verifier, or one that cannot decide, the
    /// honest answer is returned - never a blind retry, never a blind failure. See the class doc
    /// comment for why nothing here remembers <paramref name="method"/> or its params once this
    /// returns: the next identical ack gap re-verifies from scratch, every time.
    /// </summary>
    async Task<JsonValue> ResolveAckGapAsync(string method, string productGuid, AckGapVerifier? verify, Exception cause,
        CancellationToken cancellationToken)
    {
        if (verify is null) throw AmbiguousError(method, cause, verifyError: null);

        AckGapVerification verification;
        try
        {
            verification = await verify(projects, productGuid, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception verifyError)
        {
            // A verifier that cannot even run (e.g. it also needs the Editor and finds none) is
            // exactly as unable to decide as no verifier at all - the same honest answer, with the
            // verifier's own failure folded in for whoever reads the error.
            throw AmbiguousError(method, cause, verifyError);
        }

        switch (verification.Outcome)
        {
            case AckGapOutcome.Applied:
                var result = verification.Result ?? JsonValue.NewObject();
                result.SetProperty("hadesVerifiedNotAcknowledged", JsonValue.Bool(true));
                result.SetProperty("hadesVerificationNote",
                    JsonValue.String($"'{method}' applied, but the Unity Editor's own acknowledgement never "
                        + "arrived - the connection dropped first. This result was VERIFIED by checking "
                        + "project state afterward, not acknowledged directly by the plugin."));
                return result;

            case AckGapOutcome.NotApplied:
                throw new McpException(
                    $"'{method}' was interrupted before the Unity Editor responded, and checking project "
                    + "state afterward confirmed it did NOT take effect. Safe to retry.");

            default:
                throw AmbiguousError(method, cause, verifyError: null);
        }
    }

    McpException AmbiguousError(string method, Exception cause, Exception? verifyError)
    {
        var detail = verifyError is null
            ? cause.Message
            : $"{cause.Message}; the verification attempt also failed: {verifyError.Message}";

        return new McpException(
            $"'{method}' was interrupted: the Unity Editor's connection dropped before its response arrived "
            + $"({detail}). Whether it applied cannot be determined right now - interrupted, state "
            + "unverified, re-query before retrying.");
    }

    /// <summary>
    /// Unlike <see cref="AmbiguousError"/>, this is NOT a dropped connection -
    /// <see cref="ProjectService.GetCharonStatus"/> already confirmed the Editor was attached and
    /// responsive moments earlier, so Unity's main thread is almost certainly still working through
    /// <paramref name="method"/> synchronously, exactly as measured against a real Editor: a batch that
    /// outruns <see cref="CommandTimeout"/> keeps applying operations long after this exception is
    /// thrown. Giving up on the wait must not be misread as Unity giving up on the work, so the message
    /// says plainly that nothing already applied is undone and more may still land, rather than leaving
    /// a caller to assume a timeout means nothing happened.
    /// </summary>
    McpException TimeoutError(string method, JsonValue? @params)
    {
        // Every batch tool (material_apply, scene_apply, prefab_apply, animation_apply,
        // project_settings_apply - see e.g. MaterialApplyTool.MaterialApply) sends its whole spec
        // as one 'operations' array under this exact key, so this is the one place EditorProxy can
        // learn how big the interrupted batch was without knowing which specific tool called it.
        // Absent for single-shot commands (project_run_tests, assets.refresh, ...) - the count is
        // simply omitted rather than guessed.
        var operationCount = @params is not null
            && @params.TryGetProperty("operations", out var operations)
            && operations!.Kind == JsonValueKind.Array
                ? operations.Items.Count
                : (int?)null;
        var subject = operationCount is { } count ? $"'{method}' ({count} operation(s))" : $"'{method}'";

        return new McpException(
            $"{subject} timed out after {CommandTimeout.TotalSeconds:F0}s waiting for the Unity Editor to "
            + "respond - the Editor may still be executing it, since giving up on the wait does not cancel "
            + "the work. Anything already applied is NOT rolled back, and more may land after this error "
            + "returns. Call hades_charon_status, which will keep reporting the Editor busy until the work "
            + "actually finishes, then check project_get_console_log or re-query the affected paths to see "
            + "what has actually landed before retrying.");
    }

    string ProjectName(string productGuid) => projects.Get(productGuid)?.Name ?? productGuid;

    McpException NotAttachedError(string productGuid, string method) => new(
        $"'{method}' needs a Unity Editor, but none is attached to project '{ProjectName(productGuid)}'. "
        + "Call hades_charon_status to check connection state before retrying.");

    McpException BusyError(string productGuid, CharonStatus status, string method) => new(
        $"'{method}' needs a Unity Editor. Unity {status.UnityVersion} is attached to project "
        + $"'{ProjectName(productGuid)}' (pid {status.ProcessId}) but busy - its main thread has not "
        + "answered within the probe window, likely mid-compile, importing assets, or blocked on a "
        + "long-running operation. The connection itself is alive; this is not a disconnect. Call "
        + "hades_charon_status for details, or retry shortly.");
}
