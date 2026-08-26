using FactoryConnect.Abstractions;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class ObservationProcessingContractTests
{
    [Fact]
    public void ObservationPositionRejectsZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ObservationPosition(0));
    }

    [Fact]
    public void ObservationPositionDefinesCompleteOrdering()
    {
        var first = new ObservationPosition(1);
        var equivalent = new ObservationPosition(1);
        var second = new ObservationPosition(2);

        Assert.True(first < second);
        Assert.True(first <= second);
        Assert.True(second > first);
        Assert.True(second >= first);
        Assert.True(first <= equivalent);
        Assert.True(first >= equivalent);
    }

    [Fact]
    public void ObservationProcessorIdPreservesOrdinalIdentity()
    {
        var upper = new ObservationProcessorId("Machine-State");
        var lower = new ObservationProcessorId("machine-state");

        Assert.NotEqual(upper, lower);
        Assert.Equal("Machine-State", upper.Value);
    }

    [Fact]
    public void DurableObservationRejectsDifferentMachine()
    {
        var streamId = StreamId();
        var observation = Observation(MachineId.New());

        Assert.Throws<ArgumentException>(
            () => new DurableMachineObservation(
                new ObservationPosition(1),
                streamId,
                42,
                100,
                observation));
    }

    [Fact]
    public void ReadRequestRequiresPositiveBatchSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ObservationReadRequest(StreamId(), null, 0));
    }

    [Fact]
    public void ReadBatchTakesDefensiveOrderedSnapshot()
    {
        var streamId = StreamId();
        var observations = new List<DurableMachineObservation>
        {
            DurableObservation(streamId, 1),
            DurableObservation(streamId, 2),
        };

        var batch = new ObservationReadBatch(
            streamId,
            observations,
            hasMore: true);
        observations.Clear();

        Assert.Equal(2, batch.Observations.Count);
        Assert.True(batch.HasMore);
    }

    [Fact]
    public void ReadBatchRejectsNonIncreasingPositions()
    {
        var streamId = StreamId();

        Assert.Throws<ArgumentException>(
            () => new ObservationReadBatch(
                streamId,
                [
                    DurableObservation(streamId, 2),
                    DurableObservation(streamId, 1),
                ],
                hasMore: false));
    }

    [Fact]
    public void ReadBatchRejectsObservationFromAnotherStream()
    {
        var streamId = StreamId();
        var otherStreamId = new ObservationStreamId(
            streamId.MachineId,
            "MTConnect:CNC-02");

        Assert.Throws<ArgumentException>(
            () => new ObservationReadBatch(
                streamId,
                [DurableObservation(otherStreamId, 1)],
                hasMore: false));
    }

    [Fact]
    public void ProcessingCommitRejectsDifferentProcessor()
    {
        var streamId = StreamId();
        var expected = Checkpoint("machine-state", streamId, 1);
        var checkpoint = Checkpoint("metric-input", streamId, 2);

        Assert.Throws<ArgumentException>(
            () => new ObservationProcessingCommit(
                expected,
                checkpoint));
    }

    [Fact]
    public void ProcessingCommitRejectsDifferentStream()
    {
        var streamId = StreamId();
        var otherStreamId = new ObservationStreamId(
            streamId.MachineId,
            "MTConnect:CNC-02");
        var expected = Checkpoint("machine-state", streamId, 1);
        var checkpoint = Checkpoint(
            "machine-state",
            otherStreamId,
            2);

        Assert.Throws<ArgumentException>(
            () => new ObservationProcessingCommit(
                expected,
                checkpoint));
    }

    [Fact]
    public void ProcessingCommitRejectsPositionRegression()
    {
        var streamId = StreamId();
        var expected = Checkpoint("machine-state", streamId, 2);
        var checkpoint = Checkpoint("machine-state", streamId, 1);

        Assert.Throws<ArgumentException>(
            () => new ObservationProcessingCommit(
                expected,
                checkpoint));
    }

    private static ObservationProcessingCheckpoint Checkpoint(
        string processorId,
        ObservationStreamId streamId,
        ulong position) =>
        new(
            new ObservationProcessorId(processorId),
            streamId,
            new ObservationPosition(position));

    private static DurableMachineObservation DurableObservation(
        ObservationStreamId streamId,
        ulong position) =>
        new(
            new ObservationPosition(position),
            streamId,
            42,
            position + 100,
            Observation(streamId.MachineId));

    private static ObservationStreamId StreamId() =>
        new(MachineId.New(), "MTConnect:CNC-01");

    private static MachineObservation Observation(MachineId machineId) =>
        new()
        {
            MachineId = machineId,
            Source = "MTConnect",
            Address = "execution",
            Type = SignalType.Enumeration,
            Value = "ACTIVE",
            Timestamp = DateTimeOffset.UnixEpoch,
        };
}
