namespace FactoryConnect.Abstractions;

public interface IProductionContextProcessingStore
{
    Task<ObservationProcessingCheckpoint?> ReadCheckpointAsync(
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        CancellationToken cancellationToken);

    Task CommitAsync(
        ProductionContextProcessingCommit commit,
        CancellationToken cancellationToken);
}
