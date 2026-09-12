using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

public sealed class MappingCoverageCommitTests
{
    [Fact]
    public void CommitRejectsMappedHighWaterBeyondRawCoverage()
    {
        var (processorId, streamId) = Identity();

        Assert.Throws<ArgumentException>(() =>
            new MappingCoverageCommit(
                null,
                processorId,
                streamId,
                new ObservationPosition(4),
                new ObservationPosition(5)));
    }

    [Fact]
    public void CommitRejectsExpectedAuthorityFromAnotherIdentity()
    {
        var (processorId, streamId) = Identity();
        var expected = new MappingCoverageAuthority(
            new ObservationProcessorId("other-mapper"),
            streamId,
            new ObservationPosition(3),
            new ObservationPosition(2),
            new MappingAuthorityRevision(0));

        Assert.Throws<ArgumentException>(() =>
            new MappingCoverageCommit(
                expected,
                processorId,
                streamId,
                new ObservationPosition(4),
                new ObservationPosition(2)));
    }

    [Fact]
    public void CommitRejectsRawCoverageRegression()
    {
        var (processorId, streamId) = Identity();
        var expected = Authority(processorId, streamId, raw: 5, mapped: 4);

        Assert.Throws<ArgumentException>(() =>
            new MappingCoverageCommit(
                expected,
                processorId,
                streamId,
                new ObservationPosition(4),
                new ObservationPosition(4)));
    }

    [Fact]
    public void CommitRejectsMappedHighWaterRegression()
    {
        var (processorId, streamId) = Identity();
        var expected = Authority(processorId, streamId, raw: 5, mapped: 4);

        Assert.Throws<ArgumentException>(() =>
            new MappingCoverageCommit(
                expected,
                processorId,
                streamId,
                new ObservationPosition(6),
                new ObservationPosition(3)));
    }

    [Fact]
    public void CommitAllowsUnmappedRawProgressWithoutInventingMappedCoverage()
    {
        var (processorId, streamId) = Identity();
        var expected = Authority(processorId, streamId, raw: 5, mapped: 3);

        var commit = new MappingCoverageCommit(
            expected,
            processorId,
            streamId,
            new ObservationPosition(8),
            new ObservationPosition(3));

        Assert.Equal(new ObservationPosition(8), commit.RawConsumedThrough);
        Assert.Equal(
            new ObservationPosition(3),
            commit.MappedEvaluationInputHighWater);
    }

    [Fact]
    public void CommitAllowsNoMappedHighWaterBeforeAnyCanonicalOutputExists()
    {
        var (processorId, streamId) = Identity();

        var commit = new MappingCoverageCommit(
            null,
            processorId,
            streamId,
            new ObservationPosition(8),
            null);

        Assert.Null(commit.MappedEvaluationInputHighWater);
    }

    [Fact]
    public void CommitRetainsEarlierExpectedAuthorityForStaleExactReplay()
    {
        var (processorId, streamId) = Identity();
        var expectedBeforeFirstAttempt = Authority(
            processorId,
            streamId,
            raw: 5,
            mapped: 3);

        var retry = new MappingCoverageCommit(
            expectedBeforeFirstAttempt,
            processorId,
            streamId,
            new ObservationPosition(8),
            new ObservationPosition(6));

        Assert.Same(expectedBeforeFirstAttempt, retry.ExpectedAuthority);
        Assert.Equal(new ObservationPosition(8), retry.RawConsumedThrough);
        Assert.Equal(
            new ObservationPosition(6),
            retry.MappedEvaluationInputHighWater);
    }

    private static MappingCoverageAuthority Authority(
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        ulong raw,
        ulong mapped) =>
        new(
            processorId,
            streamId,
            new ObservationPosition(raw),
            new ObservationPosition(mapped),
            new MappingAuthorityRevision(0));

    private static (ObservationProcessorId ProcessorId, ObservationStreamId StreamId)
        Identity()
    {
        var machineId = MachineId.New();
        return (
            new ObservationProcessorId("canonical-signals"),
            new ObservationStreamId(machineId, "modbus:line-1"));
    }
}
