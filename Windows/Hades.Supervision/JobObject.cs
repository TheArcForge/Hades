using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Hades.Supervision;

/// <summary>
/// A Windows Job Object configured with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> — the Windows
/// equivalent of Mac/HadesSupervision/Sources/HadesCoreReaper: it guarantees a process assigned to
/// it is terminated the moment the LAST handle to the job closes, including when that happens
/// because the owning process died (crash, Task Manager "End Task", SIGKILL-equivalent) and never
/// got a chance to run any cleanup code of its own. Unlike the Mac reaper, no separate watchdog
/// process is needed: the kernel does this unconditionally once a handle it were the last is gone.
///
/// Two invariants the OWNER of a <see cref="JobObject"/> instance must uphold — this class cannot
/// enforce either one from the inside:
///
///  1. Keep the returned instance (and therefore its handle) ROOTED for exactly as long as the
///     supervised core should be allowed to run. A handle that becomes eligible for finalization
///     closes the job and kills a perfectly healthy core mid-session — the failure mode this class
///     exists to prevent, self-inflicted.
///  2. Never let a second handle to the same job outlive this one (e.g. via
///     <c>DuplicateHandle</c> or opening it by name). Any other surviving handle means the job
///     does not actually close — and therefore does not kill anything — when this instance is
///     disposed, silently voiding the whole guarantee. <c>CreateJobObjectW(NULL, ...)</c> below
///     already returns an unnamed, non-inheritable handle, which is the safe default for this.
///
/// Assigning a process to the job (<see cref="Assign"/>) is only half of closing the
/// spawn-to-supervision gap: the process must also have been created SUSPENDED
/// (<see cref="ProcessLauncher.LaunchSuspended"/>) so assignment happens before it — or anything it
/// might fork — has run a single instruction. Assignment does not retroactively capture existing
/// descendants of an already-running process.
///
/// This is a force-quit BACKSTOP, not the shutdown path: kill-on-close is the moral equivalent of
/// TerminateProcess — abrupt, no chance for the core to save state or close connections. A graceful
/// stop (send a stop request, wait, then fall back to this) belongs in the supervisor that owns
/// this instance, not here.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class JobObject : IDisposable
{
    // JOBOBJECT_INFORMATION_CLASS: JobObjectExtendedLimitInformation (winnt.h / joblimit APIs).
    private const int JobObjectExtendedLimitInformation = 9;

    // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE (winnt.h).
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private readonly SafeFileHandle _handle;
    private bool _disposed;

    public JobObject()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "CreateJobObjectW failed");
        }

        try
        {
            SetKillOnJobClose(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        _handle = handle;
    }

    /// <summary>
    /// Assigns <paramref name="processHandle"/> to this job, so kill-on-close covers it.
    ///
    /// This can fail even on modern Windows: <c>AssignProcessToJobObject</c> can return
    /// <c>ERROR_ACCESS_DENIED</c> when job-hierarchy nesting rules can't be satisfied — inside some
    /// sandboxes, silo/container hosts, or corporate launcher wrappers that already placed this
    /// process (or the target) in an incompatible job. That case must be loud, not swallowed: an
    /// unsupervised core that can outlive its parent is worse than no core at all, so this throws
    /// rather than letting the caller proceed believing the safety net is in place when it is not.
    /// The caller (the supervisor) is expected to treat that as fatal to the spawn attempt.
    /// </summary>
    public void Assign(SafeFileHandle processHandle)
    {
        if (!AssignProcessToJobObject(_handle, processHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }

    private static unsafe void SetKillOnJobClose(SafeFileHandle handle)
    {
        var info = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        var ok = SetInformationJobObject(
            handle,
            JobObjectExtendedLimitInformation,
            (IntPtr)(&info),
            (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));

        if (!ok)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed");
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeFileHandle hJob,
        int jobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle hJob, SafeFileHandle hProcess);

    // Struct layouts below must match winnt.h EXACTLY — field order, size and type — because
    // SetInformationJobObject reads/writes this memory by raw offset with no type checking on
    // either side of the P/Invoke boundary. A wrong field order or width does not throw or fail
    // fast: it silently misinterprets which bytes mean what, e.g. writing LimitFlags into what the
    // kernel reads as half of PerJobUserTimeLimit. SIZE_T / ULONG_PTR map to `nuint` (native,
    // pointer-width, unsigned) — using `uint` here would be right on 32-bit Windows and wrong (and
    // silently misaligned) on 64-bit.

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit; // LARGE_INTEGER
        public long PerJobUserTimeLimit;     // LARGE_INTEGER
        public uint LimitFlags;              // DWORD
        public nuint MinimumWorkingSetSize;  // SIZE_T
        public nuint MaximumWorkingSetSize;  // SIZE_T
        public uint ActiveProcessLimit;      // DWORD
        public nuint Affinity;               // ULONG_PTR
        public uint PriorityClass;           // DWORD
        public uint SchedulingClass;         // DWORD
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;  // ULONGLONG
        public ulong WriteOperationCount; // ULONGLONG
        public ulong OtherOperationCount; // ULONGLONG
        public ulong ReadTransferCount;   // ULONGLONG
        public ulong WriteTransferCount;  // ULONGLONG
        public ulong OtherTransferCount;  // ULONGLONG
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;     // SIZE_T
        public nuint JobMemoryLimit;         // SIZE_T
        public nuint PeakProcessMemoryUsed;  // SIZE_T
        public nuint PeakJobMemoryUsed;      // SIZE_T
    }
}
