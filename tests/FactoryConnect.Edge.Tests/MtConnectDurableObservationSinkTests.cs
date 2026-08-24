using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using FactoryConnect.Protocols.MTConnect;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class MtConnectDurableObservationSinkTests
{
    [Fact]
    public void StreamIdentityNormalizesDeviceKey()
    {
        var machineId = MachineId.New();

        var first = MtConnectObservationStreamId.Create(
            machineId,
            " CNC-01 ");
        var second = MtConnectObservationStreamId.Create(
            machineId,
            "cnc-01");

        Assert.Equal(first, second);
        Assert.Equal("mtconnect:CNC-01", first.StreamKey);
    }

    [Fact]
    public async Task WriteAsyncCommitsObservationsAndCheckpoint()
    {
        var store = new InMemoryObservationIngestionStore();
        var machineId = MachineId.New();
        var streamId = MtConnectObservationStreamId.Create(
            machineId,
            "CNC-01");
        var sink = new MtConnectDurableObservationSink(
            store,
            streamId);

        await sink.WriteAsync(
            Result(machineId, 42, 103, 101, 102),
            null);

        var checkpoint = await store.ReadCheckpointAsync(streamId);

        Assert.Equal(42UL, checkpoint?.InstanceId);
        Assert.Equal(103UL, checkpoint?.NextSequence);
        Assert.Equal(2, store.ReadObservations(streamId).Length);
    }

    [Fact]
    public async Task WriteAsyncAllowsEmptyResultToAdvanceCheckpoint()
    {
        var store = new InMemoryObservationIngestionStore();
        var machineId = MachineId.New();
        var streamId = MtConnectObservationStreamId.Create(
            machineId,
            "CNC-01");
        var sink = new MtConnectDurableObservationSink(
            store,
            streamId);

        await sink.WriteAsync(
            Result(machineId, 42, 103, 101, 102),
            null);

        var current = await store.ReadCheckpointAsync(streamId);

        await sink.WriteAsync(
            Result(machineId, 42, 111),
            current);

        Assert.Equal(
            111UL,
            (await store.ReadCheckpointAsync(streamId))?.NextSequence);
        Assert.Equal(2, store.ReadObservations(streamId).Length);
    }

    [Fact]
    public async Task WriteAsyncPropagatesStaleCheckpointWithoutMutation()
    {
        var store = new InMemoryObservationIngestionStore();
        var machineId = MachineId.New();
        var streamId = MtConnectObservationStreamId.Create(
            machineId,
            "CNC-01");
        var sink = new MtConnectDurableObservationSink(
            store,
            streamId);

        await sink.WriteAsync(
            Result(machineId, 42, 103, 101, 102),
            null);

        var stale = new ObservationCheckpoint(
            streamId,
            42,
            100);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.WriteAsync(
                Result(machineId, 43, 2, 1),
                stale).AsTask());

        var checkpoint = await store.ReadCheckpointAsync(streamId);

        Assert.Equal(42UL, checkpoint?.InstanceId);
        Assert.Equal(103UL, checkpoint?.NextSequence);
        Assert.Equal(2, store.ReadObservations(streamId).Length);
    }

    private static MtConnectSampleResult Result(
        MachineId machineId,
        ulong instanceId,
        ulong nextSequence,
        params ulong[] sequences)
    {
        return new MtConnectSampleResult
        {
            InstanceId = instanceId,
            FirstSequence = sequences.Length == 0
                ? nextSequence
                : sequences.Min(),
            LastSequence = sequences.Length == 0
                ? nextSequence
                : sequences.Max(),
            NextSequence = nextSequence,
            Observations = sequences
                .Select(sequence => new MtConnectSampleObservation
                {
                    Sequence = sequence,
                    Observation = new MachineObservation
                    {
                        MachineId = machineId,
                        Source = "MTConnect",
                        Address = $"data-item-{sequence}",
                        Type = SignalType.Text,
                        Value = "ACTIVE",
                        Timestamp = DateTimeOffset.UnixEpoch,
                    },
                })
                .ToArray(),
        };
    }
}
