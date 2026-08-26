using System.Text.Json;
using System.Text.Json.Serialization;
using Hades.Control.Client;

namespace Hades.Control.Client.Tests;

public class UnknownFallbackConverterTests
{
    [JsonConverter(typeof(UnknownFallbackConverter<Sample>))]
    public enum Sample { Unknown, Idle, Indexing }

    [Fact]
    public void DecodesAKnownValue()
    {
        Assert.Equal(Sample.Indexing, JsonSerializer.Deserialize<Sample>("\"indexing\""));
    }

    [Fact]
    public void DecodesAnUnknownValueToTheFallbackInsteadOfThrowing()
    {
        Assert.Equal(Sample.Unknown, JsonSerializer.Deserialize<Sample>("\"teleporting\""));
    }

    [Fact]
    public void IsCaseInsensitiveLikeTheWire()
    {
        Assert.Equal(Sample.Idle, JsonSerializer.Deserialize<Sample>("\"Idle\""));
    }

    [Fact]
    public void DecodesANonStringTokenToTheFallbackRatherThanThrowing()
    {
        Assert.Equal(Sample.Unknown, JsonSerializer.Deserialize<Sample>("42"));
    }

    [Fact]
    public void RoundTripsThroughCamelCaseOnTheWire()
    {
        Assert.Equal("\"idle\"", JsonSerializer.Serialize(Sample.Idle));
    }
}
