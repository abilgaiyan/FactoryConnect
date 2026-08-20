namespace FactoryConnect.Abstractions;

public sealed record FactoryDefinition
{
    public required string Name { get; init; }
    public IReadOnlyList<ProductionLineDefinition> Lines { get; init; } = [];
}

public sealed record ProductionLineDefinition
{
    public required ProductionLineId Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<MachineDefinition> Machines { get; init; } = [];
}
