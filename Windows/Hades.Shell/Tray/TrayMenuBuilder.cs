using Hades.Control.Client.Dtos;
using Hades.Supervision;

namespace Hades.Shell.Tray;

/// <summary>What each command in the tray menu does. Supplied by the shell; null in tests.</summary>
public sealed record TrayMenuActions
{
    public Action? OnOpenHades { get; init; }
    public Action? OnQuit { get; init; }

    /// <summary>Takes the lease id. Wired in Task 5, with the lease line itself.</summary>
    public Action<string>? OnRelease { get; init; }
}

/// <summary>
/// Turns supervisor state plus the last summary into the tray menu's contents, as data.
///
/// The port of <c>Mac/HadesApp/Sources/HadesApp/Views/MenuBarContentView.swift</c>, and it carries
/// that file's rule: every string here that renders API data is a SummaryResult/SummaryRow field
/// printed verbatim. The only literal copy in this file is for the three supervision-only states,
/// which by construction have no control-API response to read from, plus the two ownership lines,
/// which describe a fact only the shell knows. No formatter, no interpolation joining two API
/// fields, no severity-vs-severity comparison deciding what to show: precedence already happened
/// server-side to produce the single iconState and the row order this prints.
/// </summary>
public static class TrayMenuBuilder
{
    // These two are the reason this task matters. They are the only place a user learns whether
    // quitting takes the core down with it - the difference between closing an app and silently
    // killing a core that another editor is mid-reload against. Verbatim from
    // SupervisionFooterView.swift, em dash included.
    internal const string AdoptedFooter = "Adopted — quitting Hades leaves it running";
    internal const string SpawnedFooter = "Started by this app — quitting stops it";

    public static IReadOnlyList<TrayMenuItem> Build(
        SupervisorState state,
        SummaryResult? summary,
        TrayMenuActions? actions = null)
    {
        var content = MenuContent.Resolve(state, summary);
        var items = new List<TrayMenuItem>();

        switch (content.Kind)
        {
            case MenuContentKind.NotRunning:
                items.Add(TrayMenuItem.Label("Hades is not running", StatusGlyph.For(content)));
                break;

            case MenuContentKind.Restarting:
                items.Add(TrayMenuItem.Label(
                    $"Restarting Hades — attempt {content.Attempt}", StatusGlyph.For(content)));
                break;

            case MenuContentKind.Failed:
                items.Add(TrayMenuItem.Label("Hades failed to start", StatusGlyph.For(content)));
                items.Add(TrayMenuItem.Label(
                    $"Gave up after {content.Attempt} attempts.", isDetail: true));
                break;

            case MenuContentKind.Running:
                AppendRunning(items, content, actions);
                break;
        }

        // Drawn outside the switch, exactly as the Mac draws them outside its own: opening the
        // window or quitting must not depend on a core being up.
        items.Add(TrayMenuItem.Separator());
        items.Add(TrayMenuItem.Command("Open Hades", actions?.OnOpenHades));
        items.Add(TrayMenuItem.Command("Quit Hades", actions?.OnQuit));

        return items;
    }

    static void AppendRunning(List<TrayMenuItem> items, MenuContent content, TrayMenuActions? actions)
    {
        var summary = content.Summary!;

        // The headline first and most prominent, always drawn and always verbatim: it is the control
        // API's own one-line answer to "what is going on", in every state.
        items.Add(TrayMenuItem.Label(summary.Headline, StatusGlyph.For(summary.IconState)));

        // The held lease, directly beneath the headline and above everything else. Spec #3 §3.1
        // makes this "deliberately prominent" as net #7 of the reload-safety design: a user must
        // never be confused about why their code stopped compiling, and the answer has to be
        // reachable before they scroll past a project list to find it.
        if (summary.Lease is { } lease)
        {
            // The lease's PROJECT, not its id. The id is what Release acts on and is a hex GUID in
            // practice; naming it here would be noise where the user needs an answer to "which of
            // my projects is blocked".
            items.Add(TrayMenuItem.Label(lease.Project, StatusGlyph.For(ControlIconState.LeaseHeld)));

            // Disabled rather than hidden when not releasable - what the Mac's
            // .disabled(!lease.releasable) does. Hiding it would leave a user who can see that
            // something is held with no indication that releasing it is even a concept.
            items.Add(TrayMenuItem.Command(
                "Release",
                () => actions?.OnRelease?.Invoke(lease.LeaseId),
                enabled: lease.Releasable));
        }

        if (summary.Rows.Count > 0)
        {
            items.Add(TrayMenuItem.Separator());

            // Row order is the core's. The shell does not sort, filter or truncate: a project the
            // core chose to report and the shell chose not to show is a project whose problem the
            // user never sees.
            foreach (var row in summary.Rows)
            {
                // Name and status are two lines rather than one interpolated string, mirroring
                // ProjectRowView's stack of two Text views. Joining them would make the shell the
                // author of a string the core never sent, which is the precise thing the rule
                // forbids - and a menu is the one surface where that is tempting, because a
                // ToolStripMenuItem holds a single caption.
                items.Add(TrayMenuItem.Label(row.Project, StatusGlyph.For(row.Severity)));
                items.Add(TrayMenuItem.Label(row.Status, isDetail: true));
            }
        }

        items.Add(TrayMenuItem.Separator());
        items.Add(TrayMenuItem.Label(FooterFor(content.Ownership!.Value)));
    }

    static string FooterFor(Ownership ownership) => ownership switch
    {
        Ownership.Adopted => AdoptedFooter,
        Ownership.Spawned => SpawnedFooter,

        // Unreachable today, and deliberately the SAFE side rather than a throw: a future third
        // ownership case must not make the menu claim that quitting is harmless.
        _ => AdoptedFooter,
    };
}
