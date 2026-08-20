using Hades.Server.Control;

namespace Hades.Cli.Tests;

/// <summary>
/// Proves <see cref="Discovery.Read"/> reads exactly what a real <see cref="ControlListener"/>
/// writes - not a hand-rolled JSON fixture string of this test's own that could silently drift from
/// the server's actual wire shape (<c>ControlAuth.WriteConnectionFile</c>). This is the CLI's half
/// of Plan 11 Task 7's own requirement: "discover the port and token the same way the Swift app
/// will... do not hardcode."
/// </summary>
public sealed class DiscoveryTests : IDisposable
{
    readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    string ConnectionFilePath => Path.Combine(_tempDir, "control.token");

    [Fact]
    public void NoFileYet_ReturnsNull_HadesNotRunningIsAnOrdinaryCondition_NotAnException()
    {
        Assert.Null(Discovery.Read(ConnectionFilePath));
    }

    [Fact]
    public void RealControlListener_TheFileItActuallyWrites_RoundTripsThePortAndToken()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();

        var connection = Discovery.Read(ConnectionFilePath);

        Assert.NotNull(connection);
        Assert.Equal(listener.Port, connection!.Port);
        Assert.Equal(listener.Token, connection.Token);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}
