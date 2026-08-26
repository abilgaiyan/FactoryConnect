using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class MappedObservationReadConformanceTests
{
    [Fact]
    public async Task ReaderReturnsOrderedPagesAcrossPositionGaps()
    {
        var streamId = Stream();
        var store = new InMemoryMappedMachineObservationSink();
        await store.WriteAsync(
            [
                Observation(streamId, 5),
                Observation(streamId, 1),
                Observation(streamId, 3),
            ]);

        var first = await store.ReadAsync(
            new MappedObservationReadRequest(streamId, null, 2));
        var second = await store.ReadAsync(
            new MappedObservationReadRequest(
                streamId,
                first.Observations[^1].Position,
                2));

        Assert.Equal(
            [1UL, 3UL],
            first.Observations.Select(item => item.Position.Value));
        Assert.True(first.HasMore);
        Assert.Equal(
            [5UL],
            second.Observations.Select(item => item.Position.Value));
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task ReaderAcceptsMaximumBatchSizeWithoutOverflow()
    {
        var streamId = Stream();
        var store = new InMemoryMappedMachineObservationSink();
        await store.WriteAsync([Observation(streamId, 1)]);

        var batch = await store.ReadAsync(
            new MappedObservationReadRequest(
                streamId,
                null,
                int.MaxValue));

        Assert.Single(batch.Observations);
        Assert.False(batch.HasMore);
    }

    [Fact]
    public void ReadBatchRejectsDuplicateDurablePositions()
    {
        var streamId = Stream();
        var duplicate = Observation(streamId, 1);

        Assert.Throws<ArgumentException>(
            () => new MappedObservationReadBatch(
                streamId,
                [duplicate, duplicate],
                false));
    }

    private static ObservationStreamId Stream() =>
        new(MachineId.New(), "modbus:line-1");

    private static DurableMappedMachineObservation Observation(
        ObservationStreamId streamId,
        ulong position) =>
        new(
            new ObservationPosition(position),
            streamId,
            1,
            position,
            new MappedMachineObservation
            {
                MachineId = streamId.MachineId,
                SignalKey = CanonicalSignalKeys.Running,
                Type = SignalType.Digital,
                Value = true,
                Source = "modbus",
                Address = "DI1",
                Timestamp = DateTimeOffset.UnixEpoch,
            });
}
