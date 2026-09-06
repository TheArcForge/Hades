using Microsoft.AspNetCore.Mvc.Testing;

namespace Hades.Server.Tests;

/// <summary>
/// Disposes a test host and WAITS for its shutdown to finish, which
/// <see cref="System.IDisposable.Dispose"/> alone does not do.
///
/// <para><b>The bug this exists for.</b> Every test class here builds a per-test
/// <see cref="WebApplicationFactory{TEntryPoint}"/>, disposes it, then recursively deletes its temp
/// directories. Program.cs starts real background services unconditionally - EditorListener's
/// accept loop, ControlListener's, and ObservationService's periodic sweep - and those shut down
/// ASYNCHRONOUSLY. The synchronous <c>Dispose()</c> returns before they have actually stopped, so
/// the delete can run while a sweep still holds <c>graph.db</c> (or a <c>.project.json.*.tmp</c>)
/// open.</para>
///
/// <para><b>Why it looked like a flake for months and then became deterministic.</b> The two
/// platforms fail this differently, from the one cause:</para>
/// <list type="bullet">
/// <item>On macOS/Linux, deleting a file another handle has open SUCCEEDS - the directory entry is
/// unlinked and the file lives until the last handle closes. The delete only fails when a still-
/// running sweep CREATES an entry underneath a directory mid-delete, which surfaces as
/// <c>IOException: "Directory not empty"</c> in a different class each time - the ~1-in-2-4
/// teardown flake <see cref="TeardownDiagnostics"/> was written to investigate.</item>
/// <item>On Windows, an open handle blocks the delete outright, so the same race is not a race at
/// all: it fails every run with <c>IOException: "The process cannot access the file 'graph.db'
/// because it is being used by another process"</c>. Six tests in InspectToolTests and
/// QueryToolsTests did exactly that on the first Windows CI run this repo has ever done.</item>
/// </list>
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
