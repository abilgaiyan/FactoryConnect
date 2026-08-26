using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Machines;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class FirstDomainObservationProcessorIntegrationTests
{
    [Fact]
    public async Task RuntimeMapsDurableObservationsAndResumesWithoutDuplicates()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var store = new InMemoryObservationIngestionStore();
        var sink = new InMemoryMappedMachineObservationSink();
        var processor = Processor(machineId, sink);

        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                new ObservationCheckpoint(streamId, 9, 3),
                [
                    new SequencedMachineObservation(
                        1,
                        Observation(machineId, "DI1", true)),
                    new SequencedMachineObservation(
                        2,
                        Observation(machineId, "DI2", false)),
                ]));

        var runtime = Runtime(store, processor, streamId);
        await runtime.RunCycleAsync();

        var first = Assert.Single(sink.ReadObservations(streamId));
        Assert.Equal(new ObservationPosition(1), first.Position);
        Assert.Equal(1UL, first.Sequence);
        Assert.Equal(CanonicalSignalKeys.Running, first.Observation.SignalKey);

        var restarted = Runtime(store, processor, streamId);
        await restarted.RunCycleAsync();

        Assert.Single(sink.ReadObservations(streamId));
    }

    [Fact]
    public async Task SinkTreatsEquivalentReplayAsIdempotent()
    {
        var streamId = Stream();
        var sink = new InMemoryMappedMachineObservationSink();
        var observation = Mapped(streamId, 1, CanonicalSignalKeys.Running);

        await sink.WriteAsync([observation]);
        await sink.WriteAsync([observation]);

        Assert.Equal([observation], sink.ReadObservations(streamId));
    }

    [Fact]
    public async Task SinkRejectsConflictingPositionAtomically()
    {
        var streamId = Stream();
        var sink = new InMemoryMappedMachineObservationSink();
        var original = Mapped(streamId, 1, CanonicalSignalKeys.Running);
        await sink.WriteAsync([original]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.WriteAsync(
                [
                    Mapped(streamId, 2, CanonicalSignalKeys.PowerOn),
                    Mapped(streamId, 1, CanonicalSignalKeys.Fault),
                ]).AsTask());

        Assert.Equal([original], sink.ReadObservations(streamId));
    }

    [Fact]
    public async Task SinkOrdersReadsByDurablePositionAndIsolatesStreams()
    {
        var firstStream = Stream();
        var secondStream = Stream();
        var sink = new InMemoryMappedMachineObservationSink();

        await sink.WriteAsync(
            [
                Mapped(firstStream, 2, CanonicalSignalKeys.PowerOn),
                Mapped(secondStream, 1, CanonicalSignalKeys.Fault),
                Mapped(firstStream, 1, CanonicalSignalKeys.Running),
            ]);

        Assert.Equal(
            [1UL, 2UL],
            sink.ReadObservations(firstStream)
                .Select(item => item.Position.Value));
        Assert.Single(sink.ReadObservations(secondStream));
    }

    private static MachineSignalMappingProcessor Processor(
        MachineId machineId,
        IMappedMachineObservationSink sink) =>
        new(
            new ObservationProcessorId("canonical-signals"),
            new MachineSignalMappingConfiguration
            {
                MachineId = machineId,
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
            sink);

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
                10,
                TimeSpan.FromMilliseconds(1)));

    private static ObservationStreamId Stream() =>
        new(MachineId.New(), "modbus:line-1");

    private static MachineObservation Observation(
        MachineId machineId,
        string address,
        bool value) =>
        new()
        {
            MachineId = machineId,
            Source = "modbus",
            Address = address,
            Type = SignalType.Digital,
            Value = value,
            Timestamp = DateTimeOffset.UnixEpoch,
        };

    private static DurableMappedMachineObservation Mapped(
        ObservationStreamId streamId,
        ulong position,
        string signalKey) =>
        new(
            new ObservationPosition(position),
            streamId,
            1,
            position,
            new MappedMachineObservation
            {
                MachineId = streamId.MachineId,
                SignalKey = signalKey,
                Type = SignalType.Digital,
                Value = true,
                Source = "modbus",
                Address = "DI1",
                Timestamp = DateTimeOffset.UnixEpoch,
            });
}
