namespace FactoryConnect.Abstractions;

public sealed record MappedObservationReadBatch
{
    public MappedObservationReadBatch(
        ObservationStreamId streamId,
        IReadOnlyList<DurableMappedMachineObservation> observations,
        bool hasMore)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(observations);

        ObservationPosition? previous = null;

        foreach (var observation in observations)
        {
            ArgumentNullException.ThrowIfNull(observation);

            if (observation.StreamId != streamId)
            {
                throw new ArgumentException(
                    "Every mapped observation must belong to the read stream.",
                    nameof(observations));
            }

            if (previous is not null &&
                previous >= observation.Position)
            {
                throw new ArgumentException(
                    "Mapped observations must be ordered by strictly increasing position.",
                    nameof(observations));
            }

            previous = observation.Position;
        }

        StreamId = streamId;
        Observations = observations.ToArray();
        HasMore = hasMore;
    }

    public ObservationStreamId StreamId { get; }

    public IReadOnlyList<DurableMappedMachineObservation> Observations { get; }

    public bool HasMore { get; }
}
