namespace FactoryConnect.Abstractions;

public interface IObservationIngestionStore
{
    ValueTask<ObservationCheckpoint?> ReadCheckpointAsync(
        ObservationStreamId streamId,
        CancellationToken cancellationToken = default);

    ValueTask CommitAsync(
        ObservationIngestionBatch batch,
        CancellationToken cancellationToken = default);
}
