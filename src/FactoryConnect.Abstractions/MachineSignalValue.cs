namespace FactoryConnect.Abstractions;

public sealed record MachineSignalValue
{
    public required string Key { get; init; }
    public required SignalType Type { get; init; }
    public required object? Value { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
