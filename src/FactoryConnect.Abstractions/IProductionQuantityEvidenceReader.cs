namespace FactoryConnect.Abstractions;

public interface IProductionQuantityEvidenceReader
{
    Task<IReadOnlyList<DurableProductionQuantityEvidence>> ReadAsync(
        ObservationStreamId streamId,
        ObservationPosition? afterPosition,
        int batchSize,
        CancellationToken cancellationToken);
}
