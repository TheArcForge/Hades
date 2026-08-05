namespace Hades.Core.Editors;

/// <summary>One reload lease as the app currently believes it, for one project - never something
/// this app computed from a requested TTL. See <see cref="LeaseRegistry"/>'s class doc comment
/// for why every field here always came from the plugin's own answer.</summary>
public sealed record LeaseStatus
{
    public required string ProductGuid { get; init; }
    public required string LeaseId { get; init; }

    /// <summary>When this app first believed the CURRENT lease id became held - preserved across
    /// a renew of that same id (see <see cref="LeaseRegistry.RecordHeld"/>), so "how long has
    /// this been held" reports the whole continuous hold, not just time since the last renewal.</summary>
    public required DateTimeOffset AcquiredAtUtc { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }
}

/// <summary>
/// Which reload leases this app believes are currently held, one per project - <see cref="Runtime.ReloadGate"/>
/// (the plugin side) allows at most one lease at a time per Editor, so at most one entry per
/// project here too. "Believes": every entry is the PLUGIN's own answer from a lease.acquire,
/// lease.renew, or reconciling lease.renew call (see <see cref="ReconcileAsync"/>) - this class
/// never fabricates or extrapolates a lease's state from what the app itself requested, because
/// the plugin may not have honoured that request verbatim (see HadesBoot's lease handlers).
///
/// Reconnect is the hard case a spec rule exists for: a domain reload, an Editor restart, or the
/// app process itself restarting can all sever the link between "the app still remembers lease X"
/// and "the plugin still holds it" - only the plugin knows which. <see cref="ReconcileAsync"/> is
/// how this registry resynchronises to ground truth the moment a session (re)connects: it asks
/// the plugin, via <see cref="EditorSession.RenewLeaseAsync"/>, whether the believed lease
/// survived, and either confirms it (extending the recorded expiry, keeping the original
/// <see cref="LeaseStatus.AcquiredAtUtc"/>) or clears the belief outright - never leaving a stale
/// "held" entry around once the plugin has said otherwise.
///
/// Thread-safe: every method takes the same lock for the duration of its dictionary access, same
/// convention as <see cref="EditorRegistry"/>.
/// </summary>
public sealed class LeaseRegistry(Func<DateTimeOffset>? utcNow = null)
{
    readonly Dictionary<string, LeaseStatus> _leases = [];
    readonly Lock _gate = new();
    readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// Records that the plugin confirmed <paramref name="leaseId"/> held for <paramref name="productGuid"/>,
    /// expiring at <paramref name="expiresAtUtc"/> - the result of a successful lease.acquire,
    /// lease.renew, or a reconciling renew (see <see cref="ReconcileAsync"/>). Preserves the
    /// existing entry's <see cref="LeaseStatus.AcquiredAtUtc"/> when <paramref name="leaseId"/>
    /// matches what was already recorded for this project (a renewal, or reconciliation
    /// confirming the same ongoing hold survived); stamps a fresh "acquired now" otherwise (a
    /// genuinely new hold - nothing was recorded yet, or a different lease id supersedes it).
    /// </summary>
    public void RecordHeld(string productGuid, string leaseId, DateTimeOffset expiresAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productGuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);

        lock (_gate)
        {
            var acquiredAtUtc = _leases.TryGetValue(productGuid, out var existing) && existing.LeaseId == leaseId
                ? existing.AcquiredAtUtc
                : _utcNow();

            _leases[productGuid] = new LeaseStatus
            {
                ProductGuid = productGuid,
                LeaseId = leaseId,
                AcquiredAtUtc = acquiredAtUtc,
                ExpiresAtUtc = expiresAtUtc,
            };
        }
    }

    /// <summary>Clears any believed-held lease for a project - lease.release succeeded, or
    /// reconciliation found nothing (or a different lease) held. A no-op for a project with
    /// nothing recorded.</summary>
    public void Clear(string productGuid)
    {
        if (string.IsNullOrEmpty(productGuid)) return;
        lock (_gate) { _leases.Remove(productGuid); }
    }

    /// <summary>The believed-held lease for one project, or null when this app believes nothing
    /// is held there.</summary>
    public LeaseStatus? Get(string productGuid)
    {
        if (string.IsNullOrEmpty(productGuid)) return null;
        lock (_gate) { return _leases.GetValueOrDefault(productGuid); }
    }

    /// <summary>Every believed-held lease, across every project. A snapshot copy, safe to
    /// enumerate while leases come and go.</summary>
    public IReadOnlyList<LeaseStatus> All()
    {
        lock (_gate) { return [.. _leases.Values]; }
    }

    /// <summary>
    /// On reconnect, asks the plugin what it currently holds and updates this registry to match -
    /// see the class doc comment. A no-op that never touches the network when nothing is believed
    /// held for <paramref name="productGuid"/> - there is nothing to confirm, so there is nothing
    /// to ask. Otherwise sends <c>lease.renew</c> for the believed lease id over
    /// <paramref name="session"/> (real activity if it turns out to be genuinely correct - see
    /// ReloadGate.Renew's own doc comment on why that is fine, not a bug) and either confirms the
    /// belief (same id came back <c>success</c>) or clears it (anything else: nothing held, or a
    /// different lease holds it - either way, THIS app's believed lease is definitely not the one
    /// the plugin has, so the stale belief must not linger).
    ///
    /// Propagates whatever <see cref="EditorSession.RenewLeaseAsync"/> throws (a dropped
    /// connection, a malformed response) rather than swallowing it - an inconclusive reconciliation
    /// attempt is not evidence either way, so the safest thing this method can do is leave the
    /// existing belief untouched and let the caller decide whether to retry (e.g. on the next
    /// successful reconnect), rather than guessing.
    /// </summary>
    public async Task ReconcileAsync(string productGuid, EditorSession session, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productGuid);
        ArgumentNullException.ThrowIfNull(session);

        var believed = Get(productGuid);
        if (believed is null) return;

        var outcome = await session.RenewLeaseAsync(believed.LeaseId, cancellationToken).ConfigureAwait(false);

        if (outcome.Success && outcome.LeaseId == believed.LeaseId && outcome.ExpiresAtUtc is { } expiresAtUtc)
        {
            RecordHeld(productGuid, believed.LeaseId, expiresAtUtc);
        }
        else
        {
            Clear(productGuid);
        }
    }
}
