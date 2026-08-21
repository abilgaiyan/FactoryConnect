namespace FactoryConnect.Abstractions;

public sealed record MachineCapabilityDefinition
{
    public required string Key { get; init; }
    public required string Name { get; init; }
}
