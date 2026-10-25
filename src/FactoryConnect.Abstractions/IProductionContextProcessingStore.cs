namespace FactoryConnect.Abstractions;

public interface IProductionContextProcessingStore
{
    Task<ObservationProcessingCheckpoint?> ReadCheckpointAsync(
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        CancellationToken cancellationToken);

    Task<ObservationProcessingCheckpoint?> ReadCheckpointAsync(
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        DateTimeOffset? productionStartsAt,
        CancellationToken cancellationToken)
    {
        if (productionStartsAt is not null)
            throw new NotSupportedException("This store does not support production admission boundaries.");
        return ReadCheckpointAsync(processorId, streamId, cancellationToken);
    }

    Task CommitAsync(
        ProductionContextProcessingCommit commit,
        CancellationToken cancellationToken);
}
