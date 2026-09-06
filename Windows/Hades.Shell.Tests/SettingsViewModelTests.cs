using Hades.Control.Client;
using Hades.Control.Client.Dtos;
using Hades.Shell.ShellFacts;
using Hades.Shell.ViewModels;

namespace Hades.Shell.Tests;

public class SettingsViewModelTests
{
    sealed class FakeSettingsClient : ISettingsClient
    {
        public Func<Task<SettingsResult>> OnSettings { get; set; } = () => Task.FromResult(Settings(7823, "Information"));

        public Task<SettingsResult> SettingsAsync() => OnSettings();
    }

    sealed class FakeLaunchAtLogin : ILaunchAtLogin
    {
        public bool Enabled { get; set; }

        /// <summary>Simulates an OS that accepts the request and changes nothing.</summary>
        public bool Ignores { get; init; }

        public bool IsEnabled => Enabled;

        /// <summary>Counts writes, so a test can prove the polling path does NOT write.</summary>
        public int SetEnabledCalls { get; set; }

        public bool SetEnabled(bool enabled)
        {
            SetEnabledCalls++;
            if (!Ignores) Enabled = enabled;
            return IsEnabled;
        }
    }

    sealed class FakePowerStatus(bool batterySaver) : IPowerStatusReader
    {
        public bool IsBatterySaverOn => batterySaver;
    }

    static readonly ControlConnection AConnection = new() { Port = 1234, Token = "t" };

    static SettingsResult Settings(int port, string level) => new()
    {
        McpPort = new McpPortSetting { Port = port, InUse = false, Message = $"Listening on {port}." },
        LogLevel = new LogLevelSetting { Level = level },
    };

    static (SettingsViewModel Vm, FakeSettingsClient Client, FakeLaunchAtLogin Login) NewSubject(
        FakeLaunchAtLogin? login = null, bool batterySaver = false)
    {
        var client = new FakeSettingsClient();
        var launch = login ?? new FakeLaunchAtLogin();

        return (new SettingsViewModel(() => AConnection, _ => client, launch, new FakePowerStatus(batterySaver)),
                client, launch);
    }

    /// <summary>
    /// Both server fields render verbatim. The port's message is the core's own sentence - the shell
    /// must not rebuild it from port and inUse.
    /// </summary>
    [Fact]
    public async Task Refresh_RendersTheServersOwnSettingsVerbatim()
    {
        var (vm, client, _) = NewSubject();
        client.OnSettings = () => Task.FromResult(Settings(9001, "Debug"));

        await vm.RefreshAsync();

        Assert.Equal(9001, vm.Settings!.McpPort.Port);
        Assert.Equal("Listening on 9001.", vm.Settings.McpPort.Message);
        Assert.Equal("Debug", vm.Settings.LogLevel.Level);
    }

    [Fact]
    public async Task Refresh_ATransientFailureKeepsWhatIsOnScreen()
    {
        var (vm, client, _) = NewSubject();
        await vm.RefreshAsync();
        Assert.NotNull(vm.Settings);

        client.OnSettings = () => throw new ControlClientException(ControlClientError.Transport, "blip");
        await vm.RefreshAsync();

        Assert.NotNull(vm.Settings);
    }

    [Fact]
    public async Task Refresh_AServerMessageSurfaces()
    {
        var (vm, client, _) = NewSubject();
        client.OnSettings = () => throw new ControlClientException(
            ControlClientError.Server, "Settings are unavailable while indexing.", statusCode: 409);

        await vm.RefreshAsync();

        Assert.Equal("Settings are unavailable while indexing.", vm.RefreshError);
    }

    /// <summary>The two shell facts are read on every refresh - they are OS state, not cached.</summary>
    [Fact]
    public async Task Refresh_ReadsBothShellFacts()
    {
        var (vm, _, login) = NewSubject(batterySaver: true);
        login.Enabled = true;

        await vm.RefreshAsync();

        Assert.True(vm.LaunchAtLoginEnabled);
        Assert.True(vm.IsBatterySaverOn);
    }

    [Fact]
    public void TogglingLaunchAtLogin_ReflectsTheNewState()
    {
        var (vm, _, _) = NewSubject();

        vm.SetLaunchAtLogin(true);

        Assert.True(vm.LaunchAtLoginEnabled);
    }

    /// <summary>
    /// The Mac's discipline, and the reason this is worth a test at all: the displayed value comes
    /// from re-reading the OS, never from the value that was requested. An OS that accepts the call
    /// and does nothing must leave the toggle reading off.
    /// </summary>
    [Fact]
    public void TogglingLaunchAtLogin_ShowsWhatTheOsSays_NotWhatWasRequested()
    {
        var (vm, _, _) = NewSubject(login: new FakeLaunchAtLogin { Ignores = true });

        vm.SetLaunchAtLogin(true);

        Assert.False(vm.LaunchAtLoginEnabled);
    }

    /// <summary>
    /// A REFUSED request must still notify, so a TwoWay binding pulls the checkbox back to the
    /// truth. The value does not change - it was false and stays false - so a setter that only
    /// notified on change would leave the box showing the state the user clicked and the OS
    /// rejected, which is the precise lie this feature exists to prevent.
    /// </summary>
    [Fact]
    public void ARefusedRequestStillRaisesPropertyChanged_SoTheBoxSnapsBack()
    {
        var (vm, _, _) = NewSubject(login: new FakeLaunchAtLogin { Ignores = true });
        var raised = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SettingsViewModel.LaunchAtLoginEnabled)) raised++; };

        vm.LaunchAtLoginEnabled = true;

        Assert.False(vm.LaunchAtLoginEnabled);
        Assert.Equal(1, raised);
    }

    /// <summary>
    /// Assigning the property IS the user request - that is what the TwoWay binding does when the
    /// checkbox moves, and what assistive technology triggers through TogglePattern. Before this,
    /// the write lived in a Click handler that TogglePattern.Toggle() never raises, so the control
    /// could not be operated by a screen reader or by voice at all.
    /// </summary>
    [Fact]
    public void AssigningTheProperty_WritesToTheOs()
    {
        var (vm, _, login) = NewSubject();

        vm.LaunchAtLoginEnabled = true;

        Assert.True(login.Enabled);
        Assert.True(vm.LaunchAtLoginEnabled);
    }

    /// <summary>
    /// Refresh must NOT write. It publishes what the OS already says; routing it through the
    /// request setter would make every poll of the Settings section re-write the registry.
    /// </summary>
    [Fact]
    public async Task Refresh_PublishesWithoutWriting()
    {
        var (vm, _, login) = NewSubject();
        login.Enabled = true;
        login.SetEnabledCalls = 0;

        await vm.RefreshAsync();

        Assert.True(vm.LaunchAtLoginEnabled);
        Assert.Equal(0, login.SetEnabledCalls);
    }

    /// <summary>
    /// An external change - a user disabling the entry in Task Manager - must reach the screen while
    /// the app is running. This is the live half of the defect: the value was always read correctly
    /// here, but a broken binding meant it never arrived.
    /// </summary>
    [Fact]
    public async Task AnExternalVetoIsPickedUpByRefresh()
    {
        var (vm, _, login) = NewSubject();
        vm.LaunchAtLoginEnabled = true;
        Assert.True(vm.LaunchAtLoginEnabled);

        login.Enabled = false;   // as if Windows vetoed it behind the app's back
        await vm.RefreshAsync();

        Assert.False(vm.LaunchAtLoginEnabled);
    }
}
