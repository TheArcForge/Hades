using Microsoft.AspNetCore.Mvc.Testing;

namespace Hades.Server.Tests;

/// <summary>
/// Disposes a test host and WAITS for its shutdown to finish, which
/// <see cref="System.IDisposable.Dispose"/> alone does not do.
///
/// <para><b>What this is, and what it is not.</b> Every test class here builds a per-test
/// <see cref="WebApplicationFactory{TEntryPoint}"/>, disposes it, then recursively deletes its temp
/// directories. Program.cs starts real background services unconditionally, and they shut down
/// ASYNCHRONOUSLY; the synchronous <c>Dispose()</c> returns before they have stopped. Waiting here
/// is therefore correct - but on its own it did NOT fix the teardown failures, and the CI run that
/// proved it is why this paragraph exists. The actual cause was one layer down:
/// <c>ObservationService.Dispose()</c> did not wait for a sweep already in flight (disposing a
/// Timer or a FileSystemWatcher does not wait for a running callback), so a sweep could still hold
/// <c>graph.db</c> and a <c>.project.json.*.tmp</c> open after the host had finished shutting down.
/// That is fixed at the source, in <c>ObservationService</c>, and covered by
/// <c>ObservationServiceTests.DisposeWaitsForASweepThatIsAlreadyRunning</c>. This helper stays
/// because awaiting the host's own shutdown is the right thing for a test to do regardless.</para>
///
/// <para><b>Why the same bug looked like two different bugs.</b> On macOS/Linux, deleting a file
/// another handle has open SUCCEEDS - the directory entry is unlinked and the file lives until the
/// last handle closes - so the delete only failed when a still-running sweep CREATED an entry
/// underneath a directory mid-delete, surfacing as <c>IOException: "Directory not empty"</c> in a
/// different class each time. That is the ~1-in-2-4 flake <see cref="TeardownDiagnostics"/> was
/// written to investigate; 14 consecutive full-suite runs after the ObservationService fix
/// produced none. On Windows an open handle blocks the delete outright, so the same race was not a
/// race at all: six tests failed every run with <c>IOException: "The process cannot access the
/// file 'graph.db' because it is being used by another process"</c>.</para>
///
/// <para><b>Why sync-over-async is acceptable here, specifically.</b> Blocking on an async call is
/// normally how you deadlock: the continuation needs the thread the caller is holding. That needs a
/// synchronization context to capture, and xUnit's test host does not install one, so the
/// continuation runs on the thread pool and the wait completes. This is also teardown - no test
/// body is waiting on this thread. The alternative, converting every affected class to
/// <c>IAsyncLifetime</c>, is the more correct shape and also changes how each class expresses its
/// constructor setup; that is a larger refactor than the defect warrants.</para>
/// </summary>
internal static class HostTeardown
{
    /// <summary>Call this instead of <c>factory.Dispose()</c> anywhere a recursive delete of the
    /// host's directories follows.</summary>
    public static void DisposeBlocking<TEntryPoint>(this WebApplicationFactory<TEntryPoint> factory)
        where TEntryPoint : class
        => factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
