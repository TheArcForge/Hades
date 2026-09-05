using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Hades.Control.Client;
using Hades.Control.Client.Dtos;
using Hades.Shell.ShellFacts;

namespace Hades.Shell.ViewModels;

/// <summary>The settings surface of the control API.</summary>
public interface ISettingsClient
{
    Task<SettingsResult> SettingsAsync();
}

/// <summary>
/// The Settings section: two values the core owns, and two facts only the shell can observe.
///
/// <c>GET /control/settings</c> deliberately carries only <c>mcpPort</c> and <c>logLevel</c>, and
/// both are rendered VERBATIM - in particular the port's own <c>message</c> is the core's sentence
/// about whether the port is usable, never rebuilt here out of <c>port</c> and <c>inUse</c>.
///
/// Launch-at-login and battery saver are the carve-out: the core is a separate, headless process and
/// cannot observe either. This type reads them and shows them; it does not act on them.
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    // See TrayViewModel for why the handler is shared and the HttpClient is not.
    static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 4,
    };

    readonly Func<ControlConnection?> _discover;
    readonly Func<ControlConnection, ISettingsClient> _makeClient;
    readonly ILaunchAtLogin _launchAtLogin;
    readonly IPowerStatusReader _powerStatus;

    SettingsResult? _settings;
    string? _refreshError;
    bool _launchAtLoginEnabled;
    bool _isBatterySaverOn;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsViewModel(
        Func<ControlConnection?> discover,
        Func<ControlConnection, ISettingsClient>? makeClient,
        ILaunchAtLogin launchAtLogin,
        IPowerStatusReader powerStatus)
    {
        _discover = discover;
        _makeClient = makeClient ?? DefaultClient;
        _launchAtLogin = launchAtLogin;
        _powerStatus = powerStatus;

        // Seeded from the OS so the toggle is right the moment the section is first shown, rather
        // than defaulting to off until the first tick lands.
        _launchAtLoginEnabled = launchAtLogin.IsEnabled;
        _isBatterySaverOn = powerStatus.IsBatterySaverOn;
    }

    /// <summary>The server's own two settings, unmodified. Null until the first successful fetch.</summary>
    public SettingsResult? Settings
    {
        get => _settings;
        private set { _settings = value; OnPropertyChanged(); }
    }

    public string? RefreshError
    {
        get => _refreshError;
        private set { _refreshError = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// What the OS says right now - never what was last requested. SETTING this is a REQUEST: it
    /// writes, then publishes whatever the OS reports afterwards, which may not be what was asked.
    ///
    /// <para><b>Why the setter is public and does the work</b>, rather than the view calling a
    /// method. The checkbox binds here TwoWay. It used to bind <c>OneWay</c> with a <c>Click</c>
    /// handler that assigned <c>IsChecked</c> directly - and assigning a dependency property that
    /// carries a binding replaces the binding with a local value. So from the first click onward in
    /// a session the box stopped tracking this property entirely: the veto Windows writes when a
    /// user disables the entry in Task Manager was read correctly here and never reached the screen.
    /// Measured both ways - a clicked instance stayed "on" through a real veto, while a
    /// never-clicked instance tracked the registry live. That is the exact failure this whole
    /// feature exists to prevent, so the binding must survive being clicked.</para>
    ///
    /// <para>It also makes the control work for ASSISTIVE TECHNOLOGY. WPF's
    /// <c>TogglePattern.Toggle()</c> - what a screen reader or voice control invokes - calls
    /// <c>OnToggle()</c>, which moves <c>IsChecked</c> WITHOUT raising <c>Click</c>. With the logic
    /// on Click, toggling by automation silently did nothing: measured, the registry was never
    /// written. A TwoWay binding is driven by the property change, so both paths now work.</para>
    ///
    /// <para><b>PropertyChanged is raised unconditionally</b>, even when the value did not move.
    /// A refused request leaves the box locally showing what the user clicked while this property
    /// still holds the truth; without a notification the binding has no reason to push the truth
    /// back, and the box would keep displaying a state the OS rejected.</para>
    /// </summary>
    public bool LaunchAtLoginEnabled
    {
        get => _launchAtLoginEnabled;
        set
        {
            _launchAtLoginEnabled = _launchAtLogin.SetEnabled(value);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Publishes a value READ from the OS, with no write. Distinct from the setter above, which is
    /// a user request: this is how <see cref="RefreshAsync"/> catches up with a change made outside
    /// the app, and routing it through the setter would make every poll re-write the registry.
    /// </summary>
    void PublishLaunchAtLogin(bool enabled)
    {
        if (_launchAtLoginEnabled == enabled) return;

        _launchAtLoginEnabled = enabled;
        OnPropertyChanged(nameof(LaunchAtLoginEnabled));
    }

    /// <summary>Display-only; nothing else in the shell consumes it.</summary>
    public bool IsBatterySaverOn
    {
        get => _isBatterySaverOn;
        private set { _isBatterySaverOn = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Fetches the core's settings and re-reads both shell facts. The facts are re-read every tick
    /// rather than cached, because either can change outside this app - the user can disable startup
    /// in Task Manager, or unplug the machine, and the section must catch up on its own.
    /// </summary>
    public async Task RefreshAsync()
    {
        PublishLaunchAtLogin(_launchAtLogin.IsEnabled);
        IsBatterySaverOn = _powerStatus.IsBatterySaverOn;

        if (_discover() is not { } connection) return;

        RefreshError = null;

        try
        {
            Settings = await _makeClient(connection).SettingsAsync().ConfigureAwait(false);
        }
        catch (ControlClientException ex) when (ex.Error == ControlClientError.Server)
        {
            RefreshError = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Self-heals next tick; whatever is on screen stays.
        }
    }

    /// <summary>
    /// Requests a launch-at-login change and publishes WHAT THE OS SAYS AFTERWARDS, never the value
    /// that was requested. A request the OS refuses - or silently ignores, which Windows does when
    /// the user has vetoed the entry in Task Manager - must never display as on.
    ///
    /// <para>A named alias for assigning <see cref="LaunchAtLoginEnabled"/>, which is exactly what
    /// the TwoWay binding does when the checkbox moves. Kept so a test can say what it means.</para>
    /// </summary>
    public void SetLaunchAtLogin(bool enabled) => LaunchAtLoginEnabled = enabled;

    static ISettingsClient DefaultClient(ControlConnection connection) =>
        new ControlClientAdapter(new ControlClient(connection, new HttpClient(SharedHandler, disposeHandler: false)));

    sealed class ControlClientAdapter(ControlClient inner) : ISettingsClient
    {
        public Task<SettingsResult> SettingsAsync() => inner.SettingsAsync();
    }

    void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
