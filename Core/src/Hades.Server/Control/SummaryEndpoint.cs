using System.Text.Json.Serialization;
using Hades.Core;
using Hades.Core.Editors;

namespace Hades.Server.Control;

/// <summary>
/// The menu bar's icon state, decided entirely here - never something the shell derives. The
/// shell maps each value straight to an image and does nothing else. Precedence when several
/// projects disagree (see <see cref="SummaryEndpoint.Resolve"/>): <see cref="Error"/> outranks
/// everything, then <see cref="LeaseHeld"/>, then <see cref="Indexing"/>, then
/// <see cref="Attached"/>, then <see cref="Idle"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ControlIconState
{
    [JsonStringEnumMemberName("idle")] Idle,
    [JsonStringEnumMemberName("indexing")] Indexing,
    [JsonStringEnumMemberName("attached")] Attached,
    [JsonStringEnumMemberName("leaseHeld")] LeaseHeld,
    [JsonStringEnumMemberName("error")] Error,
}

/// <summary>A row's resolved severity - already decided, not a condition (busy? missing path?)
/// the shell would otherwise have to evaluate itself.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ControlSeverity
{
    [JsonStringEnumMemberName("ok")] Ok,
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("error")] Error,
}

/// <summary>One project's line in the menu bar. <see cref="Status"/> is the complete,
/// human-readable string to print verbatim - including any relative time - never raw fields
/// (a timestamp, a boolean) for the shell to format itself. <see cref="ProductGuid"/> is this
/// project's stable identity - present because <see cref="Project"/> (the display name) is NOT
/// unique: two different projects can share a name (e.g. two checkouts of the same repo), and a
/// shell keying rows by name alone collides them into one. The shell must key/identify rows by
/// <see cref="ProductGuid"/>, never by <see cref="Project"/>.</summary>
public sealed record SummaryRow
{
    [JsonPropertyName("project")] public required string Project { get; init; }

    /// <summary>This project's stable identity - see this record's own doc comment for why the
    /// shell must key rows on this, never on <see cref="Project"/>.</summary>
    [JsonPropertyName("productGuid")] public required string ProductGuid { get; init; }

    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("severity")] public required ControlSeverity Severity { get; init; }
}

/// <summary>The held reload lease worth surfacing right now - see
/// <see cref="SummaryEndpoint.Resolve"/> for how "right now" is chosen when more than one project
/// holds one simultaneously. Elapsed/remaining time is already computed in whole seconds; the
/// shell never subtracts two timestamps itself.</summary>
public sealed record SummaryLease
{
    [JsonPropertyName("project")] public required string Project { get; init; }

    /// <summary>What to pass as <c>{id}</c> in <c>POST /control/leases/{id}/release</c> - the
    /// Release button's whole reason to exist. Resolved by Plan 11 Task 4
    /// (<see cref="Hades.Server.Control.EditorsEndpoint"/>, whose own class doc comment has the
    /// full reasoning) to the holding project's productGuid, deliberately NOT the plugin's own
    /// wire-level lease id: that id is a single constant regardless of project, so it cannot name
    /// "which lease", while productGuid already can (UnityPlugin's <c>Runtime.ReloadGate</c> allows at
    /// most one held lease per Editor/project, and <see cref="LeaseRegistry"/> already keys its own
    /// belief the same way).</summary>
    [JsonPropertyName("leaseId")] public required string LeaseId { get; init; }

    [JsonPropertyName("heldForSeconds")] public required int HeldForSeconds { get; init; }
    [JsonPropertyName("expiresInSeconds")] public required int ExpiresInSeconds { get; init; }

    /// <summary>Whether a force-release request has an Editor session to actually reach right
    /// now - see this file's own "design decisions" note for why this is <c>Attached</c> rather
    /// than always true. CONFIRMED as-is by Plan 11 Task 4, not refined - see
    /// <see cref="Hades.Server.Control.EditorsEndpoint"/>'s own class doc comment for why a busy
    /// Editor still counts as reachable.</summary>
    [JsonPropertyName("releasable")] public required bool Releasable { get; init; }
}

/// <summary>The full <c>GET /control/summary</c> response - see <see cref="SummaryEndpoint"/>'s
/// own class doc comment.</summary>
public sealed record SummaryResult
{
    [JsonPropertyName("iconState")] public required ControlIconState IconState { get; init; }
    [JsonPropertyName("headline")] public required string Headline { get; init; }
    [JsonPropertyName("rows")] public required IReadOnlyList<SummaryRow> Rows { get; init; }

    /// <summary>Present whenever ANY known project holds a lease - independent of which project
    /// decided <see cref="IconState"/>. A lease must never go silent just because a different,
    /// higher-precedence problem elsewhere (e.g. a missing project path) won the icon - see
    /// <see cref="SummaryEndpoint.Resolve"/>'s own doc comment.</summary>
    [JsonPropertyName("lease")] public SummaryLease? Lease { get; init; }
}

/// <summary>One project's already-resolved state, the input <see cref="SummaryEndpoint.Resolve"/>
/// turns into a <see cref="SummaryResult"/>. Deliberately pre-resolved booleans/timestamps rather
/// than a raw <see cref="CharonStatus"/>/<see cref="LeaseStatus"/> pair, so <c>Resolve</c> itself
/// can be tested with plain data and no live project/editor/lease state at all.</summary>
public sealed record ProjectSnapshot
{
    public required string Name { get; init; }

    /// <summary>This project's stable identity, threaded straight through to
    /// <see cref="SummaryRow.ProductGuid"/> - see that property's own doc comment for why.</summary>
    public required string ProductGuid { get; init; }

    /// <summary>False when the project's directory no longer exists on disk - an unmounted
    /// volume, a move, or a deletion. The one <see cref="ControlIconState.Error"/> condition this
    /// endpoint detects on its own; Plan 11 Task 3's richer warning set (serialization mode,
    /// plugin version, ...) lands with <c>/control/projects</c>, not here.</summary>
    public required bool PathExists { get; init; }

    public required bool Attached { get; init; }

    /// <summary>Only meaningful when <see cref="Attached"/> is true - see
    /// <see cref="CharonStatus.Busy"/>, which this is always sourced from.</summary>
    public required bool Busy { get; init; }

    /// <summary>Null when this project has never completed an index in this process - see
    /// <see cref="ProjectService.Summary"/>. Treated as the <see cref="ControlIconState.Indexing"/>
    /// condition: the only currently-observable stand-in for "an index is in progress", since
    /// nothing in <see cref="Hades.Core.Observation.ObservationService"/> exposes a live
    /// sync-in-progress flag yet.</summary>
    public DateTimeOffset? LastIndexedUtc { get; init; }

    public LeaseStatus? Lease { get; init; }
}

/// <summary>
/// <c>GET /control/summary</c> - the menu bar's one endpoint, and per the plan the smallest and
/// most important surface in the control API. Two layers, deliberately separate:
///
/// <list type="bullet">
/// <item><see cref="Resolve"/> is pure - a <see cref="ProjectSnapshot"/> list plus "now" in,
/// a <see cref="SummaryResult"/> out. No I/O, no async, no live project/editor/lease state. This
/// is where the icon-state precedence and every display string are decided, and where
/// SummaryTests.cs puts its strict, literal, deterministic assertions - see
/// SummaryResolveTests' own class doc comment.</item>
/// <item><see cref="BuildAsync"/> is the thin async orchestrator: it asks the real
/// <see cref="ProjectService"/> and <see cref="LeaseRegistry"/> for each known project's state and
/// hands the result to <see cref="Resolve"/>. Critically, the attached/busy/absent determination
/// is <see cref="ProjectService.GetCharonStatus"/> itself - the exact method hades_charon_status
/// calls - never a second implementation of that probe.</item>
/// </list>
///
/// Design decisions the plan left to this task:
/// <list type="bullet">
/// <item><b>What counts as <see cref="ControlIconState.Error"/> here.</b> Plan 11 Task 3 owns the
/// full warning set (serialization mode, plugin mismatch, missing path, oracle conformance).
/// Task 2 needs at least one real, testable error condition to prove the precedence rule against,
/// so it uses the cheapest one already fully derivable today - the project's directory no longer
/// existing on disk (<see cref="ProjectSnapshot.PathExists"/>) - without building any of Task 3's
/// richer detection. Task 3 can add more inputs to <see cref="Resolve"/> later without touching
/// its precedence order.</item>
/// <item><b>What counts as <see cref="ControlIconState.Indexing"/>.</b> Nothing in
/// <see cref="Hades.Core.Observation.ObservationService"/> or <see cref="ProjectService"/> exposes
/// a live "a sync is in progress right now" flag. The only currently-observable proxy is "this
/// project has never completed an index in this process" (<see cref="ProjectSummary.LastIndexedUtc"/>
/// is null) - which is exactly true during a project's first index and remains a reasonable,
/// honestly-derived signal until real progress tracking exists.</item>
/// <item><b>Multiple simultaneous leases.</b> The wire shape carries one <c>lease</c> object, but
/// nothing stops two different projects (each with its own attached Editor) from holding one at
/// the same time. <see cref="Resolve"/> surfaces the one expiring soonest - the most urgent for a
/// user glancing at the menu bar to act on.</item>
/// <item><b><see cref="SummaryLease.Releasable"/>.</b> This field is <see cref="ProjectSnapshot.Attached"/>:
/// true only when there is currently a session a release request could reach. RESOLVED by Plan 11
/// Task 4 (<see cref="Hades.Server.Control.EditorsEndpoint"/> owns the actual force-release action):
/// confirmed as-is, not refined - a busy Editor still has a live, reachable session, so gating on
/// "not busy" too would only make the button flicker unhelpfully during ordinary transient load.
/// See that class's own doc comment for the full reasoning.</item>
/// <item>The <c>lease</c> object's fields match the plan's own example (<c>project</c>,
/// <c>heldForSeconds</c>, <c>expiresInSeconds</c>, <c>releasable</c>) plus one Task 4 added:
/// <see cref="SummaryLease.LeaseId"/>, resolved to the holding project's productGuid - see that
/// property's own doc comment and <see cref="Hades.Server.Control.EditorsEndpoint"/>'s class doc
/// comment for why.</item>
/// <item><b>The <see cref="ControlIconState.Error"/> headline with more than one known project.</b>
/// With exactly one project, naming its problem is the only useful thing a headline can say, and
/// the shell showing that same sentence again in the one row underneath is unavoidable, not a
/// rendering bug. With more than one, doing the same - one project's row text repeated with its
/// name stuck in front - hides whether anything else needs a look and tells the user nothing the
/// row didn't already, so <see cref="AttentionSummary"/> switches to a count ("N of M projects need
/// attention") instead: information a single row can't carry. Every other branch's headline already
/// says something a row doesn't (a lease's elapsed time, which project is still indexing) even with
/// several projects known, so only this branch needed the switch - see
/// <see cref="SummaryResolveTests.MultipleSimultaneousLeases_PicksTheOneExpiringSoonest"/> for a
/// pinned example of a multi-project headline that was already fine.</item>
/// </list>
/// </summary>
public static class SummaryEndpoint
{
    /// <summary>Orchestrates real state into a <see cref="SummaryResult"/>: every known project's
    /// Charon status (reusing <see cref="ProjectService.GetCharonStatus"/> - see this class's own
    /// doc comment), index freshness, path existence, and believed-held lease, fanned out
    /// concurrently across projects (so one busy/stuck Editor's probe timeout cannot stall every
    /// other project's row - see hades_charon_status's own description of how often Unity gets
    /// stuck) and fed to the pure <see cref="Resolve"/>.</summary>
    public static async Task<SummaryResult> BuildAsync(ProjectService projects, LeaseRegistry leases, Func<DateTimeOffset> utcNow)
    {
        var known = projects.KnownProjects();

        var snapshots = await Task.WhenAll(known.Select(async project =>
        {
            var charon = await projects.GetCharonStatus(project.ProductGuid).ConfigureAwait(false);
            var summary = projects.Summary(project.ProductGuid);
            var lease = leases.Get(project.ProductGuid);

            return new ProjectSnapshot
            {
                Name = project.Name,
                ProductGuid = project.ProductGuid,
                PathExists = Directory.Exists(project.Path),
                Attached = charon?.Attached ?? false,
                Busy = charon?.Busy ?? false,
                LastIndexedUtc = summary?.LastIndexedUtc,
                Lease = lease,
            };
        })).ConfigureAwait(false);

        return Resolve(snapshots, utcNow());
    }

    /// <summary>The pure precedence/formatting core - see this class's own doc comment for the
    /// two-layer design and <see cref="SummaryResolveTests"/> for the literal strings this
    /// contract is pinned to.</summary>
    public static SummaryResult Resolve(IReadOnlyList<ProjectSnapshot> projects, DateTimeOffset now)
    {
        // Independent of which icon wins below: a held lease is never allowed to go silent just
        // because some OTHER project's higher-precedence problem decided the icon - see this
        // class's own doc comment and SummaryResolveTests.ErrorOnOneProject_..._ButTheLeaseStaysVisible.
        var leaseHolder = projects
            .Where(p => p.Lease is not null)
            .OrderBy(p => p.Lease!.ExpiresAtUtc)
            .FirstOrDefault();
        var lease = leaseHolder is null ? null : BuildLease(leaseHolder, now);

        var rows = projects.Select(p => BuildRow(p, now)).ToList();

        if (projects.Count == 0)
        {
            return new SummaryResult
            {
                IconState = ControlIconState.Idle,
                Headline = "No projects yet — add a Unity project to get started.",
                Rows = rows,
                Lease = lease,
            };
        }

        if (projects.FirstOrDefault(p => !p.PathExists) is { } errorProject)
        {
            // Single project: name the one thing wrong with it - there is no "across projects" to
            // summarize instead, and the row right below says the same thing for the same reason
            // this headline does (see this class's own doc comment on why that's fine here but not
            // below). Multiple projects: naming just THIS project's problem would either repeat a
            // row verbatim (if that row is the only other content) or bury how many OTHER projects
            // also need a look - so the headline switches to a count, which is genuinely new
            // information rather than a copy of something already on screen.
            var headline = projects.Count == 1
                ? $"{errorProject.Name}: project path not found — check that the volume is mounted."
                : AttentionSummary(rows);

            return new SummaryResult
            {
                IconState = ControlIconState.Error,
                Headline = headline,
                Rows = rows,
                Lease = lease,
            };
        }

        if (leaseHolder is not null)
        {
            return new SummaryResult
            {
                IconState = ControlIconState.LeaseHeld,
                Headline = $"Holding script reload for {leaseHolder.Name} — {lease!.HeldForSeconds}s",
                Rows = rows,
                Lease = lease,
            };
        }

        if (projects.FirstOrDefault(p => p.LastIndexedUtc is null) is { } indexingProject)
        {
            return new SummaryResult
            {
                IconState = ControlIconState.Indexing,
                Headline = $"Indexing {indexingProject.Name}…",
                Rows = rows,
                Lease = lease,
            };
        }

        if (projects.FirstOrDefault(p => p.Attached) is { } attachedProject)
        {
            return new SummaryResult
            {
                IconState = ControlIconState.Attached,
                Headline = $"{attachedProject.Name} — Editor attached",
                Rows = rows,
                Lease = lease,
            };
        }

        return new SummaryResult
        {
            IconState = ControlIconState.Idle,
            Headline = "No Unity Editor attached",
            Rows = rows,
            Lease = lease,
        };
    }

    static SummaryRow BuildRow(ProjectSnapshot project, DateTimeOffset now)
    {
        if (!project.PathExists)
        {
            return new SummaryRow
            {
                Project = project.Name,
                ProductGuid = project.ProductGuid,
                Status = "Project path not found — check that the volume is mounted.",
                Severity = ControlSeverity.Error,
            };
        }

        var editorPart = project switch
        {
            { Attached: true, Busy: true } => "Editor attached (busy)",
            { Attached: true, Busy: false } => "Editor attached",
            _ => "No Editor attached",
        };

        var indexPart = project.LastIndexedUtc is { } lastIndexedUtc
            ? $"indexed {FormatAge(now - lastIndexedUtc)} ago"
            : "not yet indexed";

        return new SummaryRow
        {
            Project = project.Name,
            ProductGuid = project.ProductGuid,
            Status = $"{editorPart} · {indexPart}",
            Severity = project.Busy ? ControlSeverity.Warning : ControlSeverity.Ok,
        };
    }

    static SummaryLease BuildLease(ProjectSnapshot project, DateTimeOffset now)
    {
        var lease = project.Lease!;

        return new SummaryLease
        {
            Project = project.Name,
            LeaseId = lease.ProductGuid,
            HeldForSeconds = Math.Max(0, (int)Math.Round((now - lease.AcquiredAtUtc).TotalSeconds)),
            ExpiresInSeconds = Math.Max(0, (int)Math.Round((lease.ExpiresAtUtc - now).TotalSeconds)),
            Releasable = project.Attached,
        };
    }

    /// <summary>The multi-project Error headline: "N of M projects need attention" rather than one
    /// project's row text repeated with a name stuck in front - see the <see cref="Resolve"/> call
    /// site's own comment. "Needs attention" is exactly <see cref="ControlSeverity.Ok"/> vs not -
    /// the same severity already sent per row, just counted, never a second judgment about which
    /// project is wrong or how. Grammar agrees with the count that varies (N, the number needing
    /// attention) - always exactly one project by construction of the <see cref="Resolve"/> branch
    /// that calls this, but written generally rather than assuming that.</summary>
    static string AttentionSummary(IReadOnlyList<SummaryRow> rows)
    {
        var needsAttention = rows.Count(r => r.Severity != ControlSeverity.Ok);
        var verb = needsAttention == 1 ? "needs" : "need";
        return $"{needsAttention} of {rows.Count} projects {verb} attention";
    }

    /// <summary>"12s" under a minute, "2m" at or beyond - same rendering
    /// Mcp.SummaryTools.FormatAge uses for hades_charon_status, duplicated rather than shared
    /// (that method is private and this file's own scope is Control/, not Mcp/) - see this class's
    /// own doc comment for the file boundary.</summary>
    static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return age.TotalMinutes < 1 ? $"{age.TotalSeconds:F0}s" : $"{age.TotalMinutes:F0}m";
    }
}
