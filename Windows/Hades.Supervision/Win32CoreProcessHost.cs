using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Hades.Supervision;

/// <summary>
/// The real <see cref="ICoreProcessHost"/>: launches the core suspended, assigns it to a fresh
/// <see cref="JobObject"/>, then resumes it - the exact create-suspended/assign/resume ordering
/// <see cref="JobObject"/>'s own doc comment requires (assignment does not retroactively capture
/// descendants of an already-running process).
///
/// HONESTY NOTE (matches Hades.Supervision.Tests/JobObjectTests.cs's own note): as of this
/// writing there is no windows-latest CI job wired up for Windows/HadesWindows.slnx, so this type
/// has never executed anywhere - it is compile-and-review verified only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32CoreProcessHost : ICoreProcessHost
{
    public ICoreProcess Spawn(CoreProcessStartInfo startInfo)
    {
        var launched = ProcessLauncher.LaunchSuspended(startInfo.Executable, startInfo.Arguments, startInfo.WorkingDirectory);

        var job = new JobObject();
        try
        {
            job.Assign(launched.ProcessHandle);
        }
        catch (Win32Exception ex)
        {
            // "Assignment failure -> fail loudly and refuse to spawn" (this port's own
            // requirement, beyond anything the Swift original needs - Mac's reaper has no
            // equivalent "assign to a kernel object" step that can fail this way). The process was
            // created suspended and never resumed, so it has not run a single instruction yet -
            // kill it outright rather than leaking a permanently-suspended orphan, then surface a
            // type CoreSupervisor knows to treat as fatal to the whole restart cycle (see
            // CoreProcessAssignmentException's own doc comment), not just this one attempt.
            job.Dispose();
            KillOrphan(launched.ProcessId);
            launched.ProcessHandle.Dispose();
            launched.ThreadHandle.Dispose();
            throw new CoreProcessAssignmentException(
                "Failed to assign the spawned core to its supervising Job Object.", ex);
        }

        ProcessLauncher.Resume(launched);
        return new Win32CoreProcess(launched, job);
    }

    private static void KillOrphan(int processId)
    {
        try
        {
            using var orphan = Process.GetProcessById(processId);
            orphan.Kill();
        }
        catch (ArgumentException)
        {
            // Already gone - nothing to clean up.
        }
        catch (InvalidOperationException)
        {
        }
    }
}

/// <summary>
/// One core process launched by <see cref="Win32CoreProcessHost"/>. Liveness and exit are observed
/// through a SEPARATE, managed <see cref="Process"/> handle opened by PID
/// (<see cref="Process.GetProcessById(int)"/>) rather than through the raw
/// <see cref="ProcessLauncher.LaunchedProcess"/> handles - the same pattern
/// Hades.Supervision.Tests/JobObjectTests.cs already uses for its own <c>IsAlive</c> checks. This
/// gives an already-correct, fully-managed <see cref="Process.WaitForExitAsync"/> for free instead
/// of hand-rolling a <c>RegisterWaitForSingleObject</c> wrapper around the raw process handle.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32CoreProcess : ICoreProcess
{
    private readonly ProcessLauncher.LaunchedProcess _launched;
    private readonly JobObject _job;
    private readonly Process _observed;
    private bool _disposed;

    public Win32CoreProcess(ProcessLauncher.LaunchedProcess launched, JobObject job)
    {
        _launched = launched;
        _job = job;
        _observed = Process.GetProcessById(launched.ProcessId);
        Exited = _observed.WaitForExitAsync();
    }

    public int ProcessId => _launched.ProcessId;

    public bool IsRunning
    {
        get
        {
            try
            {
                return !_observed.HasExited;
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
            if (!_observed.HasExited)
            {
                _observed.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _observed.Dispose();

        // Kill-on-close backstop: if this process is somehow still alive here (Dispose called
        // without a prior Terminate call or observed exit), closing the last Job Object handle
        // ends it - see JobObject's own doc comment for why that is a backstop, not the shutdown
        // path.
        _job.Dispose();
        _launched.ProcessHandle.Dispose();
        _launched.ThreadHandle.Dispose();
    }
}
