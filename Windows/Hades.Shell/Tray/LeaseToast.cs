using Hades.Control.Client.Dtos;

namespace Hades.Shell.Tray;

/// <summary>
/// Decides when to interrupt the user about a reload lease that has been held too long.
///
/// WHY THIS EXISTS AT ALL, when the Mac has no equivalent. Spec #3 §3.1 makes the lease indicator
/// "deliberately prominent" as net #7 of the reload-safety design: a user must never be confused
/// about why their code stopped compiling. On macOS the menu-bar icon is always visible, so the icon
/// IS the prominence. Windows hides tray icons behind the overflow chevron by default, so on this
/// platform the icon can be showing `leaseHeld` where nobody will ever see it. The toast is what
/// replaces that guarantee.
///
/// Pure policy, no UI: it says whether to notify and with what, and the shell shows it. That is what
/// makes the once-per-hold rule testable, which is the part that actually goes wrong.
/// </summary>
public sealed class LeaseToast
{
    /// <summary>
    /// MIRRORS <c>ReloadGate.HeldWarningThreshold</c> in UnityPlugin/Assets/Hades/Runtime/ReloadGate.cs.
    ///
    /// It is duplicated rather than referenced because there is no shared source to reference: the
    /// plugin is a Unity C# project in no solution the shell builds, and the shell cannot take a
    /// dependency on it. The plan said to "read it from the same source" on the grounds that the Mac
    /// surface already keys its warning off this value - the Mac has no reference to it at all, so
    /// there is no such source to read.
    ///
    /// The duplication is therefore unavoidable, but the DRIFT is not:
    /// <c>LeaseToastTests.ThresholdMatchesTheUnityPlugin</c> parses ReloadGate.cs and fails if the
    /// two ever disagree. Two surfaces telling a user different things about when a lock is "too
    /// long" is exactly the confusion this feature exists to prevent.
    ///
    /// The durable fix is for the core to report the threshold in the control API, at which point
    /// this constant and its test both go away.
    /// </summary>
    public static readonly TimeSpan HeldWarningThreshold = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The lease already notified about. Cleared when no lease is held, so the NEXT hold notifies
    /// again - ReloadGate's <c>_warnedThisHold</c> flag does the same thing for the console warning.
    /// </summary>
    string? _notifiedLeaseId;

    /// <summary>
    /// The message to show, or null to stay quiet. Call on every poll; it fires at most once per
    /// continuous hold.
    ///
    /// Like ReloadGate, this measures from ORIGINAL acquisition rather than last activity - which is
    /// what <see cref="SummaryLease.HeldForSeconds"/> already reports - so a lease an agent
    /// diligently renews every few seconds still eventually warns. From the user's point of view
    /// recompiling is blocked the whole time, however often the lease itself gets renewed.
    /// </summary>
    public string? Evaluate(SummaryLease? lease)
    {
        if (lease is null)
        {
            _notifiedLeaseId = null;
            return null;
        }

        if (lease.HeldForSeconds < HeldWarningThreshold.TotalSeconds) return null;
        if (_notifiedLeaseId == lease.LeaseId) return null;

        _notifiedLeaseId = lease.LeaseId;

        // Deliberately close to ReloadGate's own console warning: a user who sees both should not
        // have to work out that they are about the same thing.
        return $"{lease.Project} has held Unity's reload lock for over "
               + $"{HeldWarningThreshold.TotalSeconds:F0}s. Unity will not recompile until it is released.";
    }
}
