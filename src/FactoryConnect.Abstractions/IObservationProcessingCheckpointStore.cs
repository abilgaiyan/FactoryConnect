namespace FactoryConnect.Abstractions;

public interface IObservationProcessingCheckpointStore
{
    ValueTask<ObservationProcessingCheckpoint?> ReadCheckpointAsync(
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        CancellationToken cancellationToken = default);

    ValueTask CommitAsync(
        ObservationProcessingCommit commit,
        CancellationToken cancellationToken = default);
}
