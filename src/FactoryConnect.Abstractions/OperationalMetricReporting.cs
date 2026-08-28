using System.Collections.ObjectModel;

namespace FactoryConnect.Abstractions;

public sealed record OperationalMetricReportItem
{
    public OperationalMetricReportItem(OperationalMetricProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        DefinitionId = projection.Key.DefinitionId;
        Status = projection.Status;
        Value = projection.Value;
        Unit = projection.Unit;
        ReasonCode = projection.ReasonCode;
        ReasonOperandName = projection.ReasonOperandName;
        SourceRevision = projection.SourceRevision;
        OperandEvidence = new ReadOnlyCollection<OperationalMetricComponentProjectionEvidence>(
            projection.OperandEvidence.ToArray());
        DependencyEvidence = new ReadOnlyCollection<OperationalMetricDependencyProjectionEvidence>(
            projection.DependencyEvidence.ToArray());
    }

    public OperationalMetricDefinitionId DefinitionId { get; }

    public OperationalMetricEvaluationStatus Status { get; }

    public decimal? Value { get; }

    public string Unit { get; }

    public OperationalMetricEvaluationReasonCode? ReasonCode { get; }

    public string? ReasonOperandName { get; }

    public MetricAggregationCheckpoint SourceRevision { get; }

    public IReadOnlyList<OperationalMetricComponentProjectionEvidence> OperandEvidence { get; }

    public IReadOnlyList<OperationalMetricDependencyProjectionEvidence> DependencyEvidence { get; }
}

public abstract record OperationalMetricPeriodReport
{
    private protected OperationalMetricPeriodReport(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        OperationalMetricEvaluationContextKey contextKey,
        IEnumerable<OperationalMetricReportItem> metrics)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        if (machineId.IsEmpty)
        {
            throw new ArgumentException("Machine ID is required.", nameof(machineId));
        }

        ArgumentNullException.ThrowIfNull(contextKey);
        ArgumentNullException.ThrowIfNull(metrics);
        contextKey.Validate();

        var snapshot = metrics.ToArray();
        if (snapshot.Any(static metric => metric is null))
        {
            throw new ArgumentException("Metric reports cannot contain null metric items.", nameof(metrics));
        }

        var duplicate = snapshot
            .GroupBy(static metric => metric.DefinitionId)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Metric reports cannot contain duplicate definition '{duplicate.Key.MetricKey}/{duplicate.Key.Version}'.",
                nameof(metrics));
        }

        ProcessorId = processorId;
        MachineId = machineId;
        ContextKey = contextKey;
        Metrics = new ReadOnlyCollection<OperationalMetricReportItem>(snapshot);
    }

    public OperationalMetricProjectionProcessorId ProcessorId { get; }

    public MachineId MachineId { get; }

    public OperationalMetricEvaluationContextKey ContextKey { get; }

    public IReadOnlyList<OperationalMetricReportItem> Metrics { get; }
}

public sealed record ShiftOperationalMetricReport : OperationalMetricPeriodReport
{
    public ShiftOperationalMetricReport(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        ShiftOccurrenceId shiftOccurrenceId,
        OperationalMetricEvaluationContextKey contextKey,
        IEnumerable<OperationalMetricReportItem> metrics)
        : base(processorId, machineId, contextKey, metrics)
    {
        ArgumentNullException.ThrowIfNull(shiftOccurrenceId);
        ShiftOccurrenceId = shiftOccurrenceId;
    }

    public ShiftOccurrenceId ShiftOccurrenceId { get; }
}

public sealed record ProductionDayOperationalMetricReport : OperationalMetricPeriodReport
{
    public ProductionDayOperationalMetricReport(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        ProductionDayId productionDayId,
        OperationalMetricEvaluationContextKey contextKey,
        IEnumerable<OperationalMetricReportItem> metrics)
        : base(processorId, machineId, contextKey, metrics)
    {
        ArgumentNullException.ThrowIfNull(productionDayId);
        ProductionDayId = productionDayId;
    }

    public ProductionDayId ProductionDayId { get; }
}

public interface IOperationalMetricProjectionQueryReader
{
    ValueTask<IReadOnlyList<OperationalMetricProjection>> ReadPeriodAsync(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        OperationalMetricPeriodId periodId,
        OperationalMetricEvaluationContextKey contextKey,
        CancellationToken cancellationToken);
}

public interface IOperationalMetricReportReader
{
    ValueTask<ShiftOperationalMetricReport?> ReadShiftAsync(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        ShiftOccurrenceId shiftOccurrenceId,
        OperationalMetricEvaluationContextKey contextKey,
        CancellationToken cancellationToken);

    ValueTask<ProductionDayOperationalMetricReport?> ReadProductionDayAsync(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        ProductionDayId productionDayId,
        OperationalMetricEvaluationContextKey contextKey,
        CancellationToken cancellationToken);
}
