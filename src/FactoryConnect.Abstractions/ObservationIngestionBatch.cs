namespace FactoryConnect.Abstractions;

public sealed record ObservationIngestionBatch
{
    public ObservationIngestionBatch(
        ObservationCheckpoint? expectedCheckpoint,
        ObservationCheckpoint checkpoint,
        IReadOnlyList<SequencedMachineObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(observations);

        if (expectedCheckpoint is not null &&
            expectedCheckpoint.StreamId != checkpoint.StreamId)
        {
            throw new ArgumentException(
                "Expected and new checkpoints must identify the same stream.",
                nameof(expectedCheckpoint));
        }

        ExpectedCheckpoint = expectedCheckpoint;
        Checkpoint = checkpoint;
        Observations = observations.ToArray();
    }

    public ObservationCheckpoint? ExpectedCheckpoint { get; }

    public ObservationCheckpoint Checkpoint { get; }

    public IReadOnlyList<SequencedMachineObservation> Observations { get; }
}
