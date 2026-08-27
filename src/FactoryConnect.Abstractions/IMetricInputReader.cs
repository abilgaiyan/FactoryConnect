namespace FactoryConnect.Abstractions;

public interface IMetricInputReader
{
    ValueTask<MetricInputReadBatch> ReadAsync(
        MetricInputReadRequest request,
        CancellationToken cancellationToken);
}
