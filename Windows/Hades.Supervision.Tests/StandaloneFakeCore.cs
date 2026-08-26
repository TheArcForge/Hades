using System.Diagnostics;

namespace Hades.Supervision.Tests;

/// <summary>
/// A FakeCore instance started directly by a TEST - standing in for "a Hades core that was already
/// running before the app ever started", i.e. exactly the case <see cref="CoreSupervisor"/>'s
/// adopt-or-spawn decision has to detect. Deliberately does NOT go through
/// <see cref="ICoreProcessHost"/>/<see cref="TestCoreProcessHost"/> at all: the whole point of the
/// adoption tests that use this is that <see cref="CoreSupervisor"/> must reach
/// <c>Running(Ownership.Adopted)</c> WITHOUT ever calling <see cref="ICoreProcessHost.Spawn"/> -
/// mixing the two would defeat the assertion that the host was never asked to spawn anything.
/// </summary>
internal sealed class StandaloneFakeCore : IDisposable
{
    private readonly Process _process;

    public string Home { get; }

    private StandaloneFakeCore(string home, Process process)
    {
        Home = home;
        _process = process;
    }

    public bool IsRunning => !_process.HasExited;

    public static async Task<StandaloneFakeCore> StartAsync(TimeSpan timeout)
    {
        var home = Path.Combine(Path.GetTempPath(), "hades-coresupervisor-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);

        var dll = FakeCoreLocator.FindDll();
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" \"{home}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");

        var tokenPath = Path.Combine(home, "control.token");
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(tokenPath))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException("FakeCore exited before writing control.token.");
            }

            if (DateTime.UtcNow >= deadline)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("FakeCore did not write control.token in time.");
            }

            await Task.Delay(20);
        }

        return new StandaloneFakeCore(home, process);
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();

        try
        {
            Directory.Delete(Home, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
