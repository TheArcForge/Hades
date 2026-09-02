using Hades.Shell.ShellFacts;

namespace Hades.Shell.Tests;

/// <summary>
/// A registry double. The real thing is never touched by tests: registering a login item is a
/// genuine side effect on the developer's machine, and a suite that did it as a consequence of
/// running would be changing what the OS does at boot.
/// </summary>
sealed class FakeRegistry : IStartupRegistry
{
    readonly Dictionary<string, string> _run = [];
    readonly Dictionary<string, byte[]> _approved = [];

    /// <summary>Simulates an OS that accepts the call and changes nothing - the failure mode the
    /// Mac reference warns about, where success cannot be inferred from the absence of an error.</summary>
    public bool RefusesWrites { get; init; }

    public string? GetRunValue(string name) => _run.GetValueOrDefault(name);

    public void SetRunValue(string name, string commandLine)
    {
        if (RefusesWrites) return;
        _run[name] = commandLine;
    }

    public void DeleteRunValue(string name)
    {
        if (RefusesWrites) return;
        _run.Remove(name);
    }

    public byte[]? GetStartupApproval(string name) => _approved.GetValueOrDefault(name);

    public void DeleteStartupApproval(string name)
    {
        if (RefusesWrites) return;
        _approved.Remove(name);
    }

    // ---- test helpers -------------------------------------------------------------------------
    //
    // Seeding bypasses RefusesWrites, and the distinction matters: that flag simulates the OS
    // refusing THIS APP's write, not the state that was already there before it ran. Seeding through
    // SetRunValue instead made one test silently set nothing up and then assert against the empty
    // registry it had accidentally created.

    public void SeedRunValue(string name, string commandLine) => _run[name] = commandLine;

    /// <summary>What Task Manager writes when the user disables a startup entry: the Run value is
    /// left alone and a veto is recorded here instead.</summary>
    public void SetStartupApprovedDisabled(string name) => _approved[name] = [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    public void SetStartupApprovedEnabled(string name) => _approved[name] = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    public bool HasStartupApprovedEntry(string name) => _approved.ContainsKey(name);
}

public class LaunchAtLoginTests
{
    const string Command = @"C:\Hades\Hades.Shell.exe";

    static LaunchAtLogin Subject(FakeRegistry registry) => new(registry, Command);

    [Fact]
    public void ReportsDisabled_WhenNothingIsRegistered()
    {
        Assert.False(Subject(new FakeRegistry()).IsEnabled);
    }

    [Fact]
    public void ReportsEnabled_WhenTheRunValueExistsAndNothingVetoesIt()
    {
        var registry = new FakeRegistry();
        registry.SeedRunValue(LaunchAtLogin.ValueName, Command);

        Assert.True(Subject(registry).IsEnabled);
    }

    /// <summary>
    /// THE BUG THIS WHOLE TASK EXISTS TO PREVENT. Disabling the app in Task Manager does NOT remove
    /// the Run value - Windows records the veto in StartupApproved\Run instead. An implementation
    /// that writes and re-reads only the Run value therefore reports "on" forever while Windows
    /// never launches the app.
    /// </summary>
    [Fact]
    public void ReportsDisabled_WhenTheUserDisabledItInTaskManager_EvenThoughTheRunValueRemains()
    {
        var registry = new FakeRegistry();
        registry.SeedRunValue(LaunchAtLogin.ValueName, Command);
        registry.SetStartupApprovedDisabled(LaunchAtLogin.ValueName);

        Assert.False(Subject(registry).IsEnabled);
    }

    [Fact]
    public void ReportsEnabled_WhenStartupApprovedHoldsAnEnabledState()
    {
        var registry = new FakeRegistry();
        registry.SeedRunValue(LaunchAtLogin.ValueName, Command);
        registry.SetStartupApprovedEnabled(LaunchAtLogin.ValueName);

        Assert.True(Subject(registry).IsEnabled);
    }

    /// <summary>
    /// Enabling DELETES any veto rather than authoring an enabled state: the entry's byte format is
    /// undocumented, and deleting returns the app to the OS's own default-enabled path, which is the
    /// honest way to express "the user just re-enabled this from inside the app".
    /// </summary>
    [Fact]
    public void Enabling_WritesTheRunValue_AndClearsAnyStartupApprovedVeto()
    {
        var registry = new FakeRegistry();
        registry.SetStartupApprovedDisabled(LaunchAtLogin.ValueName);

        var launchAtLogin = Subject(registry);
        launchAtLogin.SetEnabled(true);

        Assert.True(launchAtLogin.IsEnabled);
        Assert.False(registry.HasStartupApprovedEntry(LaunchAtLogin.ValueName));
    }

    [Fact]
    public void Disabling_RemovesTheRunValue()
    {
        var registry = new FakeRegistry();
        registry.SeedRunValue(LaunchAtLogin.ValueName, Command);

        var launchAtLogin = Subject(registry);
        launchAtLogin.SetEnabled(false);

        Assert.False(launchAtLogin.IsEnabled);
        Assert.Null(registry.GetRunValue(LaunchAtLogin.ValueName));
    }

    /// <summary>
    /// The Mac's discipline, ported: never trust the requested value, always re-read. An OS that
    /// accepts the call and silently changes nothing must not be reported as success.
    /// </summary>
    [Fact]
    public void ReportsWhatTheOsSays_NotWhatWasRequested()
    {
        var registry = new FakeRegistry { RefusesWrites = true };
        var launchAtLogin = Subject(registry);

        launchAtLogin.SetEnabled(true);

        Assert.False(launchAtLogin.IsEnabled);
    }

    /// <summary>And the same in the other direction - a refused disable must still read as on.</summary>
    [Fact]
    public void ARefusedDisable_StillReportsEnabled()
    {
        var registry = new FakeRegistry { RefusesWrites = true };
        registry.SeedRunValue(LaunchAtLogin.ValueName, Command);

        var launchAtLogin = Subject(registry);
        launchAtLogin.SetEnabled(false);

        Assert.True(launchAtLogin.IsEnabled);
    }

    /// <summary>SetEnabled hands back the re-read, so a caller cannot accidentally use the request
    /// it made instead.</summary>
    [Fact]
    public void SetEnabledReturnsTheReReadValue()
    {
        var registry = new FakeRegistry { RefusesWrites = true };

        Assert.False(Subject(registry).SetEnabled(true));
    }

    /// <summary>A registry that throws must not take the app down - the toggle simply reports what
    /// is still true.</summary>
    [Fact]
    public void AThrowingRegistryIsSurvived()
    {
        var launchAtLogin = new LaunchAtLogin(new ThrowingRegistry(), Command);

        Assert.False(launchAtLogin.SetEnabled(true));
        Assert.False(launchAtLogin.IsEnabled);
    }

    sealed class ThrowingRegistry : IStartupRegistry
    {
        public string? GetRunValue(string name) => throw new UnauthorizedAccessException("locked down");
        public void SetRunValue(string name, string commandLine) => throw new UnauthorizedAccessException("locked down");
        public void DeleteRunValue(string name) => throw new UnauthorizedAccessException("locked down");
        public byte[]? GetStartupApproval(string name) => throw new UnauthorizedAccessException("locked down");
        public void DeleteStartupApproval(string name) => throw new UnauthorizedAccessException("locked down");
    }
}
