using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using LaunchedFakeCore = Hades.Supervision.ProcessLauncher.LaunchedProcess;

namespace Hades.Supervision.Tests;

// Exercises JobObject + ProcessLauncher against a REAL Windows process (FakeCore —
// Windows/FakeCore/Program.cs — a stand-in for the actual Hades core, built exactly for use by
// this test project).
//
// HONESTY NOTE: these tests P/Invoke real Win32 APIs that do not exist on macOS. Until 2026-08-29
// none of the code below had executed anywhere and it was compile-and-review verified only; on
// that date all three ran on a real Windows 11 machine and passed. ci.yml's `dotnet test (Windows
// solution)` step runs them on windows-latest as well, though as of this writing that job has
// never actually completed a run. They are gated with
// [Trait(PlatformTraits.Key, PlatformTraits.Windows)] rather than an early-return specifically so
// that stays honest: `dotnet test` on macOS (or any non-Windows CI runner) must EXCLUDE them
// outright rather than report a vacuous pass — see PlatformTraits.cs for why traits, not skips.
[SupportedOSPlatform("windows")]
public class JobObjectTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    [Trait(PlatformTraits.Key, PlatformTraits.Windows)]
    public void Closing_the_job_terminates_a_healthy_member_process()
    {
        using var workingDir = new TempDirectory();
        var launched = LaunchFakeCoreSuspended(workingDir.Path);
        using var job = new JobObject();

        job.Assign(launched.ProcessHandle);
        ProcessLauncher.Resume(launched);

        Assert.True(WaitUntil(() => IsAlive(launched.ProcessId), Timeout),
            "FakeCore should be alive after Resume");

        // This is the behaviour under test: kill-on-close means DISPOSING the job handle — not
        // sending any signal to the child directly — is what kills it. This is the Windows
        // equivalent of Mac's HadesCoreReaper firing kill(-pgid, SIGKILL) after its parent
        // (the app) disappears.
        job.Dispose();

        Assert.True(WaitUntil(() => !IsAlive(launched.ProcessId), Timeout),
            "JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE should terminate the job's member process when the last job handle closes");
    }

    [Fact]
    [Trait(PlatformTraits.Key, PlatformTraits.Windows)]
    public void Process_is_already_a_job_member_before_it_runs_its_first_instruction()
    {
        using var workingDir = new TempDirectory();
        var launched = LaunchFakeCoreSuspended(workingDir.Path);
        using var job = new JobObject();

        // The process is still suspended here — CreateProcessW returned it in that state and
        // Resume has not been called yet — so this assignment happens before the process (or
        // anything it might fork) has executed a single instruction. This is precondition 1 from
        // JobObject's own doc comment: assignment does not retroactively capture existing
        // descendants, so the fix is ordering (CREATE_SUSPENDED -> Assign -> Resume), not a race
        // against however fast the process happens to start running.
        job.Assign(launched.ProcessHandle);

        // KNOWN WEAKNESS, recorded rather than silently relied upon: passing NULL asks "is this
        // process in ANY job?", not "is it in OUR job?" — JobObject._handle is private, so asking
        // the precise question would mean widening production API surface purely for a test. A
        // process created by a process that is itself in a job is auto-assigned to that job, so on
        // a host where the test runner is already jobbed (CI runners and some terminals do this)
        // this assertion can pass without job.Assign having done anything. What keeps the suite
        // honest is Closing_the_job_terminates_a_healthy_member_process: kill-on-close can only
        // fire on a genuine member of the job we created, so that test proves the assignment this
        // one merely observes. If this pair ever disagrees, believe that test, not this one.
        Assert.True(
            NativeTestInterop.IsProcessInJob(launched.ProcessHandle, jobHandle: IntPtr.Zero, out var isMember),
            "IsProcessInJob should succeed");
        Assert.True(isMember, "the process must already be a job member while still suspended, before Resume is ever called");

        ProcessLauncher.Resume(launched);
        Assert.True(WaitUntil(() => IsAlive(launched.ProcessId), Timeout));

        job.Dispose();
        Assert.True(WaitUntil(() => !IsAlive(launched.ProcessId), Timeout));
    }

    [Fact]
    [Trait(PlatformTraits.Key, PlatformTraits.Windows)]
    public void Healthy_child_keeps_running_while_the_job_handle_stays_open()
    {
        using var workingDir = new TempDirectory();
        var launched = LaunchFakeCoreSuspended(workingDir.Path);
        using var job = new JobObject();

        job.Assign(launched.ProcessHandle);
        ProcessLauncher.Resume(launched);

        Assert.True(WaitUntil(() => IsAlive(launched.ProcessId), Timeout));

        // Merely holding the job open must never kill a member on its own — kill-on-close is a
        // backstop that fires only when the LAST handle closes (JobObject's precondition 4: this
        // is a force-quit backstop, not something that fires spontaneously while the owner is
        // alive and doing nothing in particular).
        Thread.Sleep(TimeSpan.FromSeconds(2));
        Assert.True(IsAlive(launched.ProcessId), "a healthy job member must survive merely because the job handle is open");

        // Cleanup happens via the `using var job` disposal below (kill-on-close), exercised
        // separately by the other two tests above — this test's own assertion is only about the
        // "still alive" state, so nothing further is asserted after this point.
    }

    private static LaunchedFakeCore LaunchFakeCoreSuspended(string appDataRoot)
    {
        var dllPath = FindFakeCoreDll();
        var launched = ProcessLauncher.LaunchSuspended("dotnet", $"\"{dllPath}\" \"{appDataRoot}\"", workingDirectory: null);
        return launched;
    }

    /// <summary>
    /// Locates FakeCore's build output as a sibling of this test project's own output, rather than
    /// hardcoding a build configuration — both projects sit directly under Windows/ and build to
    /// the same relative `bin/&lt;Configuration&gt;/&lt;TFM&gt;/` shape.
    /// </summary>
    private static string FindFakeCoreDll()
    {
        var testOutputDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfmDir = new DirectoryInfo(testOutputDir);
        var configuration = tfmDir.Parent?.Name ?? throw new DirectoryNotFoundException($"Unexpected test output layout: {testOutputDir}");
        // .../Hades.Supervision.Tests/bin/<Configuration>/<TFM>/ -> .../Windows/
        var windowsRoot = tfmDir.Parent?.Parent?.Parent?.Parent
            ?? throw new DirectoryNotFoundException($"Unexpected test output layout: {testOutputDir}");

        var candidate = Path.Combine(windowsRoot.FullName, "FakeCore", "bin", configuration, tfmDir.Name, "FakeCore.dll");
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException(
                $"FakeCore.dll not found at '{candidate}' — build Windows/FakeCore before running these tests.", candidate);
        }

        return candidate;
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No such process — it has already exited and been reaped.
            return false;
        }
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return condition();
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hades-jobobject-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup only; a lingering temp directory is not worth failing a test over.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// The one extra Win32 call these tests need beyond what JobObject/ProcessLauncher already expose
/// in production code: a read-only membership check used purely to assert precondition 1
/// (assign-before-resume) actually holds. Kept test-local rather than added to JobObject's public
/// surface because the supervisor (next task) never needs to ask this question at runtime.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class NativeTestInterop
{
    // jobHandle is IntPtr rather than SafeFileHandle because Win32 documents it as [in, optional]
    // and the caller passes NULL to ask "is this process in ANY job?". [LibraryImport]'s generated
    // stub marshals a SafeHandle through SafeHandleMarshaller<T>.ManagedToUnmanagedIn.FromManaged,
    // which dereferences unconditionally and throws NullReferenceException on null; the `?`
    // annotation is not honoured by the generator. IntPtr.Zero is the only way to express the
    // documented NULL here.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsProcessInJob(SafeFileHandle processHandle, IntPtr jobHandle, [MarshalAs(UnmanagedType.Bool)] out bool result);
}
