namespace FactoryConnect.Abstractions;

public readonly record struct MachineId(Guid Value)
{
    public static MachineId New() => new(Guid.NewGuid());

    public bool IsEmpty => Value == Guid.Empty;

    public override string ToString() => Value.ToString("D");
}
