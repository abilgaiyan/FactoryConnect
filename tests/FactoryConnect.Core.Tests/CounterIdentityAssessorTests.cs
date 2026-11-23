using FactoryConnect.Core.Machines;
using FactoryConnect.Abstractions;

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

    private static readonly MachineId Machine = new(Guid.Parse("6d0878b0-24b4-4cc8-a67d-4b4aab1e608f"));
    private static readonly ObservationStreamId Stream = new(Machine, "mtconnect:M01");
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

    private static DurableMachineObservation Raw(ulong position, string source, string address,
        SignalType type = SignalType.Numeric, DateTimeOffset? at = null) =>
        new(new ObservationPosition(position), Stream, 1, position, new MachineObservation
        {
            MachineId = Machine, Source = source, Address = address, Type = type,
            Value = 10m, Timestamp = at ?? Start.AddMinutes(position)
        });

    [Fact]
    public void DurablePrefixDiscoversAmbiguityWithoutVerifyingIdentity()
    {
        var result = CounterIdentityAssessor.AssessDurablePrefix("count", Stream,
            new ObservationPosition(4), Start, Start.AddHours(1),
            [Raw(1, "adapter-a", "count"), Raw(2, "adapter-b", "count"),
             Raw(3, "adapter-c", "count", SignalType.Text), Raw(4, "adapter-d", "other")]);
        Assert.Equal(CounterIdentityDisposition.AmbiguousExactAddressCandidates, result.Disposition);
        Assert.Equal(2, result.Candidates.Count);
        Assert.False(result.IdentityVerified);
        Assert.False(result.ProductionQuantityAdmitted);
    }

    [Fact]
    public void DurablePrefixRejectsOutOfOrderOrWrongStreamEvidence()
    {
        Assert.Throws<InvalidDataException>(() => CounterIdentityAssessor.AssessDurablePrefix(
            "count", Stream, new ObservationPosition(2), Start, Start.AddHours(1),
            [Raw(2, "adapter-a", "count"), Raw(1, "adapter-a", "count")]));
        Assert.Throws<InvalidDataException>(() => CounterIdentityAssessor.AssessDurablePrefix(
            "count", Stream, new ObservationPosition(1), Start, Start.AddHours(1),
            [Raw(2, "adapter-a", "count")]));
    }

    [Fact]
    public void DurablePrefixRespectsSelectedIntervalAndSignalTypes()
    {
        var result = CounterIdentityAssessor.AssessDurablePrefix("count", Stream,
            new ObservationPosition(3), Start, Start.AddHours(1),
            [Raw(1, "adapter-a", "count", at: Start.AddMinutes(-1)),
             Raw(2, "adapter-a", "count", SignalType.Text),
             Raw(3, "adapter-b", "count", at: Start.AddHours(1))]);
        Assert.Equal(CounterIdentityDisposition.NoDiscoveredSignals, result.Disposition);
        Assert.False(result.ProductionQuantityAdmitted);
    }
}
