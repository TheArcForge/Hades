namespace Hades.Supervision.Tests;

// Exercises CoreSupervisor's adopt/spawn/restart/backoff/stable-uptime/ownership decision logic -
// the platform-neutral half of the Windows port of
// Mac/HadesSupervision/Sources/HadesSupervision/CoreSupervisor.swift. None of these tests carry
// [Trait(PlatformTraits.Key, PlatformTraits.Windows)]: CoreSupervisor.cs itself contains no
// P/Invoke (see ICoreProcessHost's own doc comment for the seam that makes that true), and every
// test below drives it through TestCoreProcessHost - a portable ICoreProcessHost built on
// System.Diagnostics.Process, not JobObject/ProcessLauncher - so they run for real here, on macOS,
// not just on a Windows CI runner that does not exist yet for this tree.
//
// Several tests spawn a REAL FakeCore.dll child process (Windows/FakeCore/Program.cs) via `dotnet`,
// so build Windows/FakeCore before running this file (see FakeCoreLocator's own error message if
// it is missing).
public class CoreSupervisorTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void DefaultBackoff_Doubles_And_Caps_At_16()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), CoreSupervisor.Configuration.DefaultBackoff(1));
        Assert.Equal(TimeSpan.FromSeconds(2), CoreSupervisor.Configuration.DefaultBackoff(2));
        Assert.Equal(TimeSpan.FromSeconds(4), CoreSupervisor.Configuration.DefaultBackoff(3));
        Assert.Equal(TimeSpan.FromSeconds(8), CoreSupervisor.Configuration.DefaultBackoff(4));
        Assert.Equal(TimeSpan.FromSeconds(16), CoreSupervisor.Configuration.DefaultBackoff(5));
        // Caps at 16 rather than continuing to double - attempt 9 would otherwise be 256s.
        Assert.Equal(TimeSpan.FromSeconds(16), CoreSupervisor.Configuration.DefaultBackoff(9));
    }

    [Fact]
    public void Configuration_Defaults_MinimumStableUptime_3s_MaxRestartAttempts_5()
    {
        var configuration = new CoreSupervisor.Configuration
        {
            Home = Path.GetTempPath(),
            CoreExecutable = "dotnet",
            CoreArguments = string.Empty,
        };

        Assert.Equal(TimeSpan.FromSeconds(3), configuration.MinimumStableUptime);
        Assert.Equal(5, configuration.MaxRestartAttempts);
    }

    [Fact]
    public async Task Start_Adopts_An_Already_Answering_Core_Without_Spawning()
    {
        using var fakeCore = await StandaloneFakeCore.StartAsync(WaitTimeout);
        var host = new TestCoreProcessHost();
        var supervisor = new CoreSupervisor(NeverSpawningConfiguration(fakeCore.Home), host);

        await supervisor.StartAsync();

        Assert.Equal(SupervisorState.Running(Ownership.Adopted), supervisor.State);
        Assert.Equal(Ownership.Adopted, supervisor.CurrentOwnership);
        Assert.Equal(0, host.SpawnCallCount);
    }

    [Fact]
    public async Task Stop_Does_Not_Kill_An_Adopted_Core()
    {
        using var fakeCore = await StandaloneFakeCore.StartAsync(WaitTimeout);
        var host = new TestCoreProcessHost();
        var supervisor = new CoreSupervisor(NeverSpawningConfiguration(fakeCore.Home), host);

        await supervisor.StartAsync();
        Assert.Equal(SupervisorState.Running(Ownership.Adopted), supervisor.State);

        await supervisor.StopAsync();

        // This is the single most important behavioural assertion in this file: an adopted core's
        // lifecycle does not belong to this supervisor, so Stop() must leave it running.
        Assert.True(fakeCore.IsRunning, "Stop() must never kill an adopted core.");
    }

    [Fact]
    public async Task Start_Spawns_When_Nothing_Answers_And_Reaches_Running_Spawned()
    {
        using var home = new EmptyHome();
        var dll = FakeCoreLocator.FindDll();
        var host = new TestCoreProcessHost();
        var configuration = new CoreSupervisor.Configuration
        {
            Home = home.Path,
            CoreExecutable = "dotnet",
            CoreArguments = $"\"{dll}\" \"{home.Path}\"",
            PingPollInterval = TimeSpan.FromMilliseconds(20),
            PingTimeout = TimeSpan.FromSeconds(10),
        };
        var supervisor = new CoreSupervisor(configuration, host);

        await supervisor.StartAsync();

        Assert.Equal(SupervisorState.Running(Ownership.Spawned), supervisor.State);
        Assert.Equal(Ownership.Spawned, supervisor.CurrentOwnership);
        Assert.Equal(1, host.SpawnCallCount);

        await supervisor.StopAsync();
    }

    [Fact]
    public async Task Spawned_Core_That_Never_Comes_Up_Is_Restarted_And_Fails_After_MaxRestartAttempts()
    {
        using var home = new EmptyHome();
        var dll = FakeCoreLocator.FindDll();
        var host = new TestCoreProcessHost();
        var clock = new ManualSupervisionClock(); // real elapsed time still bounds SpawnOnceAsync's own ping-timeout loop; only backoff sleeps are instant here.
        var configuration = new CoreSupervisor.Configuration
        {
            Home = home.Path,
            // --exit-code 1: FakeCore exits immediately, before ever writing control.token or
            // answering a ping - an ordinary, retryable launch-style failure (not an assignment
            // failure), so this should burn through the whole retry budget.
            CoreArguments = $"\"{dll}\" \"{home.Path}\" --exit-code 1",
            CoreExecutable = "dotnet",
            MaxRestartAttempts = 3,
            PingPollInterval = TimeSpan.FromMilliseconds(20),
            PingTimeout = TimeSpan.FromSeconds(5),
        };
        var supervisor = new CoreSupervisor(configuration, host, clock);

        await supervisor.StartAsync();

        Assert.Equal(SupervisorState.Failed(3), supervisor.State);
        Assert.Equal(3, host.SpawnCallCount);
    }

    [Fact]
    public async Task Assignment_Failure_Refuses_To_Spawn()
    {
        using var home = new EmptyHome();
        var host = new TestCoreProcessHost { ThrowOnAssign = true };
        var configuration = new CoreSupervisor.Configuration
        {
            Home = home.Path,
            CoreExecutable = "dotnet",
            CoreArguments = "irrelevant",
            MaxRestartAttempts = 5,
        };
        var supervisor = new CoreSupervisor(configuration, host, new ManualSupervisionClock());

        await supervisor.StartAsync();

        // "Fail loudly and refuse to spawn": the whole point is that this does NOT burn through
        // MaxRestartAttempts retrying a structurally-broken host - it gives up after the first
        // attempt, and never reaches Running.
        Assert.Equal(SupervisorState.Failed(1), supervisor.State);
        Assert.Equal(1, host.SpawnCallCount);
        Assert.NotEqual(SupervisorStateKind.Running, supervisor.State.Kind);
    }

    [Fact]
    public async Task Death_After_MinimumStableUptime_Resets_Attempt_Budget()
    {
        using var home = new EmptyHome();
        var dll = FakeCoreLocator.FindDll();
        var host = new TestCoreProcessHost();
        var clock = new ManualSupervisionClock();
        var configuration = new CoreSupervisor.Configuration
        {
            Home = home.Path,
            CoreExecutable = "dotnet",
            // Each spawned instance answers ping normally, then kills itself after 2s of real time
            // - comfortably under the default 3s MinimumStableUptime (so an un-advanced death still
            // reads as unstable) while leaving generous headroom over `dotnet`'s own cold-start
            // latency, so this doesn't race the self-destruct timer against startup. Whether a given
            // death counts as "stable" is controlled purely by whether the test calls
            // clock.Advance() before it, not by real timing.
            CoreArguments = $"\"{dll}\" \"{home.Path}\" --die-after-ms 2000",
            PingPollInterval = TimeSpan.FromMilliseconds(20),
            PingTimeout = TimeSpan.FromSeconds(10),
            // A budget of exactly 1: with no reset, a SECOND death within one restart cycle would
            // have nothing left to spend and must fail immediately, without even trying to spawn
            // again. This makes "was the budget reset" an unambiguous, directly observable fact
            // instead of something that could also be explained by a larger budget just not being
            // exhausted yet.
            MaxRestartAttempts = 1,
        };
        var supervisor = new CoreSupervisor(configuration, host, clock);

        await supervisor.StartAsync();
        await WaitForKindAsync(supervisor, SupervisorStateKind.Running);

        // Two consecutive STABLE deaths, each marked as such by advancing the clock past
        // MinimumStableUptime right after the core comes up. Neither should ever be allowed to
        // exhaust the attempt budget - proving each death's attempt count really did reset back to
        // zero, not just "hadn't run out yet".
        for (var cycle = 0; cycle < 2; cycle++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await WaitForKindAsync(supervisor, SupervisorStateKind.Restarting);
            await WaitForKindAsync(supervisor, SupervisorStateKind.Running);
        }

        // Now let the current core die WITHOUT advancing the clock - an UNSTABLE death, real
        // elapsed time between "became running" and "died" stays at ~2s, still under the default
        // 3s MinimumStableUptime. With MaxRestartAttempts=1 and no reset, this must fail outright.
        // (Not polling for an intermediate Restarting state here: with the budget already
        // exhausted, SpawnWithRetriesAsync sets Failed immediately, with no spawn attempt - and
        // therefore no real async work - in between, so Restarting can be too transient for a
        // poll to reliably observe. Unlike the stable-reset cases above, where a real spawn
        // attempt keeps Restarting visible for a meaningful stretch of real time.)
        await WaitForKindAsync(supervisor, SupervisorStateKind.Failed);

        Assert.Equal(SupervisorState.Failed(1), supervisor.State);
    }

    private static CoreSupervisor.Configuration NeverSpawningConfiguration(string home) => new()
    {
        Home = home,
        CoreExecutable = "dotnet",
        // Never actually used: the adoption tests assert the host's Spawn is never called at all.
        CoreArguments = "irrelevant",
    };

    private static async Task WaitForKindAsync(CoreSupervisor supervisor, SupervisorStateKind kind)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (supervisor.State.Kind != kind)
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"Timed out waiting for state {kind}; last observed state was {supervisor.State}.");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>An application-data root with no <c>control.token</c> in it - "nothing is running
    /// here", the precondition every spawn-path test needs.</summary>
    private sealed class EmptyHome : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hades-coresupervisor-tests-" + Guid.NewGuid().ToString("N"));

        public EmptyHome() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
