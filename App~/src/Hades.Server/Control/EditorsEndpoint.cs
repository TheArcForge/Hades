using System.Text.Json.Serialization;
using Hades.Core;
using Hades.Core.Editors;
using ModelContextProtocol;
using WireJson = Hades.Contract.Wire.JsonValue;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Control;

/// <summary>One currently-attached Unity Editor - see <see cref="EditorsEndpoint.Resolve"/>. Only
/// <see cref="ProjectEditorState.Attached"/> and <see cref="ProjectEditorState.Busy"/> ever appear
/// here: an editor that is not attached is not a row at all - "absent" is a fact about a PROJECT
/// (see <see cref="ProjectEditorInfo"/>, which represents that same project row either way), not
/// something a list of live CONNECTIONS can represent. <see cref="UnityVersion"/>/
/// <see cref="ProcessId"/>/<see cref="ConnectionAgeSeconds"/> stay nullable anyway, mirroring
/// <see cref="ProjectEditorInfo"/>'s own fields exactly, rather than asserting a non-null contract
/// this type cannot itself enforce.</summary>
public sealed record EditorRow
{
    [JsonPropertyName("project")] public required string Project { get; init; }
    [JsonPropertyName("productGuid")] public required string ProductGuid { get; init; }
    [JsonPropertyName("state")] public required ProjectEditorState State { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("unityVersion")] public string? UnityVersion { get; init; }
    [JsonPropertyName("processId")] public long? ProcessId { get; init; }
    [JsonPropertyName("connectionAgeSeconds")] public int? ConnectionAgeSeconds { get; init; }
}

/// <summary>The full <c>GET /control/editors</c> response.</summary>
public sealed record EditorsResult
{
    [JsonPropertyName("editors")] public required IReadOnlyList<EditorRow> Editors { get; init; }
}

/// <summary>
/// One known project's live Editor-attachment state, the input <see cref="EditorsEndpoint.Resolve"/>
/// turns into (at most) one <see cref="EditorRow"/>. Same two-layer reasoning as
/// <see cref="SummaryEndpoint"/>'s own <c>ProjectSnapshot</c> and <see cref="ProjectsEndpoint"/>'s
/// own <c>ProjectStateSnapshot</c>: every field here is pre-derived so <see cref="EditorsEndpoint.Resolve"/>
/// can be tested with plain data, no live project/editor state at all.
/// </summary>
public sealed record EditorStateSnapshot
{
    public required string Name { get; init; }
    public required string ProductGuid { get; init; }
    public required bool Attached { get; init; }
    public required bool Busy { get; init; }
    public string? UnityVersion { get; init; }
    public long? ProcessId { get; init; }
    public TimeSpan? ConnectionAge { get; init; }
}

/// <summary>
/// <c>GET /control/editors</c> (attached editors, plan 7's attached/busy distinction) and
/// <c>POST /control/leases/{id}/release</c> (force-release - the Release button behind the menu
/// bar's most prominent row, and the human-cancellable half of the reload-safety design's rule 7)
/// - Plan 11 Task 4.
///
/// <para><b>Two surfaces, one file</b>, because both are "editors and leases": <c>GET /control/editors</c>
/// is read-only and reuses the exact same two-layer split as <see cref="SummaryEndpoint"/>/
/// <see cref="ProjectsEndpoint"/> (<see cref="Resolve"/> pure, <see cref="BuildAsync"/> the async
/// orchestrator reusing <see cref="ProjectService.GetCharonStatus"/> - never a second busy-probe).
/// <see cref="ReleaseAsync"/> is the one action this file adds beyond that pattern, since a release
/// needs to actually reach the plugin, not just read cached state.</para>
///
/// <para><b>Design decisions the plan left to this task:</b></para>
/// <list type="bullet">
/// <item><b>Which editors appear in <c>GET /control/editors</c>.</b> Only ones actually attached
/// (<see cref="ProjectEditorState.Attached"/> or <see cref="ProjectEditorState.Busy"/>) - matching
/// the plan's own wording, "lists ATTACHED editors". A project with no Editor simply is not a row,
/// the same "only what fired" convention <see cref="ProjectsEndpoint.BuildWarnings"/> already uses
/// for warnings that do not apply - not an "absent" placeholder entry to filter out client-side,
/// which would be exactly the client-side computation this whole API exists to avoid.</item>
///
/// <item><b>How the client names "which lease to release" (the question Task 2 explicitly left
/// open).</b> <c>POST /control/leases/{id}/release</c>'s <c>{id}</c>, and the new
/// <c>SummaryLease.LeaseId</c> field this task adds to <c>/control/summary</c> (see that file's own
/// updated doc comment), are BOTH the holding project's <b>productGuid</b> - deliberately NOT the
/// plugin's own wire-level lease id. Two reasons, together conclusive: (1) the plugin only ever
/// hands out ONE constant lease id today (<c>ProjectCommands.ScriptEditingLeaseId</c>,
/// "hades-script-editing" - confirmed by reading Plugin~/Assets/Hades/Tools/ProjectCommands.cs
/// directly), so it cannot disambiguate WHICH project's lease a client means; (2)
/// <c>Runtime.ReloadGate</c> allows at most one held lease per Editor/project (confirmed by reading
/// Plugin~/Assets/Hades/Runtime/ReloadGate.cs directly - <c>Acquire</c> for a different id while one
/// is held is rejected, never queued), and <see cref="LeaseRegistry"/> already keys its own believed
/// state by productGuid for exactly that reason - so productGuid is already a correct, sufficient,
/// per-lease identity with no second id space to invent or keep in sync. The route parameter is
/// still named <c>{id}</c>, matching the plan's own literal route text and reading naturally as "the
/// identifier of the lease resource this action addresses" - a resource's id does not have to be a
/// foreign, unrelated token when a natural key already uniquely names it.</item>
///
/// <item><b><see cref="SummaryLease.Releasable"/> is CONFIRMED, not refined.</b> Task 2's own note
/// asked this task to confirm or refine "true only when <see cref="Hades.Core.Editors.EditorRegistry"/>
/// has a live session" (i.e. <c>Attached</c>, regardless of <c>Busy</c>). Kept exactly as Task 2
/// left it: a busy Editor still has a live, reachable TCP session (busy means the last probe timed
/// out, not that the socket is gone), so Task 2's own stated test - "a session the release endpoint
/// COULD reach" - already holds even when busy. Gating the button on "not busy" too would make it
/// flicker disabled during ordinary transient load, which is the opposite of net #7's own
/// requirement that this be the reliable, always-available human escape valve; a momentarily-busy
/// release attempt still fails cleanly and informatively (see <see cref="ReleaseAsync"/> below), it
/// just does not pretend in advance that busy makes the button useless.</item>
///
/// <item><b><see cref="ReleaseAsync"/> reuses <see cref="EditorProxy.SendCommandAsync"/> wholesale</b>
/// - the exact "one path every Editor-dependent call takes to reach the plugin" every one of the 52
/// Editor tools already goes through (see that class's own doc comment) - rather than re-implementing
/// attached/busy detection, the command timeout, or ack-gap handling a second time here. This is
/// also what "reuse EditorSession/EditorProxy... do not add a second release path" (the plan's own
/// constraint) means concretely: the wire method sent is ALWAYS literally <c>"lease.release"</c>
/// (see EditorsSourceScanTests in the test project, which proves this AND that no
/// Lock/UnlockReloadAssemblies call site exists anywhere outside Plugin~'s own IEditorLockApi.cs).
/// <see cref="EditorProxy"/> has no typed wrapper for lease.release specifically (only
/// <see cref="EditorSession.ReleaseLeaseAsync"/> does, which EditorProxy itself never calls for ANY
/// method) - so the tiny <c>{"leaseId": "..."}</c> params object and the <c>{success, leaseId,
/// expiresAtUtcMs}</c> result are built/read inline here, the same shape
/// <c>EditorSession.SendLeaseRequestAsync</c> uses privately for every lease.* call. Deliberately NO
/// separate not-attached/busy pre-check before this call: EditorProxy's own McpException messages
/// already name the fact plainly ("none is attached to project 'X'" / "...but busy...") and adding a
/// redundant pre-check would cost a second full probe round trip for cosmetic wording only.</item>
///
/// <item><b>Idempotency has three layers, matching Plugin~'s <c>Runtime.ReloadGate.Release</c>'s
/// own real contract (confirmed by reading Plugin~/Assets/Hades/Runtime/ReloadGate.cs directly -
/// never assumed).</b> (1) Nothing believed held (<see cref="LeaseRegistry.Get"/> returns null) - the
/// common case, since by the time a user clicks, the TTL may already have fired (plan 8 proved TTL
/// release works): succeeds locally, WITHOUT touching the wire at all - there is no lease id this
/// app could honestly send, and none is needed. (2) Something IS believed held, and the plugin
/// reports it released (or already was) - <c>lease.release</c> is idempotent by design, so this
/// succeeds whether the plugin actually unlocked or found nothing to unlock. (3) The plugin reports
/// a DIFFERENT lease now holds the gate (ReloadGate.Release's own <c>false</c> case) - structurally
/// unreachable while the plugin only ever hands out one constant lease id, but handled honestly
/// rather than assumed away: reported as a failure (the user's actual problem - Unity still is not
/// compiling - is not solved), and this app's now-provably-stale belief is cleared, mirroring
/// <see cref="LeaseRegistry.ReconcileAsync"/>'s own "the stale belief must not linger" reasoning.</item>
///
/// <item><b>No editor attached fails, not idempotently succeeds</b> - the one case release does NOT
/// treat as "nothing to do", even though the plugin's own <c>ReleaseOnDisconnect</c> means the gate
/// is very likely already free. Reachability and "is the gate actually free" are different
/// questions; this app cannot confirm the second without a live session, and claiming success it
/// cannot back up would be exactly the "visible, never confusing" net #7 was written against.
/// Failing informatively, fast (see EditorsReleaseTests.NoEditorAttached_FailsInformatively_NamesTheFact_NotATimeout
/// in the test project - <see cref="ProjectService.GetCharonStatus"/> answers "not attached"
/// synchronously when nothing is registered, no probe round trip needed) is the honest answer.</item>
/// </list>
/// </summary>
public static class EditorsEndpoint
{
    // ------------------------------------------------------------------------------------- GET

    /// <summary>Orchestrates real state into an <see cref="EditorsResult"/>: every known project's
    /// Charon status, fanned out concurrently (same reasoning as <see cref="SummaryEndpoint.BuildAsync"/>
    /// and <see cref="ProjectsEndpoint.BuildAsync"/> - one stuck Editor's probe timeout must not
    /// stall every other project's row) and fed to the pure <see cref="Resolve"/>.</summary>
    public static async Task<EditorsResult> BuildAsync(ProjectService projects)
    {
        var known = projects.KnownProjects();

        var snapshots = await Task.WhenAll(known.Select(async project =>
        {
            var charon = await projects.GetCharonStatus(project.ProductGuid).ConfigureAwait(false);

            return new EditorStateSnapshot
            {
                Name = project.Name,
                ProductGuid = project.ProductGuid,
                Attached = charon?.Attached ?? false,
                Busy = charon?.Busy ?? false,
                UnityVersion = charon?.UnityVersion,
                ProcessId = charon?.ProcessId,
                ConnectionAge = charon?.ConnectionAge,
            };
        })).ConfigureAwait(false);

        return Resolve(snapshots);
    }

    /// <summary>The pure resolution core - see this class's own doc comment for the two-layer
    /// design and exactly which editors appear.</summary>
    public static EditorsResult Resolve(IReadOnlyList<EditorStateSnapshot> projects) => new()
    {
        Editors = projects.Where(p => p.Attached).Select(BuildRow).ToList(),
    };

    static EditorRow BuildRow(EditorStateSnapshot p) => new()
    {
        Project = p.Name,
        ProductGuid = p.ProductGuid,
        State = p.Busy ? ProjectEditorState.Busy : ProjectEditorState.Attached,
        Status = p.Busy ? "Editor attached (busy)" : "Editor attached",
        UnityVersion = p.UnityVersion,
        ProcessId = p.ProcessId,
        ConnectionAgeSeconds = p.ConnectionAge is { } age ? Math.Max(0, (int)Math.Round(age.TotalSeconds)) : null,
    };

    // --------------------------------------------------------------------------------- release

    /// <summary><c>POST /control/leases/{id}/release</c> - see this class's own doc comment for
    /// the full design (why <paramref name="productGuid"/> IS "which lease", why this goes through
    /// <see cref="EditorProxy.SendCommandAsync"/> with no pre-check, and the three idempotency
    /// layers). 404 when the guid is not a known project at all - the one case this treats as a
    /// malformed request rather than a resolved outcome, matching every <see cref="ProjectsEndpoint"/>
    /// action's own convention for an unknown guid.</summary>
    public static async Task<IResult> ReleaseAsync(ProjectService projects, LeaseRegistry leases, EditorProxy editorProxy, string productGuid)
    {
        var project = projects.Get(productGuid);
        if (project is null)
        {
            return Results.Json(new { error = $"Unknown project '{productGuid}'." }, statusCode: StatusCodes.Status404NotFound);
        }

        if (leases.Get(productGuid) is not { } believed)
        {
            return Results.Json(new ActionResult
            {
                Success = true,
                Message = $"No reload lease is held for '{project.Name}' — nothing to release.",
            });
        }

        WireJson result;
        try
        {
            var @params = WireJson.NewObject().SetProperty("leaseId", WireJson.String(believed.LeaseId));
            result = await editorProxy.SendCommandAsync(productGuid, "lease.release", @params).ConfigureAwait(false);
        }
        catch (McpException ex)
        {
            // Covers "not attached" and "busy" (EditorProxy's own pre-checks), a command timeout,
            // and a dropped connection mid-flight (the ack gap) - every one of these already names
            // what happened; see this class's own doc comment for why no pre-check duplicates any
            // of this here.
            return Results.Json(new ActionResult { Success = false, Message = ex.Message });
        }

        var success = result.TryGetProperty("success", out var s) && s!.Kind == WireKind.Boolean && s.AsBoolean();

        // Whichever branch below: this app's belief about believed.LeaseId specifically is no
        // longer trustworthy either way (released, or proven to be someone else's lease now) - see
        // this class's own doc comment on the three idempotency layers. ClearIfCurrent, not Clear:
        // the "lease.release" round trip just awaited is real network activity a concurrent 'begin'
        // can land in the middle of (see LeaseRegistry.ReconcileAsync's own doc comment for the
        // identical race one layer down) - clearing unconditionally would wipe a genuinely fresh
        // lease this call never asked about, not merely the stale one it did.
        leases.ClearIfCurrent(productGuid, believed.LeaseId);

        if (!success)
        {
            return Results.Json(new ActionResult
            {
                Success = false,
                Message = $"A different reload lease now holds '{project.Name}'. This app's stale belief was cleared — check Projects and try again.",
            });
        }

        return Results.Json(new ActionResult
        {
            Success = true,
            Message = $"Released the reload lease for '{project.Name}'. Unity will resume compiling.",
        });
    }
}
