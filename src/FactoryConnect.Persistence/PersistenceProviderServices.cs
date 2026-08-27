using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence;

public sealed record PersistenceProviderServices
{
    public PersistenceProviderServices(
        IObservationIngestionStore observationIngestionStore,
        IProductionContextProcessingStore productionContextProcessingStore,
        IMetricInputReader metricInputReader,
        IMetricAggregationStore metricAggregationStore)
    {
        ArgumentNullException.ThrowIfNull(observationIngestionStore);
        ArgumentNullException.ThrowIfNull(productionContextProcessingStore);
        ArgumentNullException.ThrowIfNull(metricInputReader);
        ArgumentNullException.ThrowIfNull(metricAggregationStore);

        ObservationIngestionStore = observationIngestionStore;
        ProductionContextProcessingStore = productionContextProcessingStore;
        MetricInputReader = metricInputReader;
        MetricAggregationStore = metricAggregationStore;
    }

    public IObservationIngestionStore ObservationIngestionStore { get; }

    public IProductionContextProcessingStore ProductionContextProcessingStore { get; }

    public IMetricInputReader MetricInputReader { get; }

    public IMetricAggregationStore MetricAggregationStore { get; }
}
