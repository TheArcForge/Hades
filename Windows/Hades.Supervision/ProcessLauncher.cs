using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Hades.Supervision;

/// <summary>
/// Spawns a process SUSPENDED so it can be handed to a <see cref="JobObject"/> before it runs a
/// single instruction — closing the spawn-to-assign window described on <see cref="JobObject"/>.
/// <c>System.Diagnostics.Process</c> has no way to request <c>CREATE_SUSPENDED</c>, so this
/// P/Invokes <c>CreateProcessW</c> directly.
///
/// <c>bInheritHandles</c> is hard-coded to <see langword="false"/> here. That is deliberate and
/// safe for this task (no stdio redirection is wired up yet, so there is nothing that needs to be
/// inherited), but it is NOT safe to flip on its own later: turning it on would make every
/// inheritable handle in this process — including, unless something is done about it, a
/// <see cref="JobObject"/>'s own handle — visible to the child. Redirecting stdio in a future
/// change requires first scoping inheritance to exactly the redirect pipe's handles via
/// <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> in a <c>STARTUPINFOEX</c> (not the plain
/// <c>STARTUPINFOW</c> used below) — do not just flip <c>bInheritHandles</c> to
/// <see langword="true"/> without that.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class ProcessLauncher
{
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    /// <summary>
    /// The core is a CONSOLE application, so without this Windows gives it a console window - and
    /// the shell is a tray app that owns no visible window of its own, so what the user sees is a
    /// terminal appearing from nowhere, scrolling request logs, with no obvious relationship to
    /// Hades. It is also unclosable without killing the core: closing that window sends CTRL_CLOSE
    /// to the process.
    ///
    /// The Mac side never had to think about this - launching a binary from a bundle shows nothing.
    /// </summary>
    private const uint CREATE_NO_WINDOW = 0x08000000;

    public sealed record LaunchedProcess(SafeFileHandle ProcessHandle, SafeFileHandle ThreadHandle, int ProcessId);

    /// <summary>
    /// Creates <paramref name="executable"/> in a suspended state (its primary thread never runs
    /// until <see cref="Resume"/> is called). The caller is expected to assign the returned
    /// process to a <see cref="JobObject"/> before resuming it.
    /// </summary>
    public static LaunchedProcess LaunchSuspended(string executable, string arguments, string? workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        // lpApplicationName is left NULL deliberately: Windows only searches the PATH-style
        // locations (current directory, system directories, %PATH%) to resolve the executable
        // when it has to parse it out of lpCommandLine itself. Passing a non-null
        // lpApplicationName disables that search and requires an already-fully-qualified path —
        // which would break the common case this is built for (launching plain "dotnet", found
        // via PATH, for the `dotnet run` Debug path described in JobObject's own doc comment).
        var commandLine = arguments.Length == 0
            ? $"\"{executable}\""
            : $"\"{executable}\" {arguments}";

        // CreateProcessW's lpCommandLine parameter is an IN/OUT buffer, not a read-only string:
        // the API is documented to write into it (e.g. inserting a NUL to split the module name
        // from its arguments while parsing). A managed `string` must never be handed to a native
        // API that writes through the pointer it's given — .NET string objects are meant to be
        // immutable and are commonly interned/shared, so a write through would corrupt whatever
        // else in the process happens to reference the same backing memory (worst case: every use
        // of an identical string literal elsewhere in this process). To make that impossible, a
        // private char[] buffer is allocated here — owned by nothing but this call — sized for the
        // text plus a NUL terminator, and a reference to its first element is passed instead of
        // the string itself.
        var commandLineBuffer = new char[commandLine.Length + 1];
        commandLine.CopyTo(0, commandLineBuffer, 0, commandLine.Length);
        commandLineBuffer[commandLine.Length] = '\0';

        var startupInfo = new STARTUPINFOW
        {
            cb = (uint)Marshal.SizeOf<STARTUPINFOW>(),
        };

        var created = CreateProcessW(
            lpApplicationName: null,
            lpCommandLine: ref commandLineBuffer[0],
            lpProcessAttributes: IntPtr.Zero,
            lpThreadAttributes: IntPtr.Zero,
            bInheritHandles: false,
            dwCreationFlags: CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
            lpEnvironment: IntPtr.Zero,
            lpCurrentDirectory: workingDirectory,
            lpStartupInfo: ref startupInfo,
            lpProcessInformation: out var processInfo);

        if (!created)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcessW failed for '{executable}'");
        }

        var processHandle = new SafeFileHandle(processInfo.hProcess, ownsHandle: true);
        var threadHandle = new SafeFileHandle(processInfo.hThread, ownsHandle: true);
        return new LaunchedProcess(processHandle, threadHandle, (int)processInfo.dwProcessId);
    }

    /// <summary>Resumes the primary thread of a process created by <see cref="LaunchSuspended"/>.</summary>
    public static void Resume(LaunchedProcess process)
    {
        var previousSuspendCount = ResumeThread(process.ThreadHandle);
        if (previousSuspendCount == unchecked((uint)-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed");
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(
        string? lpApplicationName,
        ref char lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(SafeFileHandle hThread);

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }
}
