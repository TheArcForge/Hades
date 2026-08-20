using System.Net;
using System.Net.Sockets;
using Hades.Server.Control;
using Hades.Server.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hades.Server.Tests;

/// <summary>
/// Pure tests of <see cref="McpBinding.ResolveBindUrl"/> - the precedence decision behind
/// Program.cs's own <c>UseUrls</c> call. No sockets, no process; see
/// <see cref="McpBindingRealBindTests"/> for proof this decision actually reaches a real Kestrel
/// bind, and <see cref="McpBindingPortInUseTests"/> for the loud-failure path.
///
/// Context: Plan 12 Task 4 found that a spawned core with no ASPNETCORE_URLS set bound Kestrel's
/// bare default (5000) - which macOS's own ControlCenter (AirPlay Receiver) holds by default on
/// any current Mac, verified on the machine that found this bug. Every real spawn died instantly
/// with an unhandled AddressInUseException. This class is the fix: the MCP endpoint must bind its
/// fixed, documented port (7823) deliberately, the same way its own PATH is already pinned (see
/// Program.cs's own comment on MapMcp("/mcp")).
/// </summary>
public sealed class McpBindingResolveTests
{
    [Fact]
    public void NoExplicitAspNetCoreUrls_ResolvesToThePinnedMcpPort_Not5000()
    {
        Assert.Equal("http://127.0.0.1:7823", McpBinding.ResolveBindUrl(null));
        Assert.Equal($"http://127.0.0.1:{SettingsEndpoint.McpPort}", McpBinding.ResolveBindUrl(null));
    }

    [Fact]
    public void EmptyExplicitAspNetCoreUrls_IsTreatedAsAbsent_ResolvesToThePinnedMcpPort()
    {
        // ASPNETCORE_URLS="" (set but empty, distinct from unset) must not be mistaken for "bind
        // the empty string" - Kestrel refuses to start with no addresses at all.
        Assert.Equal("http://127.0.0.1:7823", McpBinding.ResolveBindUrl(""));
    }

    [Fact]
    public void ExplicitAspNetCoreUrls_AlwaysWinsOverThePinnedDefault()
    {
        // WebApplicationFactory-based tests, CI, launchSettings.json, and
        // Core/scripts/e2e-editor-attach.sh all set their own ASPNETCORE_URLS - overriding it here
        // would break every one of those callers.
        Assert.Equal("http://127.0.0.1:54321", McpBinding.ResolveBindUrl("http://127.0.0.1:54321"));
    }
}

/// <summary>
/// Pure tests of <see cref="McpBinding.ResolveBoundPort"/> - the numeric-port counterpart of
/// <see cref="McpBinding.ResolveBindUrl"/>'s own decision, so <see cref="SettingsEndpoint"/> can
/// learn which port THIS instance's MCP endpoint is actually bound to (Program.cs passes this into
/// <see cref="ControlListener"/>'s own <c>actualMcpPort</c> constructor parameter) without a
/// second, independently-maintained copy of the same "first URL wins" parsing
/// <see cref="McpBinding.DescribePortInUseFailure"/> already applies to its own message text.
/// </summary>
public sealed class McpBindingResolveBoundPortTests
{
    [Fact]
    public void SimpleUrl_ReturnsItsPort()
    {
        Assert.Equal(7823, McpBinding.ResolveBoundPort("http://127.0.0.1:7823"));
        Assert.Equal(54321, McpBinding.ResolveBoundPort("http://127.0.0.1:54321"));
    }

    [Fact]
    public void SemicolonSeparatedList_ReturnsTheFirstUrlsPort()
    {
        // Kestrel/ASPNETCORE_URLS convention: multiple bind addresses in one value, semicolon
        // separated - same "first URL wins" rule DescribePortInUseFailure already uses.
        Assert.Equal(7823, McpBinding.ResolveBoundPort("http://127.0.0.1:7823;http://127.0.0.1:8080"));
    }

    [Fact]
    public void UnparseableUrl_FallsBackToTheDocumentedMcpPort_NeverThrows()
    {
        Assert.Equal(SettingsEndpoint.McpPort, McpBinding.ResolveBoundPort("not a url"));
    }
}

/// <summary>
/// Proves <see cref="McpBinding.ResolveBindUrl"/>'s decision actually reaches a REAL Kestrel bind -
/// a directly-constructed <see cref="WebApplication"/>, deliberately never
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>, whose
/// TestServer never binds a socket at all regardless of what UseUrls says (see
/// OriginValidationTests.cs and friends for that style). Same "real listener, read the port back
/// via IServerAddressesFeature" technique <see cref="ControlListener.Start"/> itself uses -
/// see that class's own doc comment for the contrast this fixes: the control API's port is
/// deliberately ephemeral and OS-assigned, because nothing declares it statically; the MCP
/// endpoint's port must be the opposite, because a client (the Claude Code plugin) does.
/// </summary>
public sealed class McpBindingRealBindTests
{
    /// <summary>Proves the app's own real bind reaches port 7823 when nothing else holds it -
    /// SKIPPING, not failing, when something already does. Port 7823 is Hades' fixed, documented
    /// MCP port (see this class's own doc comment); it is deterministically held whenever the real
    /// app is running, which is the app's entire INTENDED state (launches at login, stays up) -
    /// see docs/backlog/dotnet-tests-write-to-real-app-data.md item 2 for the full history. A real
    /// bind is this test's whole reason to exist (<see cref="McpBinding.ResolveBindUrl"/> already
    /// has its own pure, socket-free tests in <see cref="McpBindingResolveTests"/> above - this
    /// class's unique value is proving that string actually reaches a live Kestrel bind), so there
    /// is no way to prove that without attempting a real one. Skipping on exactly the collision a
    /// healthy running app produces - rather than failing `dotnet test` deterministically for
    /// every developer who has Hades open, which trains people to ignore a red suite - preserves
    /// the real assertion for the cases that actually exercise it: CI, and a fresh checkout or a
    /// machine where nothing is running yet.
    ///
    /// <para><b>Why an early return, not xUnit's own dynamic-skip API.</b> xUnit v2.9+ has a real
    /// mechanism for exactly this (<c>throw Xunit.Sdk.SkipException.ForSkip(reason)</c>) - tried
    /// first, and reverted: verified empirically against this project's actual pinned versions
    /// (xunit.extensibility.core/assert 2.9.3, but xunit.runner.visualstudio 3.1.4 - a v3-era
    /// adapter) that the resulting run is reported as FAILED, with the literal
    /// <c>$XunitDynamicSkip$</c> marker token leaking into the console error message instead of
    /// being recognized and translated to a Skipped result. That is the exact failure mode this
    /// fix exists to remove, so shipping it would be worse than not trying. An early return with a
    /// loud <see cref="Console.WriteLine(string)"/> - the same pattern every
    /// <c>RealProject*SmokeTest.cs</c> in this directory already uses for "not exercised on this
    /// run" - reports a genuine, verified-working Passed rather than a false Failed, and the
    /// console line makes clear it did not actually exercise the bind.</para></summary>
    [Fact]
    public async Task NoExplicitAspNetCoreUrls_TheRealAppActuallyBindsPort7823()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(McpBinding.ResolveBindUrl(null));
        await using var app = builder.Build();

        try
        {
            app.Start();
        }
        catch (IOException ex) when (ex.InnerException is AddressInUseException)
        {
            // Same exception shape McpBinding.Run itself matches (see that method's own doc
            // comment) - Kestrel wraps the real bind failure in an IOException.
            Console.WriteLine("[McpBindingRealBindTests] SKIPPED the real bind assertion: port 7823 " +
                "is already in use - most likely the real Hades app is running, which is its normal, " +
                "healthy state. Quit Hades (or run this on a machine/CI job where nothing is using " +
                "7823) to actually exercise this test.");
            return;
        }

        try
        {
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses;
            Assert.Equal(new Uri($"http://127.0.0.1:{SettingsEndpoint.McpPort}"), new Uri(addresses.Single()));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task ExplicitAspNetCoreUrls_TheRealAppBindsThatAddressInstead_Not7823()
    {
        // A bound-then-immediately-released ephemeral port, same "free at the moment we asked"
        // pattern SettingsTests.cs's own SettingsBuildTests uses - never the real 7823, so this
        // cannot collide with a real Hades instance on the machine running the test.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var explicitUrl = $"http://127.0.0.1:{freePort}";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(McpBinding.ResolveBindUrl(explicitUrl));
        await using var app = builder.Build();

        app.Start();
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses;
            Assert.Equal(new Uri(explicitUrl), new Uri(addresses.Single()));
        }
        finally
        {
            await app.StopAsync();
        }
    }
}

/// <summary>
/// The loud-failure path - spec #1 §8's error-handling standard ("specific and actionable
/// improvements, rather than opaque error codes or tracebacks"), applied here to startup instead
/// of a tool call. Occupies the port FIRST, exactly Plan 12 Task 4's own repro instruction ("Test
/// it by occupying 7823 first"), then proves the raw
/// <see cref="Microsoft.AspNetCore.Connections.AddressInUseException"/> (wrapped by Kestrel in an
/// <see cref="IOException"/> - verified empirically against this exact SDK) never reaches a caller
/// of <see cref="McpBinding.Run"/> unwrapped.
///
/// <see cref="Microsoft.Extensions.Logging.ILoggingBuilder.ClearProviders"/> below is a
/// TEST-ONLY convenience (keeps this test's own console output clean); it is not what makes the
/// real fix work. The real fix - suppressing ASP.NET Core's own "Hosting failed to start" log so
/// it never prints the raw trace in the first place - is Program.cs's own narrowly-targeted
/// <c>builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None)</c>
/// call, verified by the live run recorded in this task's own final report rather than by a test
/// here: a proactive check that could have been unit-tested directly raced catastrophically across
/// the dozens of concurrent WebApplicationFactory-backed Program.cs instances `dotnet test` itself
/// constructs and was reverted for exactly that reason - see McpBinding's own class doc comment.
/// </summary>
public sealed class McpBindingPortInUseTests
{
    [Fact]
    public async Task PortAlreadyInUse_ThrowsASpecificActionableException_NotARawAddressInUseException()
    {
        using var occupier = new TcpListener(IPAddress.Loopback, 0);
        occupier.Start();
        var occupiedPort = ((IPEndPoint)occupier.LocalEndpoint).Port;
        var boundUrl = $"http://127.0.0.1:{occupiedPort}";

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); // keep test output free of the framework's own bind-failure log line
        builder.WebHost.UseUrls(boundUrl);
        await using var app = builder.Build();

        var ex = Assert.Throws<McpPortBindException>(() => McpBinding.Run(app, boundUrl));

        Assert.Equal(
            $"Hades could not start: port {occupiedPort} is already in use. The MCP endpoint binds this port "
            + "deliberately and always, so Claude Code and other MCP clients can find it at a fixed address — "
            + $"Hades will not silently switch to a different one. Find and stop whatever is using port {occupiedPort} "
            + $"(`lsof -nP -iTCP:{occupiedPort} -sTCP:LISTEN`), or set ASPNETCORE_URLS to run Hades at a different address.",
            ex.Message);

        // Never a raw, un-actionable trace - the real exception is available (InnerException), not lost.
        Assert.IsType<IOException>(ex.InnerException);
    }

    [Fact]
    public void DescribePortInUseFailure_NamesTheRealPortFromTheUrl_NotAHardcodedLiteral()
    {
        // Pure test of the message-building logic in isolation, same "never depends on a real
        // socket" discipline SettingsResolveTests.cs uses for SettingsEndpoint.Resolve.
        var message = McpBinding.DescribePortInUseFailure("http://127.0.0.1:9999");

        Assert.Contains("port 9999", message);
        Assert.DoesNotContain("7823", message);
    }
}
