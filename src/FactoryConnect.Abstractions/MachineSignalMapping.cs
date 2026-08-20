namespace FactoryConnect.Abstractions;

public sealed record MachineSignalMapping
{
    public required string SignalKey { get; init; }
    public required string Source { get; init; }
}
