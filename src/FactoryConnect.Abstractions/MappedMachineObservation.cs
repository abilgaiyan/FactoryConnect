namespace FactoryConnect.Abstractions;

public sealed record MappedMachineObservation
{
    public required MachineId MachineId { get; init; }
    public required string SignalKey { get; init; }
    public required SignalType Type { get; init; }
    public required object? Value { get; init; }
    public required string Source { get; init; }
    public required string Address { get; init; }
    public ObservationQuality Quality { get; init; } = ObservationQuality.Good;
    public DateTimeOffset Timestamp { get; init; }
}
