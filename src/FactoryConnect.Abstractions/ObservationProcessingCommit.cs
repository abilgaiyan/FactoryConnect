namespace FactoryConnect.Abstractions;

public sealed record ObservationProcessingCommit
{
    public ObservationProcessingCommit(
        ObservationProcessingCheckpoint? expectedCheckpoint,
        ObservationProcessingCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        if (expectedCheckpoint is not null &&
            (expectedCheckpoint.ProcessorId != checkpoint.ProcessorId ||
             expectedCheckpoint.StreamId != checkpoint.StreamId))
        {
            throw new ArgumentException(
                "Expected and new processing checkpoints must identify the same processor and stream.",
                nameof(expectedCheckpoint));
        }

        if (expectedCheckpoint is not null &&
            expectedCheckpoint.Position.CompareTo(checkpoint.Position) > 0)
        {
            throw new ArgumentException(
                "A processing checkpoint cannot move backwards.",
                nameof(checkpoint));
        }

        ExpectedCheckpoint = expectedCheckpoint;
        Checkpoint = checkpoint;
    }

    public ObservationProcessingCheckpoint? ExpectedCheckpoint { get; }

    public ObservationProcessingCheckpoint Checkpoint { get; }
}
