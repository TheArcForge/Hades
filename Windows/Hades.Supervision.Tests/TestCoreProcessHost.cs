using System.Diagnostics;

namespace Hades.Supervision.Tests;

/// <summary>
/// The <see cref="ICoreProcessHost"/> test double CoreSupervisorTests.cs runs against. Spawns
/// FakeCore via <see cref="System.Diagnostics.Process"/> - portable, no Job Object, no P/Invoke -
/// so <see cref="CoreSupervisor"/>'s adopt/spawn/backoff/stable-uptime logic can be exercised for
/// real, end to end, against a real child process, on macOS as well as Windows. This deliberately
/// does NOT reuse <see cref="Win32CoreProcessHost"/> for anything: proving <see cref="CoreSupervisor"/>
/// itself has no P/Invoke of its own, and can be driven entirely through this interface, is the
/// whole point of the seam - see <see cref="ICoreProcessHost"/>'s own doc comment.
/// </summary>
internal sealed class TestCoreProcessHost : ICoreProcessHost
{
    /// <summary>Number of times <see cref="Spawn"/> has been called - tests use this to assert a
    /// spawn never happened (adoption) or happened exactly the expected number of times
    /// (assignment failure refusing to retry).</summary>
    public int SpawnCallCount { get; private set; }

    /// <summary>When set, <see cref="Spawn"/> throws <see cref="CoreProcessAssignmentException"/>
    /// instead of launching anything - simulates the real host's Job Object assignment failing
    /// (see <see cref="Win32CoreProcessHost"/>'s own handling of that case).</summary>
    public bool ThrowOnAssign { get; set; }

    public ICoreProcess Spawn(CoreProcessStartInfo startInfo)
    {
        SpawnCallCount++;

        if (ThrowOnAssign)
        {
            throw new CoreProcessAssignmentException(
                "Simulated Job Object assignment failure.", new InvalidOperationException("assignment denied"));
        }

        var psi = new ProcessStartInfo(startInfo.Executable, startInfo.Arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrEmpty(startInfo.WorkingDirectory))
        {
            psi.WorkingDirectory = startInfo.WorkingDirectory;
        }

        foreach (var pair in startInfo.Environment)
        {
            psi.Environment[pair.Key] = pair.Value;
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        return new TestCoreProcess(process);
    }
}

/// <summary>The <see cref="ICoreProcess"/> half of <see cref="TestCoreProcessHost"/> - a thin
/// wrapper over a real, portable <see cref="Process"/>.</summary>
internal sealed class TestCoreProcess : ICoreProcess
{
    private readonly Process _process;

    public TestCoreProcess(Process process)
    {
        _process = process;
        Exited = _process.WaitForExitAsync();
    }

    public int ProcessId => _process.Id;

    public bool IsRunning
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public Task Exited { get; }

    public void Terminate()
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
    }

    public void Dispose() => _process.Dispose();
}
