using Hades.Control.Client;

namespace Hades.Control.Client.Tests;

public class DiscoveryTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ReadsPortAndToken()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "control.token"), """{"port":54321,"token":"abc"}""");

        var connection = Discovery.Read(_root);

        Assert.NotNull(connection);
        Assert.Equal(54321, connection.Port);
        Assert.Equal("abc", connection.Token);
    }

    [Fact]
    public void ReturnsNullWhenTheFileIsAbsent()
    {
        Assert.Null(Discovery.Read(_root));
    }

    [Fact]
    public void ReturnsNullOnMalformedContent()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "control.token"), "not json");

        Assert.Null(Discovery.Read(_root));
    }

    [Fact]
    public void ReturnsNullOnJsonMissingRequiredFields()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "control.token"), """{"port":1}""");

        Assert.Null(Discovery.Read(_root));
    }
}
