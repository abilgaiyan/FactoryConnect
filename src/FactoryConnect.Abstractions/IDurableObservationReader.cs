namespace FactoryConnect.Abstractions;

public interface IDurableObservationReader
{
    ValueTask<ObservationReadBatch> ReadAsync(
        ObservationReadRequest request,
        CancellationToken cancellationToken = default);
}
