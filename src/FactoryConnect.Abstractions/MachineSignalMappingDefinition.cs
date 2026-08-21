namespace FactoryConnect.Abstractions;

public sealed record MachineSignalMappingDefinition
{
    public required string Source { get; init; }
    public required string Address { get; init; }
    public required string SignalKey { get; init; }
    public required SignalType Type { get; init; }
    public bool Invert { get; init; }
}
