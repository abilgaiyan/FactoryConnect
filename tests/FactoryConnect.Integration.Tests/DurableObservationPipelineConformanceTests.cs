using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Machines;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class DurableObservationPipelineConformanceTests
{
    [Fact]
    public async Task PipelineProcessesAcrossGapsAndRestartWithoutDuplicates()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var rawStore = new InMemoryObservationIngestionStore();
        var mappedStore = new InMemoryMappedMachineObservationSink();
        var projectionStore =
            new InMemoryMachineStateActivityProjectionStore();

        await rawStore.CommitAsync(
            new ObservationIngestionBatch(
                null,
                new ObservationCheckpoint(streamId, 7, 5),
                [
                    Sequenced(machineId, 1, "DI1", true),
                    Sequenced(machineId, 2, "UNMAPPED", true),
                    Sequenced(machineId, 3, "DI1", true),
                    Sequenced(machineId, 4, "DI1", false),
                ]));

        var first = Pipeline(
            rawStore,
            mappedStore,
            projectionStore,
            streamId);

        for (var index = 0; index < 4; index++)
        {
            Assert.True(await first.RunCycleAsync());
        }

        var mappingCheckpoint = await rawStore.ReadCheckpointAsync(
            new ObservationProcessorId("canonical-mapping"),
            streamId);
        var projection = await projectionStore.ReadAsync(
            new ObservationProcessorId("machine-state-activity"),
            streamId);
        var mapped = mappedStore.ReadObservations(streamId);
        var stateChanges = projectionStore.ReadStateChanges(
            new ObservationProcessorId("machine-state-activity"),
            streamId);
        var activities = projectionStore.ReadActivityPeriods(
            new ObservationProcessorId("machine-state-activity"),
            streamId);

        Assert.Equal(new ObservationPosition(4), mappingCheckpoint?.Position);
        Assert.Equal(new ObservationPosition(4), projection?.Position);
        Assert.Equal([1UL, 3UL, 4UL], mapped.Select(item => item.Position.Value));
        Assert.Equal(2, stateChanges.Length);
        var activity = Assert.Single(activities);
        Assert.Equal(new ObservationPosition(4), activity.Position);
        Assert.Equal(At(1), activity.Period.StartedAt);
        Assert.Equal(At(4), activity.Period.EndedAt);

        var restarted = Pipeline(
            rawStore,
            mappedStore,
            projectionStore,
            streamId);

        Assert.False(await restarted.RunCycleAsync());
        Assert.Equal(3, mappedStore.ReadObservations(streamId).Length);
        Assert.Equal(
            2,
            projectionStore.ReadStateChanges(
                new ObservationProcessorId("machine-state-activity"),
                streamId).Length);
        Assert.Single(
            projectionStore.ReadActivityPeriods(
                new ObservationProcessorId("machine-state-activity"),
                streamId));
    }

    private static DurableObservationProcessingPipeline Pipeline(
        InMemoryObservationIngestionStore rawStore,
        InMemoryMappedMachineObservationSink mappedStore,
        InMemoryMachineStateActivityProjectionStore projectionStore,
        ObservationStreamId streamId)
    {
        var options = new ObservationProcessingRuntimeOptions(
            1,
            TimeSpan.FromMilliseconds(1));
        var mapping = new MachineSignalMappingProcessor(
            new ObservationProcessorId("canonical-mapping"),
            new MachineSignalMappingConfiguration
            {
                MachineId = streamId.MachineId,
                Mappings =
                [
                    new MachineSignalMappingDefinition
                    {
                        Source = "modbus",
                        Address = "DI1",
                        SignalKey = CanonicalSignalKeys.Running,
                        Type = SignalType.Digital,
                    },
                ],
            },
            mappedStore);
        var stateActivity = new MachineStateActivityProcessor(
            new ObservationProcessorId("machine-state-activity"),
            projectionStore);

        return new DurableObservationProcessingPipeline(
            new ObservationProcessingRuntime(
                rawStore,
                rawStore,
                mapping,
                streamId,
                options),
            new MappedObservationProcessingRuntime(
                mappedStore,
                projectionStore,
                stateActivity,
                streamId,
                options),
            options.PollingInterval);
    }

    private static SequencedMachineObservation Sequenced(
        MachineId machineId,
        ulong sequence,
        string address,
        bool value) =>
        new(
            sequence,
            new MachineObservation
            {
                MachineId = machineId,
                Source = "modbus",
                Address = address,
                Type = SignalType.Digital,
                Value = value,
                Timestamp = At(sequence),
            });

    private static DateTimeOffset At(ulong minute) =>
        new(2026, 8, 26, 10, checked((int)minute), 0, TimeSpan.Zero);
}
