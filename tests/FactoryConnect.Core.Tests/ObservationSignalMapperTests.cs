using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;

namespace FactoryConnect.Core.Tests;

public sealed class ObservationSignalMapperTests
{
    [Fact]
    public void MapCreatesCanonicalSignalFromMatchingObservation()
    {
        var machineId = MachineId.New();
        var timestamp = DateTimeOffset.UtcNow;
        var observation = new MachineObservation
        {
            MachineId = machineId,
            Source = "modbus",
            Address = "DI0",
            Type = SignalType.Digital,
            Value = true,
            Quality = ObservationQuality.Good,
            Timestamp = timestamp,
        };
        var mapping = new MachineSignalMapping
        {
            SignalKey = CanonicalSignalKeys.Running,
            Source = "DI0",
        };
        var definition = new MachineSignalDefinition
        {
            Key = CanonicalSignalKeys.Running,
            Name = "Running",
            Type = SignalType.Digital,
            Category = SignalCategory.State,
        };

        var signal = ObservationSignalMapper.Map(
            observation,
            mapping,
            definition);

        Assert.NotNull(signal);
        Assert.Equal(CanonicalSignalKeys.Running, signal.Key);
        Assert.Equal(true, signal.Value);
        Assert.Equal("modbus", signal.Source);
        Assert.Equal(ObservationQuality.Good, signal.Quality);
        Assert.Equal(timestamp, signal.Timestamp);
    }

    [Fact]
    public void MapReturnsNullWhenObservationAddressDoesNotMatch()
    {
        var observation = CreateObservation("DI1", SignalType.Digital, true);
        var mapping = new MachineSignalMapping
        {
            SignalKey = CanonicalSignalKeys.Running,
            Source = "DI0",
        };
        var definition = CreateDefinition(
            CanonicalSignalKeys.Running,
            SignalType.Digital);

        var signal = ObservationSignalMapper.Map(
            observation,
            mapping,
            definition);

        Assert.Null(signal);
    }

    [Fact]
    public void MapReturnsNullWhenObservationTypeDoesNotMatchDefinition()
    {
        var observation = CreateObservation("R100", SignalType.Numeric, 1200.0);
        var mapping = new MachineSignalMapping
        {
            SignalKey = CanonicalSignalKeys.SpindleSpeed,
            Source = "R100",
        };
        var definition = CreateDefinition(
            CanonicalSignalKeys.SpindleSpeed,
            SignalType.WholeNumber);

        var signal = ObservationSignalMapper.Map(
            observation,
            mapping,
            definition);

        Assert.Null(signal);
    }

    private static MachineObservation CreateObservation(
        string address,
        SignalType type,
        object value) =>
        new()
        {
            MachineId = MachineId.New(),
            Source = "test",
            Address = address,
            Type = type,
            Value = value,
        };

    private static MachineSignalDefinition CreateDefinition(
        string key,
        SignalType type) =>
        new()
        {
            Key = key,
            Name = key,
            Type = type,
        };
}
