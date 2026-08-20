using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

namespace Hades.Server.Tests;

/// <summary>
/// Proves the ordering fix for the Plan 13 Task 8 spawn-loop bug at the real-process level: the
/// control listener - the ONLY signal <c>CoreSupervisor.spawnOnce()</c> uses to decide "did the
/// spawn succeed" (Swift, Mac/HadesSupervision, not exercised here) - must never become
/// reachable for a core whose MCP bind is about to fail. Before the fix, Program.cs called
/// <c>controlListener.Start()</c> unconditionally, before <c>McpBinding.Run</c> ever
/// attempted the fixed-port Kestrel bind that was about to fail - so a core that could never
/// actually finish starting still wrote a real, briefly-live discovery file. A supervisor that
/// polls and happens to land inside that window observes a genuine ping success for a process
/// that is, at that exact moment, already doomed - see <see cref="McpBindingPortInUseTests"/> for
/// the isolated bind-failure message this same conflict produces.
///
/// This launches the REAL, already-built <c>Hades.Server.dll</c> as a REAL OS process - never
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>, whose
/// TestServer never touches a real socket and so can never reproduce a real bind conflict for the
/// MCP endpoint (see <see cref="McpBindingRealBindTests"/>'s own doc comment for that same
/// distinction) - against a real, pre-occupied ephemeral port (never the literal 7823, so this
/// can never collide with a real Hades instance on the machine running the test). This exercises
/// Program.cs's actual compiled statement order, not a hand-reconstructed approximation of it.
/// </summary>
public sealed class ProgramStartupOrderingTests
{
    // Hades.Server.Tests references Hades.Server as a ProjectReference, so the compiled DLL this
    // test process is ALREADY running against is copied right next to this test assembly's own
    // output - no path-guessing needed, and it is always the exact bits `dotnet test` just built.
    static readonly string ServerDllPath = typeof(Program).Assembly.Location;

    static (Process process, string hadesHome) LaunchServer(string aspNetCoreUrls)
    {
        var hadesHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(hadesHome);

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(ServerDllPath),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(ServerDllPath);
        startInfo.Environment["ASPNETCORE_URLS"] = aspNetCoreUrls;
        startInfo.Environment["HADES_HOME"] = hadesHome;

        var process = new Process { StartInfo = startInfo };
        process.Start();
        // Drained asynchronously via event handlers, never left unread: a redirected pipe that
        // nobody reads can fill its OS buffer and block the child process's own writes, which
        // would hang this test's wait below on the child itself, not just on a slow assertion.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return (process, hadesHome);
    }

    /// <summary>Polls for <paramref name="path"/> to exist, bounded - never an unbounded wait.
    /// This project already hit a real `swift test` hang from an unbounded
    /// `Process.waitUntilExit()` and fixed it with bounded polling (see
    /// Mac/HadesSupervision/Tests' own `ReaperForceKillTests` comment on the same standard);
    /// this is that same discipline applied on the .NET side.</summary>
    static async Task<bool> WaitForFile(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)) return true;
            await Task.Delay(20);
        }
        return File.Exists(path);
    }

    [Fact]
    public async Task McpBindFails_ControlListenerNeverWritesADiscoveryFile_NeverAdvertisesReadiness()
    {
        using var occupier = new TcpListener(IPAddress.Loopback, 0);
        occupier.Start();
        var occupiedPort = ((IPEndPoint)occupier.LocalEndpoint).Port;

        var (process, hadesHome) = LaunchServer($"http://127.0.0.1:{occupiedPort}");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Handled by the HasExited assertion below - a doomed core hanging instead of
                // exiting is itself a failure worth reporting clearly, not an unbounded wait.
            }

            Assert.True(process.HasExited,
                "the doomed core must exit on its own (Environment.Exit(1)) within 30s, never hang");
            Assert.Equal(1, process.ExitCode);

            var tokenPath = Path.Combine(hadesHome, "control.token");
            Assert.False(File.Exists(tokenPath),
                "the control listener must never advertise readiness (write its discovery file) " +
                "for a core whose MCP bind failed - readiness must mean fully started");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            Directory.Delete(hadesHome, recursive: true);
        }
    }

    [Fact]
    public async Task McpBindSucceeds_ControlListenerBecomesReachable()
    {
        // A bound-then-immediately-released ephemeral port - same "free at the moment we asked"
        // pattern McpBindingRealBindTests uses - never the real 7823.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var (process, hadesHome) = LaunchServer($"http://127.0.0.1:{freePort}");
        try
        {
            var tokenPath = Path.Combine(hadesHome, "control.token");
            var appeared = await WaitForFile(tokenPath, TimeSpan.FromSeconds(30));
            Assert.True(appeared, "a healthy core must eventually advertise readiness");
            Assert.False(process.HasExited, "a healthy core must not have exited");

            var payload = await File.ReadAllTextAsync(tokenPath);
            using var doc = JsonDocument.Parse(payload);
            var port = doc.RootElement.GetProperty("port").GetInt32();
            var token = doc.RootElement.GetProperty("token").GetString();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var request = new HttpRequestMessage(HttpMethod.Get, "/control/ping");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            Directory.Delete(hadesHome, recursive: true);
        }
    }
}
