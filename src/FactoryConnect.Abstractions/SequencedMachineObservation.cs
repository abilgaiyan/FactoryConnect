namespace FactoryConnect.Abstractions;

public sealed record SequencedMachineObservation
{
    public SequencedMachineObservation(
        ulong sequence,
        MachineObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        Sequence = sequence;
        Observation = observation;
    }

    public ulong Sequence { get; }

    public MachineObservation Observation { get; }
}
