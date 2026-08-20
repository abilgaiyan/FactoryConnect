namespace FactoryConnect.Abstractions;

public sealed record MachineSignalDefinition
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public SignalType Type { get; init; } = SignalType.Digital;
    public bool IsRequired { get; init; }
}
