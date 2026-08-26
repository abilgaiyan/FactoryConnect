using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Machines;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class DurableObservationCheckpointFailureConformanceTests
{
    [Fact]
    public async Task CanonicalWriteFollowedByCheckpointFailureRetriesWithoutDuplicateOutput()
    {
        var streamId = new ObservationStreamId(
            MachineId.New(),
            "modbus:line-1");
        var rawStore = new InMemoryObservationIngestionStore();
        var checkpointStore = new FailOnceProcessingCheckpointStore(rawStore);
        var mappedStore = new InMemoryMappedMachineObservationSink();
        var projectionStore =
            new InMemoryMachineStateActivityProjectionStore();

        await rawStore.CommitAsync(
            new ObservationIngestionBatch(
                null,
                new ObservationCheckpoint(streamId, 7, 2),
                [Sequenced(streamId.MachineId)]));

        var pipeline = Pipeline(
            rawStore,
            checkpointStore,
            mappedStore,
            projectionStore,
            streamId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.RunCycleAsync());

        Assert.Single(mappedStore.ReadObservations(streamId));
        Assert.Null(
            await rawStore.ReadCheckpointAsync(
                new ObservationProcessorId("canonical-mapping"),
                streamId));

        Assert.True(await pipeline.RunCycleAsync());

        Assert.Single(mappedStore.ReadObservations(streamId));
        Assert.Equal(
            new ObservationPosition(1),
            (await rawStore.ReadCheckpointAsync(
                new ObservationProcessorId("canonical-mapping"),
                streamId))?.Position);
        Assert.Equal(
            new ObservationPosition(1),
            (await projectionStore.ReadAsync(
                new ObservationProcessorId("machine-state-activity"),
                streamId))?.Position);
        Assert.Single(
            projectionStore.ReadStateChanges(
                new ObservationProcessorId("machine-state-activity"),
                streamId));
    }

    private static DurableObservationProcessingPipeline Pipeline(
        InMemoryObservationIngestionStore rawStore,
        IObservationProcessingCheckpointStore checkpointStore,
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
                checkpointStore,
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
        MachineId machineId) =>
        new(
            1,
            new MachineObservation
            {
                MachineId = machineId,
                Source = "modbus",
                Address = "DI1",
                Type = SignalType.Digital,
                Value = true,
                Timestamp = DateTimeOffset.UnixEpoch,
            });

    private sealed class FailOnceProcessingCheckpointStore(
        IObservationProcessingCheckpointStore inner)
        : IObservationProcessingCheckpointStore
    {
        private bool _shouldFail = true;

        public ValueTask<ObservationProcessingCheckpoint?> ReadCheckpointAsync(
            ObservationProcessorId processorId,
            ObservationStreamId streamId,
            CancellationToken cancellationToken = default) =>
            inner.ReadCheckpointAsync(
                processorId,
                streamId,
                cancellationToken);

        public ValueTask CommitAsync(
            ObservationProcessingCommit commit,
            CancellationToken cancellationToken = default)
        {
            if (_shouldFail)
            {
                _shouldFail = false;
                throw new InvalidOperationException(
                    "Simulated processing-checkpoint failure.");
            }

            return inner.CommitAsync(commit, cancellationToken);
        }
    }
}
