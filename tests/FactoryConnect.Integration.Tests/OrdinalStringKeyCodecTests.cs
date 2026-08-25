using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class OrdinalStringKeyCodecTests
{
    [Fact]
    public void EncodingPreservesOrdinalDistinctions()
    {
        AssertDistinct("A", "a");
        AssertDistinct("A", "A ");
        AssertDistinct("é", "e\u0301");
        AssertDistinct("A\0B", "A\0C");
    }

    [Fact]
    public void EncodingRoundTripsSupplementaryCharacters()
    {
        const string value = "machine-😀-stream";

        var encoded = OrdinalStringKeyCodec.Encode(value);
        var decoded = OrdinalStringKeyCodec.Decode(encoded);

        Assert.Equal(value, decoded);
    }

    [Fact]
    public void EncodingRoundTripsUnpairedSurrogateCodeUnits()
    {
        var value = new string(['\uD800', 'A', '\uDC00']);

        var encoded = OrdinalStringKeyCodec.Encode(value);
        var decoded = OrdinalStringKeyCodec.Decode(encoded);

        Assert.Equal(value, decoded);
    }

    [Fact]
    public void EncodingRejectsKeysLongerThanProviderLimit()
    {
        var value = new string('x', OrdinalStringKeyCodec.MaxCodeUnits + 1);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => OrdinalStringKeyCodec.Encode(value));
    }

    private static void AssertDistinct(string first, string second)
    {
        var firstBytes = OrdinalStringKeyCodec.Encode(first);
        var secondBytes = OrdinalStringKeyCodec.Encode(second);

        Assert.False(firstBytes.SequenceEqual(secondBytes));
    }
}
