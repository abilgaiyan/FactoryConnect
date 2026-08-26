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

    [Fact]
    public async Task CanonicalSinkFailureDoesNotAdvanceRawProcessingProgress()
    {
        var streamId = new ObservationStreamId(
            MachineId.New(),
            "modbus:line-1");
        var rawStore = new InMemoryObservationIngestionStore();
        var mappedStore = new FailOnceMappedStore();
        var projectionStore =
            new InMemoryMachineStateActivityProjectionStore();

        await rawStore.CommitAsync(
            new ObservationIngestionBatch(
                null,
                new ObservationCheckpoint(streamId, 7, 2),
                [Sequenced(streamId.MachineId, 1, "DI1", true)]));

        var pipeline = Pipeline(
            rawStore,
            mappedStore,
            mappedStore,
            projectionStore,
            streamId,
            "DI1",
            CanonicalSignalKeys.Running);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.RunCycleAsync());

        Assert.Null(
            await rawStore.ReadCheckpointAsync(
                new ObservationProcessorId("canonical-mapping"),
                streamId));
        Assert.Empty(mappedStore.Inner.ReadObservations(streamId));

        Assert.True(await pipeline.RunCycleAsync());

        Assert.Equal(
            new ObservationPosition(1),
            (await rawStore.ReadCheckpointAsync(
                new ObservationProcessorId("canonical-mapping"),
                streamId))?.Position);
        Assert.Single(mappedStore.Inner.ReadObservations(streamId));
    }

    [Fact]
    public async Task ProjectionFailureLeavesCanonicalObservationEligibleForRetry()
    {
        var streamId = new ObservationStreamId(
            MachineId.New(),
            "modbus:line-1");
        var rawStore = new InMemoryObservationIngestionStore();
        var mappedStore = new InMemoryMappedMachineObservationSink();
        var projectionStore = new FailOnceProjectionStore();

        await rawStore.CommitAsync(
            new ObservationIngestionBatch(
                null,
                new ObservationCheckpoint(streamId, 7, 2),
                [Sequenced(streamId.MachineId, 1, "DI1", true)]));

        var pipeline = Pipeline(
            rawStore,
            mappedStore,
            mappedStore,
            projectionStore,
            streamId,
            "DI1",
            CanonicalSignalKeys.Running);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.RunCycleAsync());

        Assert.Equal(
            new ObservationPosition(1),
            (await rawStore.ReadCheckpointAsync(
                new ObservationProcessorId("canonical-mapping"),
                streamId))?.Position);
        Assert.Single(mappedStore.ReadObservations(streamId));
        Assert.Null(
            await projectionStore.Inner.ReadAsync(
                new ObservationProcessorId("machine-state-activity"),
                streamId));

        Assert.True(await pipeline.RunCycleAsync());

        Assert.Equal(
            new ObservationPosition(1),
            (await projectionStore.Inner.ReadAsync(
                new ObservationProcessorId("machine-state-activity"),
                streamId))?.Position);
        Assert.Single(mappedStore.ReadObservations(streamId));
        Assert.Single(
            projectionStore.Inner.ReadStateChanges(
                new ObservationProcessorId("machine-state-activity"),
                streamId));
    }

    [Fact]
    public async Task MultipleStreamsRestartWithIndependentMappingsAndProgress()
    {
        var firstStream = new ObservationStreamId(
            MachineId.New(),
            "modbus:line-1");
        var secondStream = new ObservationStreamId(
            MachineId.New(),
            "modbus:line-2");
        var rawStore = new InMemoryObservationIngestionStore();
        var mappedStore = new InMemoryMappedMachineObservationSink();
        var projectionStore =
            new InMemoryMachineStateActivityProjectionStore();

        await rawStore.CommitAsync(
            new ObservationIngestionBatch(
                null,
                new ObservationCheckpoint(firstStream, 1, 2),
                [Sequenced(firstStream.MachineId, 1, "DI1", true)]));
        await rawStore.CommitAsync(
            new ObservationIngestionBatch(
                null,
                new ObservationCheckpoint(secondStream, 1, 2),
                [Sequenced(secondStream.MachineId, 1, "X1", true)]));

        var first = PipelineSet(
            rawStore,
            mappedStore,
            projectionStore,
            firstStream,
            secondStream);

        Assert.True(await first.RunCycleAsync());

        Assert.Equal(
            CanonicalSignalKeys.Running,
            Assert.Single(mappedStore.ReadObservations(firstStream))
                .Observation.SignalKey);
        Assert.Equal(
            CanonicalSignalKeys.PowerOn,
            Assert.Single(mappedStore.ReadObservations(secondStream))
                .Observation.SignalKey);
        Assert.Equal(
            new ObservationPosition(1),
            (await rawStore.ReadCheckpointAsync(
                new ObservationProcessorId("canonical-mapping"),
                firstStream))?.Position);
        Assert.Equal(
            new ObservationPosition(1),
            (await rawStore.ReadCheckpointAsync(
                new ObservationProcessorId("canonical-mapping"),
                secondStream))?.Position);

        var restarted = PipelineSet(
            rawStore,
            mappedStore,
            projectionStore,
            firstStream,
            secondStream);

        Assert.False(await restarted.RunCycleAsync());
        Assert.Single(mappedStore.ReadObservations(firstStream));
        Assert.Single(mappedStore.ReadObservations(secondStream));
    }

    private static DurableObservationProcessingPipelineSet PipelineSet(
        InMemoryObservationIngestionStore rawStore,
        InMemoryMappedMachineObservationSink mappedStore,
        InMemoryMachineStateActivityProjectionStore projectionStore,
        ObservationStreamId firstStream,
        ObservationStreamId secondStream)
    {
        var options = Options();
        DurableObservationProcessingPipeline[] pipelines =
        [
            Pipeline(
                rawStore,
                mappedStore,
                mappedStore,
                projectionStore,
                firstStream,
                "DI1",
                CanonicalSignalKeys.Running),
            Pipeline(
                rawStore,
                mappedStore,
                mappedStore,
                projectionStore,
                secondStream,
                "X1",
                CanonicalSignalKeys.PowerOn),
        ];

        return new DurableObservationProcessingPipelineSet(
            pipelines,
            options.PollingInterval);
    }

    private static DurableObservationProcessingPipeline Pipeline(
        InMemoryObservationIngestionStore rawStore,
        InMemoryMappedMachineObservationSink mappedStore,
        InMemoryMachineStateActivityProjectionStore projectionStore,
        ObservationStreamId streamId) =>
        Pipeline(
            rawStore,
            mappedStore,
            mappedStore,
            projectionStore,
            streamId,
            "DI1",
            CanonicalSignalKeys.Running);

    private static DurableObservationProcessingPipeline Pipeline(
        InMemoryObservationIngestionStore rawStore,
        IMappedMachineObservationSink mappedSink,
        IDurableMappedObservationReader mappedReader,
        IMachineStateActivityProjectionStore projectionStore,
        ObservationStreamId streamId,
        string address,
        string signalKey)
    {
        var options = Options();
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
                        Address = address,
                        SignalKey = signalKey,
                        Type = SignalType.Digital,
                    },
                ],
            },
            mappedSink);
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
                mappedReader,
                projectionStore,
                stateActivity,
                streamId,
                options),
            options.PollingInterval);
    }

    private static ObservationProcessingRuntimeOptions Options() =>
        new(1, TimeSpan.FromMilliseconds(1));

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

    private sealed class FailOnceMappedStore :
        IMappedMachineObservationSink,
        IDurableMappedObservationReader
    {
        private bool _shouldFail = true;

        public InMemoryMappedMachineObservationSink Inner { get; } = new();

        public ValueTask WriteAsync(
            IReadOnlyList<DurableMappedMachineObservation> observations,
            CancellationToken cancellationToken = default)
        {
            if (_shouldFail)
            {
                _shouldFail = false;
                throw new InvalidOperationException("Simulated mapped-store failure.");
            }

            return Inner.WriteAsync(observations, cancellationToken);
        }

        public ValueTask<MappedObservationReadBatch> ReadAsync(
            MappedObservationReadRequest request,
            CancellationToken cancellationToken = default) =>
            Inner.ReadAsync(request, cancellationToken);
    }

    private sealed class FailOnceProjectionStore :
        IMachineStateActivityProjectionStore
    {
        private bool _shouldFail = true;

        public InMemoryMachineStateActivityProjectionStore Inner { get; } =
            new();

        public ValueTask<MachineStateActivityProjection?> ReadAsync(
            ObservationProcessorId processorId,
            ObservationStreamId streamId,
            CancellationToken cancellationToken = default) =>
            Inner.ReadAsync(processorId, streamId, cancellationToken);

        public ValueTask CommitAsync(
            MachineStateActivityProjectionCommit commit,
            CancellationToken cancellationToken = default)
        {
            if (_shouldFail)
            {
                _shouldFail = false;
                throw new InvalidOperationException("Simulated projection-store failure.");
            }

            return Inner.CommitAsync(commit, cancellationToken);
        }
    }
}
