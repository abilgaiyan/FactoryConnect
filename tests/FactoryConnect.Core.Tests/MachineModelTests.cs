using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

public sealed class MachineModelTests
{
    [Fact]
    public void MachineDefinitionCanDescribeCanonicalSignalsAndCapabilities()
    {
        var machine = new MachineDefinition
        {
            Id = MachineId.New(),
            Name = "Machine 01",
            LineId = new ProductionLineId("L1"),
            Signals =
            [
                new MachineSignalDefinition
                {
                    Key = CanonicalSignalKeys.Running,
                    Name = "Machine Running",
                    Type = SignalType.Digital,
                    Category = SignalCategory.State,
                    IsRequired = true
                },
                new MachineSignalDefinition
                {
                    Key = CanonicalSignalKeys.PartCount,
                    Name = "Part Count",
                    Type = SignalType.Counter,
                    Category = SignalCategory.Production
                }
            ],
            Capabilities =
            [
                new MachineCapabilityDefinition
                {
                    Key = CanonicalSignalKeys.Running,
                    Name = "Running State"
                },
                new MachineCapabilityDefinition
                {
                    Key = CanonicalSignalKeys.PartCount,
                    Name = "Part Count"
                }
            ]
        };

        Assert.False(machine.Id.IsEmpty);
        Assert.Equal("L1", machine.LineId.Value);
        Assert.Equal(2, machine.Signals.Count);
        Assert.Equal(CanonicalSignalKeys.Running, machine.Signals[0].Key);
        Assert.Equal(SignalCategory.State, machine.Signals[0].Category);
        Assert.Equal(2, machine.Capabilities.Count);
        Assert.Equal(CanonicalSignalKeys.PartCount, machine.Capabilities[1].Key);
    }

    [Fact]
    public void FactoryDefinitionCanRepresentPilotHierarchy()
    {
        var line1 = new ProductionLineDefinition
        {
            Id = new ProductionLineId("L1"),
            Name = "Line 1",
            Machines =
            [
                new MachineDefinition
                {
                    Id = MachineId.New(),
                    Name = "Machine 01",
                    LineId = new ProductionLineId("L1")
                }
            ]
        };

        var line2 = new ProductionLineDefinition
        {
            Id = new ProductionLineId("L2"),
            Name = "Line 2",
            Machines =
            [
                new MachineDefinition
                {
                    Id = MachineId.New(),
                    Name = "Machine 06",
                    LineId = new ProductionLineId("L2")
                }
            ]
        };

        var factory = new FactoryDefinition
        {
            Name = "Gajra Gears",
            Lines = [line1, line2]
        };

        Assert.Equal(2, factory.Lines.Count);
        Assert.Equal("Line 1", factory.Lines[0].Name);
        Assert.Equal("Line 2", factory.Lines[1].Name);
    }

    [Fact]
    public void SignalMappingUsesCanonicalSignalKeyAndOpaqueSource()
    {
        var mapping = new MachineSignalMapping
        {
            SignalKey = CanonicalSignalKeys.Running,
            Source = "DI0"
        };

        Assert.Equal(CanonicalSignalKeys.Running, mapping.SignalKey);
        Assert.Equal("DI0", mapping.Source);
    }
}
