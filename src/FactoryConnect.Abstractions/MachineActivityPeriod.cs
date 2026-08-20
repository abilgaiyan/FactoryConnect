namespace FactoryConnect.Abstractions;

public sealed record MachineActivityPeriod(
    MachineId MachineId,
    MachineState State,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt)
{
    public TimeSpan Duration => EndedAt - StartedAt;
}
