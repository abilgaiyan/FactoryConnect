using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class ObservationProcessingRuntimeIntegrationTests
{
    [Fact]
    public async Task RestartResumesAfterLastCommittedProcessingPosition()
    {
        var streamId = new ObservationStreamId(
            MachineId.New(),
            "MTConnect:CNC-01");
        var store = new InMemoryObservationIngestionStore();
        var processor = new RecordingProcessor();

        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                new ObservationCheckpoint(streamId, 42, 103),
                [
                    new SequencedMachineObservation(
                        101,
                        Observation(streamId.MachineId, "execution")),
                    new SequencedMachineObservation(
                        102,
                        Observation(streamId.MachineId, "load")),
                ]));

        var firstRuntime = Runtime(store, processor, streamId);

        await firstRuntime.RunCycleAsync();
        await firstRuntime.RunCycleAsync();

        var checkpoint = await store.ReadCheckpointAsync(
            processor.ProcessorId,
            streamId);

        Assert.Equal(
            new ObservationPosition(2),
            checkpoint?.Position);
        Assert.Equal(
            [101UL, 102UL],
            processor.Observations.Select(item => item.Sequence));

        var restartedRuntime = Runtime(store, processor, streamId);
        var resumedBatch = await restartedRuntime.RunCycleAsync();

        Assert.Empty(resumedBatch.Observations);
        Assert.Equal(2, processor.Observations.Count);
    }

    private static ObservationProcessingRuntime Runtime(
        InMemoryObservationIngestionStore store,
        IObservationProcessor processor,
        ObservationStreamId streamId) =>
        new(
            store,
            store,
            processor,
            streamId,
            new ObservationProcessingRuntimeOptions(
                1,
                TimeSpan.FromMilliseconds(1)));

    private static MachineObservation Observation(
        MachineId machineId,
        string address) =>
        new()
        {
            MachineId = machineId,
            Source = "MTConnect",
            Address = address,
            Type = SignalType.Enumeration,
            Value = "ACTIVE",
            Timestamp = DateTimeOffset.UnixEpoch,
        };

    private sealed class RecordingProcessor : IObservationProcessor
    {
        public ObservationProcessorId ProcessorId { get; } =
            new("machine-state");

        public List<DurableMachineObservation> Observations { get; } = [];

        public ValueTask ProcessAsync(
            IReadOnlyList<DurableMachineObservation> observations,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Observations.AddRange(observations);

            return ValueTask.CompletedTask;
        }
    }
}
