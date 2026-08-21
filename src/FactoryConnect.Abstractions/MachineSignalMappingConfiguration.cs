namespace FactoryConnect.Abstractions;

public sealed record MachineSignalMappingConfiguration
{
    public required MachineId MachineId { get; init; }
    public IReadOnlyCollection<MachineSignalMappingDefinition> Mappings { get; init; } = [];
}
