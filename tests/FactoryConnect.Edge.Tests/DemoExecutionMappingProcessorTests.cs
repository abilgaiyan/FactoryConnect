using FactoryConnect.Abstractions;
using FactoryConnect.Edge;

namespace FactoryConnect.Edge.Tests;

public sealed class DemoExecutionMappingProcessorTests
{
    [Theory]
    [InlineData("ACTIVE", true)]
    [InlineData("READY", false)]
    public async Task ProcessAsync_NormalizesFixtureExecutionToDigitalRunning(
        string value,
        bool expected)
    {
        var inner = new CapturingProcessor();
        var sut = new DemoExecutionMappingProcessor(inner);
        var source = CreateObservation(value);

        await sut.ProcessAsync([source]);

        var actual = Assert.Single(inner.Observations);
        Assert.Equal(source.Position, actual.Position);
        Assert.Equal(source.StreamId, actual.StreamId);
        Assert.Equal(source.InstanceId, actual.InstanceId);
        Assert.Equal(source.Sequence, actual.Sequence);
        Assert.Equal(SignalType.Digital, actual.Observation.Type);
        Assert.Equal(expected, Assert.IsType<bool>(actual.Observation.Value));
        Assert.Equal(source.Observation.Source, actual.Observation.Source);
        Assert.Equal(source.Observation.Address, actual.Observation.Address);
        Assert.Equal(source.Observation.Timestamp, actual.Observation.Timestamp);
    }

    [Fact]
    public async Task ProcessAsync_BadQualityExecutionDoesNotManufactureBooleanState()
    {
        var inner = new CapturingProcessor();
        var sut = new DemoExecutionMappingProcessor(inner);
        var source = CreateObservation("ACTIVE", ObservationQuality.Bad);

        await sut.ProcessAsync([source]);

        var actual = Assert.Single(inner.Observations);
        Assert.Equal(SignalType.Digital, actual.Observation.Type);
        Assert.Null(actual.Observation.Value);
        Assert.Equal(ObservationQuality.Bad, actual.Observation.Quality);
    }

    [Fact]
    public async Task ProcessAsync_UnexpectedGoodExecutionFailsClosed()
    {
        var sut = new DemoExecutionMappingProcessor(new CapturingProcessor());
        var source = CreateObservation("STOPPED");

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await sut.ProcessAsync([source]).AsTask());
    }

    [Fact]
    public async Task ProcessAsync_NonFixtureObservationPassesThroughUnchanged()
    {
        var inner = new CapturingProcessor();
        var sut = new DemoExecutionMappingProcessor(inner);
        var machineId = new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var streamId = new ObservationStreamId(machineId, "mtconnect:CNC-01");
        var source = new DurableMachineObservation(
            new ObservationPosition(17),
            streamId,
            23,
            41,
            new MachineObservation
            {
                MachineId = machineId,
                Source = "mtconnect",
                Address = "part_count",
                Type = SignalType.Numeric,
                Value = 12m,
                Quality = ObservationQuality.Good,
                Timestamp = new DateTimeOffset(2026, 9, 25, 8, 30, 0, TimeSpan.Zero),
            });

        await sut.ProcessAsync([source]);

        Assert.Same(source, Assert.Single(inner.Observations));
    }

    [Fact]
    public void ProcessorId_IsOwnedByInnerProcessor()
    {
        var inner = new CapturingProcessor();
        var sut = new DemoExecutionMappingProcessor(inner);

        Assert.Equal(inner.ProcessorId, sut.ProcessorId);
    }

    private static DurableMachineObservation CreateObservation(
        string value,
        ObservationQuality quality = ObservationQuality.Good)
    {
        var machineId = new MachineId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var streamId = new ObservationStreamId(machineId, "mtconnect:CNC-01");
        return new DurableMachineObservation(
            new ObservationPosition(17),
            streamId,
            23,
            41,
            new MachineObservation
            {
                MachineId = machineId,
                Source = "mtconnect",
                Address = "exec",
                Type = SignalType.Enumeration,
                Value = value,
                Quality = quality,
                Timestamp = new DateTimeOffset(2026, 9, 25, 8, 30, 0, TimeSpan.Zero),
            });
    }

    private sealed class CapturingProcessor : IObservationProcessor
    {
        public ObservationProcessorId ProcessorId { get; } = new("test-demo-execution");

        public IReadOnlyList<DurableMachineObservation> Observations { get; private set; } = [];

        public ValueTask ProcessAsync(
            IReadOnlyList<DurableMachineObservation> observations,
            CancellationToken cancellationToken = default)
        {
            Observations = observations;
            return ValueTask.CompletedTask;
        }
    }
}
