using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;

namespace FactoryConnect.Core.Tests;

public sealed class MachineSignalMapperTests
{
    [Fact]
    public void TryMapMapsConfiguredAddressToCanonicalSignal()
    {
        var machineId = MachineId.New();
        var timestamp = new DateTimeOffset(2026, 8, 21, 6, 0, 0, TimeSpan.Zero);
        var observation = Observation(
            machineId,
            "tcp-v",
            "TR.1.DIN.4",
            SignalType.Digital,
            true,
            timestamp);
        var configuration = Configuration(
            machineId,
            Mapping("tcp-v", "TR.1.DIN.4", CanonicalSignalKeys.PowerOn));

        var mapped = MachineSignalMapper.TryMap(
            observation,
            configuration,
            out var result);

        Assert.True(mapped);
        Assert.NotNull(result);
        Assert.Equal(machineId, result.MachineId);
        Assert.Equal(CanonicalSignalKeys.PowerOn, result.SignalKey);
        Assert.Equal(true, result.Value);
        Assert.Equal(observation.Source, result.Source);
        Assert.Equal(observation.Address, result.Address);
        Assert.Equal(timestamp, result.Timestamp);
    }

    [Fact]
    public void TryMapAllowsSameAddressToHaveDifferentMeaningForDifferentMachines()
    {
        var firstMachine = MachineId.New();
        var secondMachine = MachineId.New();

        var firstMapped = MachineSignalMapper.TryMap(
            Observation(firstMachine, "modbus", "DI1", SignalType.Digital, true),
            Configuration(
                firstMachine,
                Mapping("modbus", "DI1", CanonicalSignalKeys.Running)),
            out var firstResult);

        var secondMapped = MachineSignalMapper.TryMap(
            Observation(secondMachine, "modbus", "DI1", SignalType.Digital, true),
            Configuration(
                secondMachine,
                Mapping("modbus", "DI1", CanonicalSignalKeys.PowerOn)),
            out var secondResult);

        Assert.True(firstMapped);
        Assert.True(secondMapped);
        Assert.NotNull(firstResult);
        Assert.NotNull(secondResult);
        Assert.Equal(CanonicalSignalKeys.Running, firstResult.SignalKey);
        Assert.Equal(CanonicalSignalKeys.PowerOn, secondResult.SignalKey);
    }

    [Fact]
    public void TryMapSupportsActiveLowDigitalSignalsThroughInversion()
    {
        var machineId = MachineId.New();
        var configuration = Configuration(
            machineId,
            Mapping(
                "modbus",
                "DI2",
                CanonicalSignalKeys.Fault,
                invert: true));

        var mapped = MachineSignalMapper.TryMap(
            Observation(machineId, "modbus", "DI2", SignalType.Digital, false),
            configuration,
            out var result);

        Assert.True(mapped);
        Assert.NotNull(result);
        Assert.Equal(true, result.Value);
    }

    [Fact]
    public void TryMapReturnsFalseWhenAddressIsNotConfigured()
    {
        var machineId = MachineId.New();
        var observation = Observation(
            machineId,
            "tcp-v",
            "TR.1.DIN.3",
            SignalType.Digital,
            true);
        var configuration = Configuration(
            machineId,
            Mapping("tcp-v", "TR.1.DIN.1", CanonicalSignalKeys.Running));

        var mapped = MachineSignalMapper.TryMap(
            observation,
            configuration,
            out var result);

        Assert.False(mapped);
        Assert.Null(result);
    }

    [Fact]
    public void TryMapRejectsObservationFromDifferentMachineScope()
    {
        var configuredMachine = MachineId.New();
        var otherMachine = MachineId.New();
        var configuration = Configuration(
            configuredMachine,
            Mapping("modbus", "DI1", CanonicalSignalKeys.Running));

        Assert.Throws<ArgumentException>(
            () => MachineSignalMapper.TryMap(
                Observation(otherMachine, "modbus", "DI1", SignalType.Digital, true),
                configuration,
                out _));
    }

    [Fact]
    public void TryMapRejectsAmbiguousDuplicateMappings()
    {
        var machineId = MachineId.New();
        var configuration = Configuration(
            machineId,
            Mapping("modbus", "DI1", CanonicalSignalKeys.Running),
            Mapping("modbus", "DI1", CanonicalSignalKeys.PowerOn));

        Assert.Throws<InvalidOperationException>(
            () => MachineSignalMapper.TryMap(
                Observation(machineId, "modbus", "DI1", SignalType.Digital, true),
                configuration,
                out _));
    }

    private static MachineSignalMappingConfiguration Configuration(
        MachineId machineId,
        params MachineSignalMappingDefinition[] mappings) =>
        new()
        {
            MachineId = machineId,
            Mappings = mappings,
        };

    private static MachineSignalMappingDefinition Mapping(
        string source,
        string address,
        string signalKey,
        bool invert = false) =>
        new()
        {
            Source = source,
            Address = address,
            SignalKey = signalKey,
            Type = SignalType.Digital,
            Invert = invert,
        };

    private static MachineObservation Observation(
        MachineId machineId,
        string source,
        string address,
        SignalType type,
        object? value,
        DateTimeOffset? timestamp = null) =>
        new()
        {
            MachineId = machineId,
            Source = source,
            Address = address,
            Type = type,
            Value = value,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
        };
}
