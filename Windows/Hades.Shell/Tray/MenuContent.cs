using Hades.Control.Client.Dtos;
using Hades.Supervision;

namespace Hades.Shell.Tray;

public enum MenuContentKind
{
    /// <summary>No core to ask: supervision has not started, is starting, or a Running core has not
    /// yet answered /control/summary even once.</summary>
    NotRunning,

    /// <summary>A spawned core died and CoreSupervisor is retrying with backoff.</summary>
    Restarting,

    /// <summary>Every restart attempt failed. Terminal until the app restarts supervision.</summary>
    Failed,

    /// <summary>A core is running and has answered /control/summary at least once.</summary>
    Running,
}

/// <summary>
/// Everything the tray needs to render, collapsed into one value the menu switches on. The port of
/// <c>Mac/HadesApp/Sources/HadesApp/MenuBarContent.swift</c>.
///
/// Per "the shell renders, the core decides", the ONLY decision made here is which of these four
/// CASES applies, and it is driven entirely by <see cref="SupervisorState"/> - a shell-native
/// concept the core has no equivalent for, since the core cannot know whether some local process is
/// supervising it. Once the case is <see cref="MenuContentKind.Running"/>, the payload is the
/// control API's own <see cref="SummaryResult"/> carried through completely unchanged:
/// <see cref="Resolve"/> never reads inside it, never reformats a field, never picks a row.
///
/// C# has no tagged-union enum, so this follows <see cref="SupervisorState"/>'s own shape - a record
/// struct carrying only the payload the active <see cref="Kind"/> uses.
/// </summary>
public readonly record struct MenuContent
{
    public MenuContentKind Kind { get; }

    /// <summary>Restarting's 1-based attempt counter, or Failed's total attempt count.</summary>
    public int Attempt { get; }

    /// <summary>Set only when <see cref="Kind"/> is Running.</summary>
    public Ownership? Ownership { get; }

    /// <summary>Set only when <see cref="Kind"/> is Running. Never modified, only carried.</summary>
    public SummaryResult? Summary { get; }

    MenuContent(MenuContentKind kind, int attempt, Ownership? ownership, SummaryResult? summary)
    {
        Kind = kind;
        Attempt = attempt;
        Ownership = ownership;
        Summary = summary;
    }

    public static readonly MenuContent NotRunning = new(MenuContentKind.NotRunning, 0, null, null);

    public static MenuContent Restarting(int attempt) => new(MenuContentKind.Restarting, attempt, null, null);

    public static MenuContent Failed(int attempts) => new(MenuContentKind.Failed, attempts, null, null);

    public static MenuContent Running(Ownership ownership, SummaryResult summary) =>
        new(MenuContentKind.Running, 0, ownership, summary);

    /// <summary>
    /// The pure <see cref="SupervisorState"/> + last summary -> <see cref="MenuContent"/> mapping:
    /// no I/O, nothing but a switch.
    ///
    /// <paramref name="lastSummary"/> is whatever the caller's most recent successful
    /// /control/summary fetch produced, or null if there has never been one. This does not fetch
    /// anything itself, so callers MUST clear it once the supervisor moves off Running - a stale
    /// summary from a now-dead core must not survive into a later Running.
    /// </summary>
    public static MenuContent Resolve(SupervisorState supervisorState, SummaryResult? lastSummary) =>
        supervisorState.Kind switch
        {
            SupervisorStateKind.NotStarted or SupervisorStateKind.Starting => NotRunning,
            SupervisorStateKind.Restarting => Restarting(supervisorState.Attempt),
            SupervisorStateKind.Failed => Failed(supervisorState.Attempt),

            // A Running supervisor whose core has never answered still renders as not-running: there
            // is genuinely nothing to show, and an empty Running menu would read as "up, with no
            // projects" rather than "still coming up".
            SupervisorStateKind.Running => lastSummary is null
                ? NotRunning
                : Running(supervisorState.Ownership!.Value, lastSummary),

            _ => NotRunning,
        };
}
