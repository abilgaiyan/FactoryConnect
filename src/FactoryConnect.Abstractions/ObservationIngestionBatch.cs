namespace FactoryConnect.Abstractions;

public sealed record ObservationIngestionBatch
{
    public ObservationIngestionBatch(
        ObservationCheckpoint checkpoint,
        IReadOnlyList<SequencedMachineObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(observations);

        Checkpoint = checkpoint;
        Observations = observations.ToArray();
    }

    public ObservationCheckpoint Checkpoint { get; }

    public IReadOnlyList<SequencedMachineObservation> Observations { get; }
}
