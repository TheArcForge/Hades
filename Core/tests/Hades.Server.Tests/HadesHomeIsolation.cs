using System.Runtime.CompilerServices;

namespace Hades.Server.Tests;

/// <summary>
/// Isolates every test host in this assembly from the real application-data root by default - see
/// <c>docs/backlog/dotnet-tests-write-to-real-app-data.md</c> for the defect this closes.
/// <c>Program.cs</c> has always honoured <c>HADES_HOME</c> (<see cref="Hades.Core.Storage.AppPaths"/>'s
/// own doc comment: "an override root so tests never touch the real application-data directory"),
/// but nothing SET it for a plain <c>dotnet test</c> run: unless the invoking shell already
/// exported it, every <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// in this assembly - and there are dozens, one per *ProgramWiringTests/*Tests class across this
/// project - built its <c>AppPaths</c> from the real default root the moment its host was first
/// touched, silently writing <c>editor.token</c>/<c>control.token</c> (both listeners start
/// unconditionally in <c>Program.cs</c>) into this machine's actual
/// <c>~/Library/Application Support/Hades</c>.
///
/// A <see cref="ModuleInitializerAttribute"/> method runs once, before any test in this assembly
/// executes - a CLR-guaranteed hook, not a race against test ordering or xUnit's parallelization -
/// so this always fires before the first <c>WebApplicationFactory&lt;Program&gt;</c> anywhere in
/// this assembly is constructed, regardless of which test class gets there first. Setting the
/// environment variable process-wide is safe specifically because of that guarantee: it happens
/// exactly once, before anything ever reads it, so there is no later mutation to race against.
///
/// <b>Deliberately does not override an already-set <c>HADES_HOME</c>.</b> A developer or CI job
/// that exported one on purpose (e.g. to point at a specific scratch directory while debugging)
/// keeps their own choice; this only supplies the default that was missing - the same thing the
/// documented workaround (<c>HADES_HOME=$(mktemp -d) dotnet test</c>) did by hand.
///
/// <b>Individual test classes that already override <c>AppPaths</c> per-instance</b> (the
/// <c>services.RemoveAll&lt;AppPaths&gt;(); services.AddSingleton(new AppPaths(...))</c> pattern
/// <see cref="EditorToolTestBase"/> and most *ProgramWiringTests classes already use) are
/// unaffected either way - their own override still wins for that instance. This closes the gap
/// for the ones that do not.
/// </summary>
internal static class HadesHomeIsolation
{
    [ModuleInitializer]
    internal static void IsolateHadesHome()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HADES_HOME"))) return;

        var isolatedRoot = Path.Combine(Path.GetTempPath(), $"hades-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolatedRoot);
        Environment.SetEnvironmentVariable("HADES_HOME", isolatedRoot);
    }
}
