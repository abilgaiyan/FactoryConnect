namespace FactoryConnect.Abstractions;

public sealed record MachineSignalDefinition
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public SignalType Type { get; init; } = SignalType.Digital;
    public SignalCategory Category { get; init; } = SignalCategory.Custom;
    public string? Unit { get; init; }
    public bool IsRequired { get; init; }
}
