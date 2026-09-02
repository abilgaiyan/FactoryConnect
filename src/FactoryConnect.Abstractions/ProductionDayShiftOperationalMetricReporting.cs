using System.Collections.ObjectModel;

namespace FactoryConnect.Abstractions;

public sealed class ProductionDayShiftRosterCoverageRequiredException : InvalidOperationException
{
    public ProductionDayShiftRosterCoverageRequiredException(
        MachineId machineId,
        ProductionDayId productionDayId)
        : base($"Authoritative machine-shift roster coverage is required for reporting machine '{machineId}' and production day '{productionDayId}'.")
    {
        MachineId = machineId;
        ProductionDayId = productionDayId;
    }

    public MachineId MachineId { get; }

    public ProductionDayId ProductionDayId { get; }
}

public sealed record ProductionDayShiftReportingSource
{
    public ProductionDayShiftReportingSource(
        OperationalMetricReportingSource source,
        ProductionDayId productionDayId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(productionDayId);
        Source = source;
        ProductionDayId = productionDayId;
    }

    public OperationalMetricReportingSource Source { get; }

    public ProductionDayId ProductionDayId { get; }
}

public sealed record ProductionDayShiftOperationalMetricQuery
{
    public ProductionDayShiftOperationalMetricQuery(
        IEnumerable<ProductionDayShiftReportingSource> sources,
        OperationalMetricEvaluationContextKey contextKey,
        OperationalMetricDefinitionSelection? metrics = null,
        OperationalMetricStatusSelection? statuses = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(contextKey);
        contextKey.Validate();

        var snapshot = sources.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException(
                "At least one machine production-day reporting source is required.",
                nameof(sources));
        }

        if (snapshot.Any(static source => source is null))
        {
            throw new ArgumentException(
                "Machine production-day reporting sources cannot contain null values.",
                nameof(sources));
        }

        var duplicate = snapshot
            .GroupBy(static source => (source.Source.MachineId, source.ProductionDayId))
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                "Machine production-day reporting sources must be unique.",
                nameof(sources));
        }

        Sources = new ReadOnlyCollection<ProductionDayShiftReportingSource>(snapshot);
        ContextKey = contextKey;
        Metrics = metrics;
        Statuses = statuses;
    }

    public IReadOnlyList<ProductionDayShiftReportingSource> Sources { get; }

    public OperationalMetricEvaluationContextKey ContextKey { get; }

    public OperationalMetricDefinitionSelection? Metrics { get; }

    public OperationalMetricStatusSelection? Statuses { get; }
}

public sealed record ProductionDayShiftOperationalMetricReport
{
    public ProductionDayShiftOperationalMetricReport(
        OperationalMetricReportingSource source,
        ProductionDayId productionDayId,
        ProductionLineId productionLineId,
        ShiftOccurrenceId shiftOccurrenceId,
        OperationalMetricEvaluationContextKey contextKey,
        MetricAggregationCheckpoint? sourceRevision,
        IEnumerable<OperationalMetricReportItem> metrics)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(productionDayId);
        if (productionLineId.IsEmpty)
        {
            throw new ArgumentException("Production line ID is required.", nameof(productionLineId));
        }

        ArgumentNullException.ThrowIfNull(shiftOccurrenceId);
        ArgumentNullException.ThrowIfNull(contextKey);
        ArgumentNullException.ThrowIfNull(metrics);
        contextKey.Validate();

        if (shiftOccurrenceId.SiteId != productionDayId.SiteId)
        {
            throw new ArgumentException(
                "Shift occurrence and production day must belong to the same site.",
                nameof(shiftOccurrenceId));
        }

        if (sourceRevision is not null &&
            sourceRevision.StreamId.MachineId != source.MachineId)
        {
            throw new ArgumentException(
                "Source revision must belong to the reporting machine stream.",
                nameof(sourceRevision));
        }

        var snapshot = metrics.ToArray();
        if (snapshot.Any(static metric => metric is null))
        {
            throw new ArgumentException("Shift reports cannot contain null metrics.", nameof(metrics));
        }

        if (snapshot.GroupBy(static metric => metric.DefinitionId).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Shift reports cannot contain duplicate metric definitions.", nameof(metrics));
        }

        Source = source;
        ProductionDayId = productionDayId;
        ProductionLineId = productionLineId;
        ShiftOccurrenceId = shiftOccurrenceId;
        ContextKey = contextKey;
        SourceRevision = sourceRevision;
        Metrics = new ReadOnlyCollection<OperationalMetricReportItem>(snapshot);
    }

    public OperationalMetricReportingSource Source { get; }

    public ProductionDayId ProductionDayId { get; }

    public ProductionLineId ProductionLineId { get; }

    public ShiftOccurrenceId ShiftOccurrenceId { get; }

    /// <summary>
    /// Exact reporting-context selection under which this authoritative shift occurrence
    /// was queried. This remains present for zero-evidence occurrences; when an FC-027
    /// report exists the reader must verify that its context matches this value exactly.
    /// </summary>
    public OperationalMetricEvaluationContextKey ContextKey { get; }

    public MetricAggregationCheckpoint? SourceRevision { get; }

    public IReadOnlyList<OperationalMetricReportItem> Metrics { get; }
}

public interface IProductionDayShiftOperationalMetricReader
{
    ValueTask<IReadOnlyList<ProductionDayShiftOperationalMetricReport>> ReadAsync(
        ProductionDayShiftOperationalMetricQuery query,
        CancellationToken cancellationToken);
}
