using FactoryConnect.Abstractions;

namespace FactoryConnect.Persistence;

public sealed record PersistenceProviderServices
{
    public PersistenceProviderServices(
        IObservationIngestionStore observationIngestionStore,
        IProductionContextProcessingStore productionContextProcessingStore,
        IMetricInputReader metricInputReader,
        IMetricAggregationStore metricAggregationStore,
        IMetricAggregationRevisionReader? metricAggregationRevisionReader = null,
        IRevisionedOperationalMetricComponentSnapshotReader? revisionedOperationalMetricComponentSnapshotReader = null,
        IOperationalMetricProjectionStore? operationalMetricProjectionStore = null,
        IOperationalMetricProjectionQueryReader? operationalMetricProjectionQueryReader = null,
        IOperationalMetricReportingQueryProvider? operationalMetricReportingQueryProvider = null)
    {
        ArgumentNullException.ThrowIfNull(observationIngestionStore);
        ArgumentNullException.ThrowIfNull(productionContextProcessingStore);
        ArgumentNullException.ThrowIfNull(metricInputReader);
        ArgumentNullException.ThrowIfNull(metricAggregationStore);

        ObservationIngestionStore = observationIngestionStore;
        ProductionContextProcessingStore = productionContextProcessingStore;
        MetricInputReader = metricInputReader;
        MetricAggregationStore = metricAggregationStore;
        MetricAggregationRevisionReader = metricAggregationRevisionReader;
        RevisionedOperationalMetricComponentSnapshotReader = revisionedOperationalMetricComponentSnapshotReader;
        OperationalMetricProjectionStore = operationalMetricProjectionStore;
        OperationalMetricProjectionQueryReader = operationalMetricProjectionQueryReader;
        OperationalMetricReportingQueryProvider = operationalMetricReportingQueryProvider;
    }

    public IObservationIngestionStore ObservationIngestionStore { get; }

    public IProductionContextProcessingStore ProductionContextProcessingStore { get; }

    public IMetricInputReader MetricInputReader { get; }

    public IMetricAggregationStore MetricAggregationStore { get; }

    public IMetricAggregationRevisionReader? MetricAggregationRevisionReader { get; }

    public IRevisionedOperationalMetricComponentSnapshotReader? RevisionedOperationalMetricComponentSnapshotReader { get; }

    public IOperationalMetricProjectionStore? OperationalMetricProjectionStore { get; }

    public IOperationalMetricProjectionQueryReader? OperationalMetricProjectionQueryReader { get; }

    public IOperationalMetricReportingQueryProvider? OperationalMetricReportingQueryProvider { get; }
}
