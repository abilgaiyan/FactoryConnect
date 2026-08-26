using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;

namespace FactoryConnect.Core.Tests;

public sealed class MachineSignalMappingProcessorTests
{
    [Fact]
    public async Task ProcessAsyncMapsConfiguredObservationAndPreservesDurableIdentity()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var sink = new RecordingSink();
        var processorId = new ObservationProcessorId("canonical-signals");
        var processor = new MachineSignalMappingProcessor(
            processorId,
            Configuration(machineId),
            sink);
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
    }

    [Fact]
    public async Task ProcessAsyncSkipsUnconfiguredObservationWithoutWriting()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var sink = new RecordingSink();
        var processor = new MachineSignalMappingProcessor(
            new ObservationProcessorId("canonical-signals"),
            Configuration(machineId),
            sink);

        await processor.ProcessAsync(
            [Durable(streamId, 1, 1, 1, true, "DI2")]);

        Assert.Equal(0, sink.WriteCount);
        Assert.Empty(sink.Observations);
    }

    [Fact]
    public async Task ProcessAsyncAppliesConfiguredDigitalInversion()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var sink = new RecordingSink();
        var processor = new MachineSignalMappingProcessor(
            new ObservationProcessorId("canonical-signals"),
            Configuration(machineId, invert: true),
            sink);

        await processor.ProcessAsync(
            [Durable(streamId, 1, 1, 1, false)]);

        Assert.Equal(true, Assert.Single(sink.Observations).Observation.Value);
    }

    [Fact]
    public async Task ProcessAsyncDoesNotWritePartialBatchWhenMappingFails()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var sink = new RecordingSink();
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
            sink);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync(
                [Durable(streamId, 1, 1, 1, true)]).AsTask());

        Assert.Equal(0, sink.WriteCount);
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
        var processor = new MachineSignalMappingProcessor(
            new ObservationProcessorId("canonical-signals"),
            new MachineSignalMappingConfiguration
            {
                MachineId = machineId,
                Mappings = mappings,
            },
            sink);
        mappings.Clear();

        await processor.ProcessAsync(
            [Durable(streamId, 1, 1, 1, true)]);

        Assert.Single(sink.Observations);
    }

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

        public List<DurableMappedMachineObservation> Observations { get; } = [];

        public ValueTask WriteAsync(
            IReadOnlyList<DurableMappedMachineObservation> observations,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;
            Observations.AddRange(observations);

            return ValueTask.CompletedTask;
        }
    }
}
