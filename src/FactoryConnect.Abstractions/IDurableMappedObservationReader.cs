namespace FactoryConnect.Abstractions;

public interface IDurableMappedObservationReader
{
    ValueTask<MappedObservationReadBatch> ReadAsync(
        MappedObservationReadRequest request,
        CancellationToken cancellationToken = default);
}
