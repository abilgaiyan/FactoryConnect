namespace FactoryConnect.Abstractions;

public sealed record ObservationReadBatch
{
    public ObservationReadBatch(
        ObservationStreamId streamId,
        IReadOnlyList<DurableMachineObservation> observations,
        bool hasMore)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(observations);

        var snapshot = observations.ToArray();

        for (var index = 0; index < snapshot.Length; index++)
        {
            if (snapshot[index].StreamId != streamId)
            {
                throw new ArgumentException(
                    "Every durable observation must belong to the requested stream.",
                    nameof(observations));
            }

            if (index > 0 &&
                snapshot[index - 1].Position.CompareTo(
                    snapshot[index].Position) >= 0)
            {
                throw new ArgumentException(
                    "Durable observations must be ordered by strictly increasing position.",
                    nameof(observations));
            }
        }

        StreamId = streamId;
        Observations = snapshot;
        HasMore = hasMore;
    }

    public ObservationStreamId StreamId { get; }

    public IReadOnlyList<DurableMachineObservation> Observations { get; }

    public bool HasMore { get; }
}
