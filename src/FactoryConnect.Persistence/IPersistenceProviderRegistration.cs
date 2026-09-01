namespace FactoryConnect.Persistence;

[Flags]
public enum PersistenceProviderCapabilities
{
    None = 0,
    ObservationIngestion = 1 << 0,
    ProductionContextProcessing = 1 << 1,
    MetricInputReading = 1 << 2,
    MetricAggregation = 1 << 3,
    MetricAggregationRevisionReading = 1 << 4,
    RevisionedOperationalMetricSnapshotReading = 1 << 5,
    OperationalMetricProjectionStorage = 1 << 6,
    OperationalMetricProjectionQuery = 1 << 7,
    OperationalMetricReportingQuery = 1 << 8,
    MachineShiftOccurrenceRoster = 1 << 9,

    Core = ObservationIngestion |
        ProductionContextProcessing |
        MetricInputReading |
        MetricAggregation,

    OperationalMetrics = MetricAggregationRevisionReading |
        RevisionedOperationalMetricSnapshotReading |
        OperationalMetricProjectionStorage |
        OperationalMetricProjectionQuery,

    All = Core |
        OperationalMetrics |
        OperationalMetricReportingQuery |
        MachineShiftOccurrenceRoster,
}

public interface IPersistenceProviderRegistration
{
    string ProviderKey { get; }

    PersistenceProviderCapabilities Capabilities { get; }

    PersistenceProviderServices Create(IServiceProvider services);
}
