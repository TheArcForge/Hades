using Hades.Server.Control;

namespace Hades.Server.Tests.Control;

/// <summary>
/// Shared by every test in this folder that resolves <see cref="ControlListener"/> straight from a
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>-backed DI
/// container and needs its real bound <see cref="ControlListener.Port"/> right away. Program.cs
/// starts that listener off <c>app.Lifetime.ApplicationStarted.Register(controlListener.Start)</c>
/// - deliberately, so the app never advertises readiness before its control listener is actually
/// up (see Program.cs's own comment on that line) - which means Port is not guaranteed to be
/// assigned yet at the exact moment a test's own thread first reads it. Reading it too early
/// observes 0, and "http://127.0.0.1:0" fails HttpClient with SocketException ("Can't assign
/// requested address") rather than anything that names the real cause - a different test each run,
/// since which one loses the race is timing-dependent. Port flips from 0 to its real value exactly
/// once and never back, so a short bounded poll - not a second event to hook - is enough to close
/// the race without touching Program.cs's own deliberate ordering.
///
/// Only these DI-resolved-listener tests need this. The direct-construction test classes earlier in
/// these same files (e.g. ControlAuthTests, above ControlListenerProgramWiringTests) call
/// <c>listener.Start()</c> themselves, synchronously, in-test - by the time that call returns, Port
/// is already set, so their own <c>ClientFor</c> helpers read it safely with no wait.
/// </summary>
static class ProgramWiringPort
{
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Polls <paramref name="listener"/>.Port until it is non-zero and returns it, or
    /// throws a <see cref="TimeoutException"/> naming exactly what this waited for once
    /// <paramref name="timeout"/> (default 10s - generous next to how fast ApplicationStarted
    /// actually fires once it fires at all) elapses.</summary>
    public static async Task<int> WaitAsync(ControlListener listener, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var deadline = DateTime.UtcNow + limit;

        while (listener.Port == 0)
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"ControlListener.Port was still 0 after waiting {limit.TotalSeconds:F0}s for " +
                    "Program.cs's ApplicationStarted callback to run ControlListener.Start(). Either " +
                    "the app failed to start, or this environment is far slower than this timeout assumes.");
            }

            await Task.Delay(10);
        }

        return listener.Port;
    }
}
