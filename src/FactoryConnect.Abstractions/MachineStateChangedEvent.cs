namespace FactoryConnect.Abstractions;

public sealed record MachineStateChangedEvent(
    MachineId MachineId,
    MachineState PreviousState,
    MachineState CurrentState,
    DateTimeOffset Timestamp);
