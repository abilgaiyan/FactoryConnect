using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;

namespace FactoryConnect.Core.Tests;

public sealed class MachineSignalMappingProcessorTests
{
    [Fact]
    public async Task ProcessAsyncMapsConfiguredObservationAndPublishesCoverage()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var sink = new RecordingSink();
        var authorityStore = new RecordingAuthorityStore();
        var processorId = new ObservationProcessorId("canonical-signals");
        var processor = new MachineSignalMappingProcessor(
            processorId,
            Configuration(machineId),
            sink,
            authorityStore);
        var durable = Durable(streamId, 7, 42, 101, true);

        await processor.ProcessAsync([durable]);

        var result = Assert.Single(sink.Observations);
        Assert.Equal(processorId, processor.ProcessorId);
        Assert.Equal(durable.Position, result.Position);
        Assert.Equal(streamId, result.StreamId);
        Assert.Equal(42UL, result.InstanceId);
        Assert.Equal(101UL, result.Sequence);
        Assert.Equal(CanonicalSignalKeys.Running, result.Observation.SignalKey);
        Assert.Equal(true, result.Observation.Value);

        var authority = Assert.IsType<MappingCoverageAuthority>(authorityStore.Current);
        Assert.Equal(new ObservationPosition(7), authority.RawConsumedThrough);
        Assert.Equal(
            new ObservationPosition(7),
            authority.MappedEvaluationInputHighWater);
        Assert.Equal(new MappingAuthorityRevision(0), authority.MappingRevision);
    }

    [Fact]
    public async Task ProcessAsyncUnmappedOnlyBatchAdvancesRawCoverageWithoutWriting()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var sink = new RecordingSink();
        var authorityStore = new RecordingAuthorityStore();
        var processor = Processor(machineId, sink, authorityStore);

        await processor.ProcessAsync(
            [
                Durable(streamId, 1, 1, 1, true, "DI2"),
                Durable(streamId, 4, 1, 2, true, "DI3"),
            ]);

        Assert.Equal(0, sink.WriteCount);
        Assert.Empty(sink.Observations);
        var authority = Assert.IsType<MappingCoverageAuthority>(authorityStore.Current);
        Assert.Equal(new ObservationPosition(4), authority.RawConsumedThrough);
        Assert.Null(authority.MappedEvaluationInputHighWater);
    }

    [Fact]
    public async Task ProcessAsyncUnmappedProgressPreservesPriorMappedHighWater()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var processorId = new ObservationProcessorId("canonical-signals");
        var prior = new MappingCoverageAuthority(
            processorId,
            streamId,
            new ObservationPosition(5),
            new ObservationPosition(3),
            new MappingAuthorityRevision(4));
        var authorityStore = new RecordingAuthorityStore(prior);
        var sink = new RecordingSink();
        var processor = new MachineSignalMappingProcessor(
            processorId,
            Configuration(machineId),
            sink,
            authorityStore);

        await processor.ProcessAsync(
            [
                Durable(streamId, 6, 1, 6, true, "DI2"),
                Durable(streamId, 9, 1, 9, true, "DI3"),
            ]);

        Assert.Empty(sink.Observations);
        var authority = Assert.IsType<MappingCoverageAuthority>(authorityStore.Current);
        Assert.Equal(new ObservationPosition(9), authority.RawConsumedThrough);
        Assert.Equal(
            new ObservationPosition(3),
            authority.MappedEvaluationInputHighWater);
        Assert.Equal(new MappingAuthorityRevision(5), authority.MappingRevision);
    }

    [Fact]
    public async Task ProcessAsyncUsesGreatestMappedPositionAsEvaluationHighWater()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var sink = new RecordingSink();
        var authorityStore = new RecordingAuthorityStore();
        var processor = Processor(machineId, sink, authorityStore);

        await processor.ProcessAsync(
            [
                Durable(streamId, 2, 1, 2, true),
                Durable(streamId, 9, 1, 9, true, "DI2"),
            ]);

        Assert.Single(sink.Observations);
        var authority = Assert.IsType<MappingCoverageAuthority>(authorityStore.Current);
        Assert.Equal(new ObservationPosition(9), authority.RawConsumedThrough);
        Assert.Equal(
            new ObservationPosition(2),
            authority.MappedEvaluationInputHighWater);
    }

    [Fact]
    public async Task ProcessAsyncAppliesConfiguredDigitalInversion()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var sink = new RecordingSink();
        var authorityStore = new RecordingAuthorityStore();
        var processor = new MachineSignalMappingProcessor(
            new ObservationProcessorId("canonical-signals"),
            Configuration(machineId, invert: true),
            sink,
            authorityStore);

        await processor.ProcessAsync(
            [Durable(streamId, 1, 1, 1, false)]);

        Assert.Equal(true, Assert.Single(sink.Observations).Observation.Value);
        Assert.NotNull(authorityStore.Current);
    }

    [Fact]
    public async Task ProcessAsyncDoesNotPublishAnythingWhenMappingFails()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var sink = new RecordingSink();
        var authorityStore = new RecordingAuthorityStore();
        var mappings = new[]
        {
            Mapping(CanonicalSignalKeys.Running),
            Mapping(CanonicalSignalKeys.PowerOn),
        };
        var processor = new MachineSignalMappingProcessor(
            new ObservationProcessorId("canonical-signals"),
            new MachineSignalMappingConfiguration
            {
                MachineId = machineId,
                Mappings = mappings,
            },
            sink,
            authorityStore);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync(
                [Durable(streamId, 1, 1, 1, true)]).AsTask());

        Assert.Equal(0, sink.WriteCount);
        Assert.Equal(0, authorityStore.CommitCount);
        Assert.Null(authorityStore.Current);
    }

    [Fact]
    public async Task SinkFailurePreventsAuthorityPublication()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var sink = new RecordingSink { ThrowOnWrite = true };
        var authorityStore = new RecordingAuthorityStore();
        var processor = Processor(machineId, sink, authorityStore);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync(
                [Durable(streamId, 1, 1, 1, true)]).AsTask());

        Assert.Equal(1, sink.WriteCount);
        Assert.Equal(0, authorityStore.CommitCount);
        Assert.Null(authorityStore.Current);
    }

    [Fact]
    public async Task AuthorityFailureOccursAfterMappedOutputPublication()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var sink = new RecordingSink();
        var authorityStore = new RecordingAuthorityStore
        {
            ThrowOnCommit = true,
        };
        var processor = Processor(machineId, sink, authorityStore);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync(
                [Durable(streamId, 1, 1, 1, true)]).AsTask());

        Assert.Single(sink.Observations);
        Assert.Equal(1, authorityStore.CommitCount);
        Assert.Null(authorityStore.Current);
    }

    [Fact]
    public async Task EmptyBatchPublishesNeitherOutputNorAuthority()
    {
        var machineId = MachineId.New();
        var sink = new RecordingSink();
        var authorityStore = new RecordingAuthorityStore();
        var processor = Processor(machineId, sink, authorityStore);

        await processor.ProcessAsync([]);

        Assert.Equal(0, sink.WriteCount);
        Assert.Equal(0, authorityStore.ReadCount);
        Assert.Equal(0, authorityStore.CommitCount);
    }

    [Fact]
    public async Task MixedStreamBatchIsRejectedBeforePublication()
    {
        var machineId = MachineId.New();
        var firstStream = new ObservationStreamId(machineId, "modbus:line-1");
        var secondStream = new ObservationStreamId(machineId, "modbus:line-2");
        var sink = new RecordingSink();
        var authorityStore = new RecordingAuthorityStore();
        var processor = Processor(machineId, sink, authorityStore);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync(
                [
                    Durable(firstStream, 1, 1, 1, true),
                    Durable(secondStream, 2, 1, 2, true),
                ]).AsTask());

        Assert.Equal(0, sink.WriteCount);
        Assert.Equal(0, authorityStore.ReadCount);
        Assert.Equal(0, authorityStore.CommitCount);
    }

    [Fact]
    public async Task ReplayPublishesSameCoverageWithoutChangingAuthorityRevision()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var sink = new RecordingSink();
        var authorityStore = new RecordingAuthorityStore();
        var processor = Processor(machineId, sink, authorityStore);
        var batch = new[]
        {
            Durable(streamId, 2, 1, 2, true),
            Durable(streamId, 5, 1, 5, true, "DI2"),
        };

        await processor.ProcessAsync(batch);
        var first = Assert.IsType<MappingCoverageAuthority>(authorityStore.Current);

        await processor.ProcessAsync(batch);
        var replay = Assert.IsType<MappingCoverageAuthority>(authorityStore.Current);

        Assert.Equal(first, replay);
        Assert.Equal(2, sink.WriteCount);
        Assert.Equal(2, authorityStore.CommitCount);
    }

    [Fact]
    public async Task ConstructorSnapshotsSignalMappings()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var mappings = new List<MachineSignalMappingDefinition>
        {
            Mapping(CanonicalSignalKeys.Running),
        };
        var sink = new RecordingSink();
        var authorityStore = new RecordingAuthorityStore();
        var processor = new MachineSignalMappingProcessor(
            new ObservationProcessorId("canonical-signals"),
            new MachineSignalMappingConfiguration
            {
                MachineId = machineId,
                Mappings = mappings,
            },
            sink,
            authorityStore);
        mappings.Clear();

        await processor.ProcessAsync(
            [Durable(streamId, 1, 1, 1, true)]);

        Assert.Single(sink.Observations);
        Assert.NotNull(authorityStore.Current);
    }

    private static MachineSignalMappingProcessor Processor(
        MachineId machineId,
        IMappedMachineObservationSink sink,
        IMappingCoverageAuthorityStore authorityStore) =>
        new(
            new ObservationProcessorId("canonical-signals"),
            Configuration(machineId),
            sink,
            authorityStore);

    private static MachineSignalMappingConfiguration Configuration(
        MachineId machineId,
        bool invert = false) =>
        new()
        {
            MachineId = machineId,
            Mappings = [Mapping(CanonicalSignalKeys.Running, invert)],
        };

    private static MachineSignalMappingDefinition Mapping(
        string signalKey,
        bool invert = false) =>
        new()
        {
            Source = "modbus",
            Address = "DI1",
            SignalKey = signalKey,
            Type = SignalType.Digital,
            Invert = invert,
        };

    private static DurableMachineObservation Durable(
        ObservationStreamId streamId,
        ulong position,
        ulong instanceId,
        ulong sequence,
        bool value,
        string address = "DI1") =>
        new(
            new ObservationPosition(position),
            streamId,
            instanceId,
            sequence,
            new MachineObservation
            {
                MachineId = streamId.MachineId,
                Source = "modbus",
                Address = address,
                Type = SignalType.Digital,
                Value = value,
                Timestamp = DateTimeOffset.UnixEpoch,
            });

    private sealed class RecordingSink : IMappedMachineObservationSink
    {
        public int WriteCount { get; private set; }

        public bool ThrowOnWrite { get; init; }

        public List<DurableMappedMachineObservation> Observations { get; } = [];

        public ValueTask WriteAsync(
            IReadOnlyList<DurableMappedMachineObservation> observations,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;

            if (ThrowOnWrite)
            {
                throw new InvalidOperationException("Mapped sink failure.");
            }

            Observations.AddRange(observations);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAuthorityStore : IMappingCoverageAuthorityStore
    {
        public RecordingAuthorityStore(MappingCoverageAuthority? current = null)
        {
            Current = current;
        }

        public MappingCoverageAuthority? Current { get; private set; }

        public int ReadCount { get; private set; }

        public int CommitCount { get; private set; }

        public bool ThrowOnCommit { get; init; }

        public ValueTask<MappingCoverageAuthority?> ReadAsync(
            ObservationProcessorId mappingProcessorId,
            ObservationStreamId observationStreamId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(Current);
        }

        public ValueTask CommitAsync(
            MappingCoverageCommit commit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitCount++;

            if (ThrowOnCommit)
            {
                throw new InvalidOperationException("Mapping authority failure.");
            }

            if (Current is not null &&
                Current.RawConsumedThrough == commit.RawConsumedThrough &&
                Current.MappedEvaluationInputHighWater ==
                    commit.MappedEvaluationInputHighWater)
            {
                return ValueTask.CompletedTask;
            }

            var nextRevision = Current is null
                ? 0UL
                : Current.MappingRevision.Value + 1;
            Current = new MappingCoverageAuthority(
                commit.MappingProcessorId,
                commit.ObservationStreamId,
                commit.RawConsumedThrough,
                commit.MappedEvaluationInputHighWater,
                new MappingAuthorityRevision(nextRevision));
            return ValueTask.CompletedTask;
        }
    }
}
