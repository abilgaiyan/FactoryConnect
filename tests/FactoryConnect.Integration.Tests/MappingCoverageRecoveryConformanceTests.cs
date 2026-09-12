using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Machines;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class MappingCoverageRecoveryConformanceTests
{
    [Fact]
    public async Task AuthorityPublicationFailureLeavesCheckpointEligibleForReplayAndConverges()
    {
        var streamId = Stream();
        var rawStore = new InMemoryObservationIngestionStore();
        var mappedStore = new InMemoryMappedMachineObservationSink();
        var authorityStore = new FailOnceMappingCoverageAuthorityStore();
        var processorId = new ObservationProcessorId("canonical-mapping");
        var runtime = Runtime(
            rawStore,
            rawStore,
            mappedStore,
            authorityStore,
            processorId,
            streamId,
            batchSize: 1);

        await CommitRawAsync(
            rawStore,
            streamId,
            [Sequenced(streamId.MachineId, 1, "DI1")]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.RunCycleAsync());

        Assert.Single(mappedStore.ReadObservations(streamId));
        Assert.Null(await authorityStore.Inner.ReadAsync(processorId, streamId));
        Assert.Null(await rawStore.ReadCheckpointAsync(processorId, streamId));

        await runtime.RunCycleAsync();

        Assert.Single(mappedStore.ReadObservations(streamId));
        var authority = await RequiredAuthority(
            authorityStore.Inner,
            processorId,
            streamId);
        Assert.Equal(new ObservationPosition(1), authority.RawConsumedThrough);
        Assert.Equal(
            new ObservationPosition(1),
            authority.MappedEvaluationInputHighWater);
        Assert.Equal(new MappingAuthorityRevision(0), authority.MappingRevision);
        Assert.Equal(
            new ObservationPosition(1),
            (await rawStore.ReadCheckpointAsync(processorId, streamId))?.Position);
    }

    [Fact]
    public async Task ProcessingCheckpointFailureAfterAuthorityPublicationReplaysWithoutRevisionAdvance()
    {
        var streamId = Stream();
        var rawStore = new InMemoryObservationIngestionStore();
        var checkpointStore = new FailOnceProcessingCheckpointStore(rawStore);
        var mappedStore = new InMemoryMappedMachineObservationSink();
        var authorityStore = new InMemoryMappingCoverageAuthorityStore();
        var processorId = new ObservationProcessorId("canonical-mapping");
        var runtime = Runtime(
            rawStore,
            checkpointStore,
            mappedStore,
            authorityStore,
            processorId,
            streamId,
            batchSize: 1);

        await CommitRawAsync(
            rawStore,
            streamId,
            [Sequenced(streamId.MachineId, 1, "DI1")]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.RunCycleAsync());

        var afterAuthorityPublication = await RequiredAuthority(
            authorityStore,
            processorId,
            streamId);
        Assert.Equal(
            new MappingAuthorityRevision(0),
            afterAuthorityPublication.MappingRevision);
        Assert.Single(mappedStore.ReadObservations(streamId));
        Assert.Null(await rawStore.ReadCheckpointAsync(processorId, streamId));

        await runtime.RunCycleAsync();

        var afterReplay = await RequiredAuthority(
            authorityStore,
            processorId,
            streamId);
        Assert.Equal(afterAuthorityPublication, afterReplay);
        Assert.Single(mappedStore.ReadObservations(streamId));
        Assert.Equal(
            new ObservationPosition(1),
            (await rawStore.ReadCheckpointAsync(processorId, streamId))?.Position);
    }

    [Fact]
    public async Task UnmappedProgressKeepsMappedHighWaterNullUntilFirstMappedObservation()
    {
        var streamId = Stream();
        var rawStore = new InMemoryObservationIngestionStore();
        var mappedStore = new InMemoryMappedMachineObservationSink();
        var authorityStore = new InMemoryMappingCoverageAuthorityStore();
        var processorId = new ObservationProcessorId("canonical-mapping");
        var runtime = Runtime(
            rawStore,
            rawStore,
            mappedStore,
            authorityStore,
            processorId,
            streamId,
            batchSize: 1);

        await CommitRawAsync(
            rawStore,
            streamId,
            [
                Sequenced(streamId.MachineId, 1, "UNMAPPED"),
                Sequenced(streamId.MachineId, 2, "DI1"),
            ]);

        await runtime.RunCycleAsync();

        var afterUnmapped = await RequiredAuthority(
            authorityStore,
            processorId,
            streamId);
        Assert.Equal(new ObservationPosition(1), afterUnmapped.RawConsumedThrough);
        Assert.Null(afterUnmapped.MappedEvaluationInputHighWater);
        Assert.Equal(new MappingAuthorityRevision(0), afterUnmapped.MappingRevision);
        Assert.Empty(mappedStore.ReadObservations(streamId));

        await runtime.RunCycleAsync();

        var afterMapped = await RequiredAuthority(
            authorityStore,
            processorId,
            streamId);
        Assert.Equal(new ObservationPosition(2), afterMapped.RawConsumedThrough);
        Assert.Equal(
            new ObservationPosition(2),
            afterMapped.MappedEvaluationInputHighWater);
        Assert.Equal(new MappingAuthorityRevision(1), afterMapped.MappingRevision);
        Assert.Single(mappedStore.ReadObservations(streamId));
    }

    private static ObservationProcessingRuntime Runtime(
        IDurableObservationReader reader,
        IObservationProcessingCheckpointStore checkpointStore,
        IMappedMachineObservationSink mappedStore,
        IMappingCoverageAuthorityStore authorityStore,
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        int batchSize)
    {
        var processor = new MachineSignalMappingProcessor(
            processorId,
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
            mappedStore,
            authorityStore);

        return new ObservationProcessingRuntime(
            reader,
            checkpointStore,
            processor,
            streamId,
            new ObservationProcessingRuntimeOptions(
                batchSize,
                TimeSpan.FromMilliseconds(1)));
    }

    private static async Task CommitRawAsync(
        InMemoryObservationIngestionStore store,
        ObservationStreamId streamId,
        IReadOnlyList<SequencedMachineObservation> observations)
    {
        var lastSequence = observations[^1].Sequence;
        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                new ObservationCheckpoint(streamId, 1, lastSequence + 1),
                observations,
                DateTimeOffset.UnixEpoch));
    }

    private static SequencedMachineObservation Sequenced(
        MachineId machineId,
        ulong sequence,
        string address) =>
        new(
            sequence,
            new MachineObservation
            {
                MachineId = machineId,
                Source = "modbus",
                Address = address,
                Type = SignalType.Digital,
                Value = true,
                Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            });

    private static ObservationStreamId Stream() =>
        new(MachineId.New(), "modbus:line-1");

    private static async Task<MappingCoverageAuthority> RequiredAuthority(
        InMemoryMappingCoverageAuthorityStore store,
        ObservationProcessorId processorId,
        ObservationStreamId streamId)
    {
        var authority = await store.ReadAsync(processorId, streamId);
        Assert.NotNull(authority);
        return authority;
    }

    private sealed class FailOnceMappingCoverageAuthorityStore :
        IMappingCoverageAuthorityStore
    {
        private bool _shouldFail = true;

        public InMemoryMappingCoverageAuthorityStore Inner { get; } = new();

        public ValueTask<MappingCoverageAuthority?> ReadAsync(
            ObservationProcessorId mappingProcessorId,
            ObservationStreamId observationStreamId,
            CancellationToken cancellationToken = default) =>
            Inner.ReadAsync(
                mappingProcessorId,
                observationStreamId,
                cancellationToken);

        public ValueTask CommitAsync(
            MappingCoverageCommit commit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_shouldFail)
            {
                _shouldFail = false;
                throw new InvalidOperationException(
                    "Simulated mapping-authority publication failure.");
            }

            return Inner.CommitAsync(commit, cancellationToken);
        }
    }

    private sealed class FailOnceProcessingCheckpointStore(
        IObservationProcessingCheckpointStore inner)
        : IObservationProcessingCheckpointStore
    {
        private bool _shouldFail = true;

        public ValueTask<ObservationProcessingCheckpoint?> ReadCheckpointAsync(
            ObservationProcessorId processorId,
            ObservationStreamId streamId,
            CancellationToken cancellationToken = default) =>
            inner.ReadCheckpointAsync(processorId, streamId, cancellationToken);

        public ValueTask CommitAsync(
            ObservationProcessingCommit commit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
