using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class OperationalMetricContainmentIdentityTests
{
    [Fact]
    public void DigestDomainsIncludeOneTerminatingZeroAndRemainDistinct()
    {
        // Independent Python hashlib probes, not canonical FCWE/FCDS envelopes.
        // This test certifies digest construction only, never source authority.
        var bytes = Convert.FromHexString("01020300FF");
        Assert.Equal("073E96C656E532EE7442C26DBA1D71FFAE14EB8FD3E761DFA7A0E9A7E394F853",
            Convert.ToHexString(OperationalMetricContainmentIdentity.ComputeWitnessId(bytes)));
        Assert.Equal("526ACE60829D5621663D7F60CCCE506A4408D59180D715D0E046EFCE8C5C21A9",
            Convert.ToHexString(OperationalMetricContainmentIdentity.ComputeResultId(bytes)));
        Assert.Equal("3F623C4FF8AE55CB87D9B0BCC3987A8CA30EC95CF4B33E8C9AEF9A7973DD74C1",
            Convert.ToHexString(OperationalMetricContainmentIdentity.ComputeDependencyHash(bytes)));
    }

    [Fact]
    public void EmptyAndOversizedEnvelopesAreRejectedInEveryDomain()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OperationalMetricContainmentIdentity.ComputeWitnessId([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => OperationalMetricContainmentIdentity.ComputeResultId([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => OperationalMetricContainmentIdentity.ComputeDependencyHash([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => OperationalMetricContainmentIdentity.ComputeWitnessId(
            new byte[OperationalMetricContainmentIdentity.MaximumWitnessLength + 1]));
        var oversized = new byte[OperationalMetricContainmentIdentity.MaximumResultLength + 1];
        Assert.Throws<ArgumentOutOfRangeException>(() => OperationalMetricContainmentIdentity.ComputeResultId(oversized));
        Assert.Throws<ArgumentOutOfRangeException>(() => OperationalMetricContainmentIdentity.ComputeDependencyHash(oversized));
    }

    [Fact]
    public void ExactMaximumEnvelopeLengthsAreAccepted()
    {
        Assert.Equal(32, OperationalMetricContainmentIdentity.ComputeWitnessId(
            new byte[OperationalMetricContainmentIdentity.MaximumWitnessLength]).Length);
        var bytes = new byte[OperationalMetricContainmentIdentity.MaximumResultLength];
        Assert.Equal(32, OperationalMetricContainmentIdentity.ComputeResultId(bytes).Length);
        Assert.Equal(32, OperationalMetricContainmentIdentity.ComputeDependencyHash(bytes).Length);
    }
}
