namespace FactoryConnect.Abstractions;

public sealed record MachineObservation
{
    public required MachineId MachineId { get; init; }
    public required string Source { get; init; }
    public required string Address { get; init; }
    public required SignalType Type { get; init; }
    public required object? Value { get; init; }
    public ObservationQuality Quality { get; init; } = ObservationQuality.Good;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
