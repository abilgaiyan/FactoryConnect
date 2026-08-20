namespace FactoryConnect.Abstractions;

public sealed record MachineDefinition
{
    public required MachineId Id { get; init; }
    public required string Name { get; init; }
    public required string LineId { get; init; }
    public IReadOnlyList<MachineSignalDefinition> Signals { get; init; } = [];
}
