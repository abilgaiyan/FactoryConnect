using FactoryConnect.Abstractions;

namespace FactoryConnect.Edge;

public sealed record MtConnectStartupState
{
    public MtConnectStartupState(
        ObservationStreamId streamId,
        ulong fromSequence,
        ObservationCheckpoint? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(streamId);

        if (checkpoint is not null &&
            checkpoint.StreamId != streamId)
        {
            throw new ArgumentException(
                "Checkpoint must identify the startup stream.",
                nameof(checkpoint));
        }

        if (checkpoint is not null &&
            checkpoint.NextSequence != fromSequence)
        {
            throw new ArgumentException(
                "Restored sequence must equal the checkpoint next sequence.",
                nameof(fromSequence));
        }

        StreamId = streamId;
        FromSequence = fromSequence;
        Checkpoint = checkpoint;
    }

    public ObservationStreamId StreamId { get; }

    public ulong FromSequence { get; }

    public ObservationCheckpoint? Checkpoint { get; }

    public bool IsRestored => Checkpoint is not null;
}
