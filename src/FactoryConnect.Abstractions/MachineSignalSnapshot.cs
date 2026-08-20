namespace FactoryConnect.Abstractions;

public sealed record MachineSignalSnapshot(
    MachineId MachineId,
    IReadOnlyList<MachineSignalValue> Signals,
    DateTimeOffset Timestamp)
{
    public static MachineSignalSnapshot Empty(MachineId machineId) =>
        new(machineId, [], DateTimeOffset.UtcNow);
}
