using System.Drawing;
using Hades.Control.Client.Dtos;
using Hades.Supervision;

// The one place in the shell that reaches for WinForms. Hades.Shell.csproj removes
// System.Windows.Forms from the implicit usings precisely so this import has to be written down,
// rather than every unqualified Application/MessageBox/Clipboard in the tree silently resolving to
// the WinForms twin of the WPF type. The alias keeps that visible at each use site.
using WinForms = System.Windows.Forms;

namespace Hades.Shell.Tray;

/// <summary>
/// Owns the notification-area icon and its menu. WPF has no tray primitive, so this wraps
/// <c>System.Windows.Forms.NotifyIcon</c>, the in-box API, rather than taking a third-party package.
///
/// It decides nothing: <see cref="Update"/> hands state to <see cref="MenuContent.Resolve"/> and
/// <see cref="TrayMenuBuilder"/> and renders whatever comes back.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    readonly WinForms.NotifyIcon _icon;
    readonly WinForms.ContextMenuStrip _menu;

    /// <summary>The user asked for the main window - via the menu, or by double-clicking the icon.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>The user asked to quit. What that means for a spawned core is the shell's call, not this class's.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>Release the given lease id. Wired in Task 5, with the lease line itself.</summary>
    public event EventHandler<string>? ReleaseRequested;

    /// <summary>
    /// The context menu is about to be shown, or has closed. The shell polls faster while it is
    /// open - what the user is actually reading should be current, and the rest of the time only
    /// the icon has to be.
    /// </summary>
    public event EventHandler? MenuOpened;

    public event EventHandler? MenuClosed;

    public TrayIcon()
    {
        _menu = new WinForms.ContextMenuStrip();
        _menu.Opening += (_, _) => MenuOpened?.Invoke(this, EventArgs.Empty);
        _menu.Closed += (_, _) => MenuClosed?.Invoke(this, EventArgs.Empty);
        _icon = new WinForms.NotifyIcon
        {
            Icon = Load(IconNameFor(MenuContent.NotRunning)),
            Text = "Hades",
            ContextMenuStrip = _menu,
            Visible = false,
        };

        // Double-click is the Windows convention for "show me the thing". The context menu's own
        // Open Hades item raises the same event.
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        Update(SupervisorState.NotStarted, summary: null);
    }

    public void Show() => _icon.Visible = true;

    /// <summary>
    /// Re-renders the icon and the menu for the current supervisor state and the last summary.
    /// Callers must pass a null summary once the supervisor leaves Running - a stale summary from a
    /// dead core must not survive into a later Running, which is <see cref="MenuContent.Resolve"/>'s
    /// own documented precondition.
    /// </summary>
    public void Update(SupervisorState state, SummaryResult? summary)
    {
        var content = MenuContent.Resolve(state, summary);

        SetIcon(IconNameFor(content));
        TrayMenuAdapter.Apply(_menu, TrayMenuBuilder.Build(state, summary, new TrayMenuActions
        {
            OnOpenHades = () => OpenRequested?.Invoke(this, EventArgs.Empty),
            OnQuit = () => QuitRequested?.Invoke(this, EventArgs.Empty),
            OnRelease = leaseId => ReleaseRequested?.Invoke(this, leaseId),
        }));
    }

    /// <summary>
    /// Shows a notification balloon. The wording belongs to the caller: this class does not know
    /// why it is being asked to say something, and an earlier version that hardcoded one message
    /// ("Hades is already running") ended up showing it for double-clicks too, where it was simply
    /// untrue.
    /// </summary>
    public void ShowBalloon(string message) =>
        _icon.ShowBalloonTip(3000, "Hades", message, WinForms.ToolTipIcon.Info);

    /// <summary>
    /// Which of the seven .ico files represents this content. The supervision-only cases have no
    /// control-API response to read an iconState from, so they are mapped here; Running delegates
    /// entirely to the API's own resolved value. Parallel to
    /// <see cref="StatusGlyph.For(MenuContent)"/>, which picks the in-window glyph for the same
    /// states - the two must not drift.
    /// </summary>
    internal static string IconNameFor(MenuContent content) => content.Kind switch
    {
        MenuContentKind.NotRunning => "notRunning",
        MenuContentKind.Restarting => "indexing",
        MenuContentKind.Failed => "error",
        MenuContentKind.Running => IconNameFor(content.Summary!.IconState),
        _ => "unknown",
    };

    internal static string IconNameFor(ControlIconState state) => state switch
    {
        ControlIconState.Idle => "idle",
        ControlIconState.Indexing => "indexing",
        ControlIconState.Attached => "attached",
        ControlIconState.LeaseHeld => "leaseHeld",
        ControlIconState.Error => "error",
        _ => "unknown",
    };

    void SetIcon(string iconName)
    {
        var previous = _icon.Icon;
        _icon.Icon = Load(iconName);

        // NotifyIcon does not own the Icon it is handed, so replacing one leaks the old handle
        // unless we dispose it ourselves - and this is swapped on every poll that changes state.
        previous?.Dispose();
    }

    static Icon Load(string name)
    {
        var uri = new Uri($"pack://application:,,,/Icons/{name}.ico");
        using var stream = System.Windows.Application.GetResourceStream(uri).Stream;

        // SmallIconSize rather than a hardcoded 16: the notification area asks for 20/24/32 on a
        // scaled display, and picking the wrong entry out of a multi-size .ico is exactly the
        // "looks slightly wrong and nobody can say why" bug the extra sizes exist to prevent.
        return new Icon(stream, WinForms.SystemInformation.SmallIconSize);
    }

    // NotifyIcon MUST be disposed explicitly. A tray icon whose owning process exits without
    // disposing leaves a "ghost" icon in the notification area that only vanishes when the user
    // hovers over it - a classic, very visible Windows bug. Clearing Visible first makes the icon
    // disappear immediately rather than at the shell's convenience.
    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
        _menu.Dispose();
    }
}
