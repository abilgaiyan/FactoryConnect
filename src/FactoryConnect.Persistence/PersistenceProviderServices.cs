using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence;

public sealed record PersistenceProviderServices
{
    public PersistenceProviderServices(
        IObservationIngestionStore observationIngestionStore,
        IProductionContextProcessingStore productionContextProcessingStore,
        IMetricInputReader metricInputReader,
        IMetricAggregationStore metricAggregationStore,
        IMetricAggregationRevisionReader metricAggregationRevisionReader,
        IRevisionedOperationalMetricComponentSnapshotReader revisionedOperationalMetricComponentSnapshotReader,
        IOperationalMetricProjectionStore operationalMetricProjectionStore,
        IOperationalMetricProjectionQueryReader operationalMetricProjectionQueryReader)
    {
        ArgumentNullException.ThrowIfNull(observationIngestionStore);
        ArgumentNullException.ThrowIfNull(productionContextProcessingStore);
        ArgumentNullException.ThrowIfNull(metricInputReader);
        ArgumentNullException.ThrowIfNull(metricAggregationStore);
        ArgumentNullException.ThrowIfNull(metricAggregationRevisionReader);
        ArgumentNullException.ThrowIfNull(revisionedOperationalMetricComponentSnapshotReader);
        ArgumentNullException.ThrowIfNull(operationalMetricProjectionStore);
        ArgumentNullException.ThrowIfNull(operationalMetricProjectionQueryReader);

        ObservationIngestionStore = observationIngestionStore;
        ProductionContextProcessingStore = productionContextProcessingStore;
        MetricInputReader = metricInputReader;
        MetricAggregationStore = metricAggregationStore;
        MetricAggregationRevisionReader = metricAggregationRevisionReader;
        RevisionedOperationalMetricComponentSnapshotReader = revisionedOperationalMetricComponentSnapshotReader;
        OperationalMetricProjectionStore = operationalMetricProjectionStore;
        OperationalMetricProjectionQueryReader = operationalMetricProjectionQueryReader;
    }

    public IObservationIngestionStore ObservationIngestionStore { get; }

    public IProductionContextProcessingStore ProductionContextProcessingStore { get; }

    public IMetricInputReader MetricInputReader { get; }

    public IMetricAggregationStore MetricAggregationStore { get; }

    public IMetricAggregationRevisionReader MetricAggregationRevisionReader { get; }

    public IRevisionedOperationalMetricComponentSnapshotReader RevisionedOperationalMetricComponentSnapshotReader { get; }

    public IOperationalMetricProjectionStore OperationalMetricProjectionStore { get; }

    public IOperationalMetricProjectionQueryReader OperationalMetricProjectionQueryReader { get; }
}
