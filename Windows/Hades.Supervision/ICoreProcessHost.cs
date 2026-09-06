namespace Hades.Supervision;

/// <summary>
/// The platform-neutral seam between <see cref="CoreSupervisor"/>'s adopt-or-spawn/backoff/
/// stable-uptime decision logic and the OS-specific mechanics of actually starting a process and
/// guaranteeing it cannot outlive its parent.
///
/// This split exists because <see cref="JobObject"/> and <see cref="ProcessLauncher"/> P/Invoke
/// <c>kernel32.dll</c> directly and throw <see cref="System.DllNotFoundException"/> on any
/// non-Windows machine, while none of <see cref="CoreSupervisor"/>'s own decisions ("is this core
/// already answering", "has it been up long enough to trust", "how many attempts are left") have
/// any OS dependency at all - they are exactly the kind of logic
/// Mac/HadesSupervision/Sources/HadesSupervision/CoreSupervisor.swift already proves is worth
/// testing on its own, in-process, with no real OS process involved. Routing every process
/// interaction through this interface keeps CoreSupervisor.cs itself free of P/Invoke, so it (and
/// therefore the adopt/backoff/uptime/ownership behaviour this port cares about) compiles AND RUNS
/// on macOS - only <see cref="Win32CoreProcessHost"/>, the real implementation of this interface,
/// needs actual Windows to execute.
/// </summary>
public interface ICoreProcessHost
{
    /// <summary>
    /// Spawns a process for <paramref name="startInfo"/> with the guarantee that it cannot
    /// outlive the calling process - see <see cref="JobObject"/>'s own doc comment for how the
    /// real implementation gives that guarantee (create suspended, assign to a Job Object, then
    /// resume).
    /// </summary>
    /// <exception cref="CoreProcessAssignmentException">
    /// Thrown when the supervision guarantee itself could not be established - see that type's
    /// own doc comment for why this is fatal rather than an ordinary, retryable spawn failure.
    /// </exception>
    ICoreProcess Spawn(CoreProcessStartInfo startInfo);
}

/// <summary>
/// Everything <see cref="ICoreProcessHost.Spawn"/> needs to launch the core - the platform-neutral
/// equivalent of Swift's <c>Configuration.coreExecutable</c>/<c>coreArguments</c>/
/// <c>extraEnvironment</c> trio, minus the reaper executable Windows has no equivalent of: the
/// kernel itself (via the Job Object) plays that role here, so there is no separate watchdog
/// binary to point at - see <see cref="JobObject"/>'s own doc comment.
/// </summary>
public sealed record CoreProcessStartInfo(
    string Executable,
    string Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// A single spawned-and-supervised core process, as seen by <see cref="CoreSupervisor"/>.
/// Deliberately narrow - just enough surface for adopt/spawn/restart/backoff, nothing a caller
/// could use to bypass the supervision guarantee <see cref="ICoreProcessHost.Spawn"/> already
/// established.
/// </summary>
public interface ICoreProcess : IDisposable
{
    int ProcessId { get; }

    bool IsRunning { get; }

    /// <summary>
    /// Completes exactly once, whenever this process exits for any reason - the .NET analogue of
    /// Swift's <c>Process.terminationHandler</c>. Only ever awaited by <see cref="CoreSupervisor"/>
    /// for a spawn attempt that actually succeeded - see that type's own comment on why abandoned
    /// or failed attempts are never wired up to this at all, unlike the Swift original (which
    /// registers a termination handler on every attempt and instead filters stale firings by
    /// process identity - see its own <c>handleCoreProcessExit</c> doc comment).
    /// </summary>
    Task Exited { get; }

    /// <summary>
    /// Ends the process right now. Used both to abandon a spawn attempt that never answered
    /// <c>/control/ping</c> in time, and by <see cref="CoreSupervisor"/>'s own <c>Stop()</c> to end
    /// a spawned core deliberately.
    /// </summary>
    void Terminate();
}

/// <summary>
/// Thrown when a process was created but the "cannot outlive its parent" guarantee could not be
/// attached to it (on Windows: <c>AssignProcessToJobObject</c> failing - see
/// <see cref="JobObject.Assign"/>'s own doc comment for real-world causes, e.g. sandboxed or
/// container-hosted job-nesting restrictions).
///
/// This is deliberately its own exception type, not folded into an ordinary "spawn failed" return
/// value: an ordinary launch failure (bad path, missing executable) is safe to retry with backoff
/// like any other transient problem, but a process that exists and is running WITHOUT the
/// supervision guarantee is actively dangerous. An unsupervised core that can outlive its parent is
/// worse than no core at all - so <see cref="CoreSupervisor"/> treats this type as fatal to the
/// entire restart cycle rather than one more attempt to burn through the backoff budget on.
/// </summary>
public sealed class CoreProcessAssignmentException : Exception
{
    public CoreProcessAssignmentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
