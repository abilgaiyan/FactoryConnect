using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class OperationalMetricWitnessNodePathCodecTests
{
    [Fact]
    public void RootAndNestedPathsHaveExactNetworkOrderBytes()
    {
        Assert.Equal("00000000", Convert.ToHexString(OperationalMetricWitnessNodePathCodec.Encode([])));
        Assert.Equal("0000000100000000", Convert.ToHexString(OperationalMetricWitnessNodePathCodec.Encode([0])));
        Assert.Equal("0000000201020304FFFFFFFF",
            Convert.ToHexString(OperationalMetricWitnessNodePathCodec.Encode([0x01020304, uint.MaxValue])));
        Assert.Equal(new uint[] { 0x01020304, uint.MaxValue },
            OperationalMetricWitnessNodePathCodec.Decode(Convert.FromHexString("0000000201020304FFFFFFFF")));
        Assert.Empty(OperationalMetricWitnessNodePathCodec.Decode(Convert.FromHexString("00000000")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("000000")]
    [InlineData("0000000000")]
    [InlineData("00000001")]
    [InlineData("00000001000000")]
    [InlineData("000000010000000000")]
    [InlineData("0100000000000000")]
    [InlineData("FFFFFFFF")]
    public void MalformedCountsTruncationAndTrailingBytesAreRejected(string hex)
    {
        Assert.Throws<InvalidDataException>(() => OperationalMetricWitnessNodePathCodec.Decode(Convert.FromHexString(hex)));
    }

    [Fact]
    public void MaximumDepthFitsSqlPathAndExcessDepthIsRejectedBeforeAllocation()
    {
        var ordinals = Enumerable.Range(0, 63).Select(static value => (uint)value).ToArray();
        var encoded = OperationalMetricWitnessNodePathCodec.Encode(ordinals);
        Assert.Equal(256, encoded.Length);
        Assert.Equal(ordinals, OperationalMetricWitnessNodePathCodec.Decode(encoded));
        Assert.Throws<ArgumentOutOfRangeException>(() => OperationalMetricWitnessNodePathCodec.Encode(new uint[64]));
        Assert.Throws<InvalidDataException>(() => OperationalMetricWitnessNodePathCodec.Decode(new byte[257]));
        encoded[3] = 64;
        Assert.Throws<InvalidDataException>(() => OperationalMetricWitnessNodePathCodec.Decode(encoded));
    }
}
