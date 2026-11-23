using FactoryConnect.Core.Machines;

namespace FactoryConnect.Core.Tests;

public sealed class CounterIdentityAssessorTests
{
    [Fact]
    public void MissingConfigurationAndNoObservationsAreDistinct()
    {
        var signals = new[] { new CounterSignal("mtconnect", "count") };
        Assert.Equal(CounterIdentityDisposition.NoConfiguredDataItem,
            CounterIdentityAssessor.Assess(null, signals).Disposition);
        Assert.Equal(CounterIdentityDisposition.NoDiscoveredSignals,
            CounterIdentityAssessor.Assess("count", []).Disposition);
        Assert.Throws<ArgumentException>(() => CounterIdentityAssessor.Assess(" ", signals));
    }

    [Fact]
    public void LiteralAddressComparisonDoesNotInventMapping()
    {
        var result = CounterIdentityAssessor.Assess("count",
            [new CounterSignal("mtconnect", "COUNT")]);
        Assert.Equal(CounterIdentityDisposition.NoExactAddressCandidate, result.Disposition);
        Assert.Empty(result.Candidates);
        Assert.False(result.IdentityVerified);
        Assert.False(result.ProductionQuantityAdmitted);
    }

    [Fact]
    public void SingleCandidateIsNeverVerifiedOrAdmitted()
    {
        var result = CounterIdentityAssessor.Assess("count",
            [new CounterSignal("mtconnect", "count")]);
        Assert.Equal(CounterIdentityDisposition.SingleUnverifiedCandidate, result.Disposition);
        Assert.Single(result.Candidates);
        Assert.False(result.IdentityVerified);
        Assert.False(result.ProductionQuantityAdmitted);
    }

    [Fact]
    public void MultipleSourcesRemainAmbiguousAndDeterministic()
    {
        var result = CounterIdentityAssessor.Assess("count",
            [new CounterSignal("source-b", "count"), new CounterSignal("source-a", "count")]);
        Assert.Equal(CounterIdentityDisposition.AmbiguousExactAddressCandidates, result.Disposition);
        Assert.Equal("source-a", result.Candidates[0].Source);
        Assert.Equal("source-b", result.Candidates[1].Source);
        Assert.False(result.IdentityVerified);
        Assert.False(result.ProductionQuantityAdmitted);
    }
}
