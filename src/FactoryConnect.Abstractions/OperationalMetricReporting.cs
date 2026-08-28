using System.Collections.ObjectModel;

namespace FactoryConnect.Abstractions;

public sealed record OperationalMetricProjectionSummary
{
    public OperationalMetricProjectionSummary(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey key,
        OperationalMetricEvaluationStatus status,
        decimal? value,
        string unit,
        OperationalMetricEvaluationReasonCode? reasonCode,
        string? reasonOperandName,
        MetricAggregationCheckpoint sourceRevision)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentNullException.ThrowIfNull(sourceRevision);

        if (sourceRevision.StreamId.MachineId != key.MachineId)
        {
            throw new ArgumentException(
                "Projection summary source revision must belong to the evaluation machine stream.",
                nameof(sourceRevision));
        }

        if (status == OperationalMetricEvaluationStatus.Calculated)
        {
            if (value is null || reasonCode is not null || reasonOperandName is not null)
            {
                throw new ArgumentException(
                    "Calculated projection summaries require a value and no failure reason.",
                    nameof(status));
            }
        }
        else if (value is not null || reasonCode is null)
        {
            throw new ArgumentException(
                "Non-calculated projection summaries require a reason and no value.",
                nameof(status));
        }

        ProcessorId = processorId;
        Key = key;
        Status = status;
        Value = value;
        Unit = unit;
        ReasonCode = reasonCode;
        ReasonOperandName = reasonOperandName;
        SourceRevision = sourceRevision;
    }

    public OperationalMetricProjectionSummary(OperationalMetricProjection projection)
        : this(
            projection?.ProcessorId ?? throw new ArgumentNullException(nameof(projection)),
            projection.Key,
            projection.Status,
            projection.Value,
            projection.Unit,
            projection.ReasonCode,
            projection.ReasonOperandName,
            projection.SourceRevision)
    {
    }

    public OperationalMetricProjectionProcessorId ProcessorId { get; }

    public OperationalMetricEvaluationKey Key { get; }

    public OperationalMetricEvaluationStatus Status { get; }

    public decimal? Value { get; }

    public string Unit { get; }

    public OperationalMetricEvaluationReasonCode? ReasonCode { get; }

    public string? ReasonOperandName { get; }

    public MetricAggregationCheckpoint SourceRevision { get; }
}

public sealed record OperationalMetricReportItem
{
    public OperationalMetricReportItem(OperationalMetricProjectionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        DefinitionId = summary.Key.DefinitionId;
        Status = summary.Status;
        Value = summary.Value;
        Unit = summary.Unit;
        ReasonCode = summary.ReasonCode;
        ReasonOperandName = summary.ReasonOperandName;
    }

    public OperationalMetricDefinitionId DefinitionId { get; }

    public OperationalMetricEvaluationStatus Status { get; }

    public decimal? Value { get; }

    public string Unit { get; }

    public OperationalMetricEvaluationReasonCode? ReasonCode { get; }

    public string? ReasonOperandName { get; }
}

public sealed record OperationalMetricReportDetail
{
    public OperationalMetricReportDetail(OperationalMetricProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ProcessorId = projection.ProcessorId;
        Key = projection.Key;
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

    public OperationalMetricProjectionProcessorId ProcessorId { get; }

    public OperationalMetricEvaluationKey Key { get; }

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
        MetricAggregationCheckpoint sourceRevision,
        IEnumerable<OperationalMetricReportItem> metrics)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        if (machineId.IsEmpty)
        {
            throw new ArgumentException("Machine ID is required.", nameof(machineId));
        }

        ArgumentNullException.ThrowIfNull(contextKey);
        ArgumentNullException.ThrowIfNull(sourceRevision);
        ArgumentNullException.ThrowIfNull(metrics);
        contextKey.Validate();

        if (sourceRevision.StreamId.MachineId != machineId)
        {
            throw new ArgumentException(
                "Report source revision must belong to the report machine stream.",
                nameof(sourceRevision));
        }

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
        SourceRevision = sourceRevision;
        Metrics = new ReadOnlyCollection<OperationalMetricReportItem>(snapshot);
    }

    public OperationalMetricProjectionProcessorId ProcessorId { get; }

    public MachineId MachineId { get; }

    public OperationalMetricEvaluationContextKey ContextKey { get; }

    public MetricAggregationCheckpoint SourceRevision { get; }

    public IReadOnlyList<OperationalMetricReportItem> Metrics { get; }
}

public sealed record ShiftOperationalMetricReport : OperationalMetricPeriodReport
{
    public ShiftOperationalMetricReport(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        ShiftOccurrenceId shiftOccurrenceId,
        OperationalMetricEvaluationContextKey contextKey,
        MetricAggregationCheckpoint sourceRevision,
        IEnumerable<OperationalMetricReportItem> metrics)
        : base(processorId, machineId, contextKey, sourceRevision, metrics)
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
        MetricAggregationCheckpoint sourceRevision,
        IEnumerable<OperationalMetricReportItem> metrics)
        : base(processorId, machineId, contextKey, sourceRevision, metrics)
    {
        ArgumentNullException.ThrowIfNull(productionDayId);
        ProductionDayId = productionDayId;
    }

    public ProductionDayId ProductionDayId { get; }
}

public interface IOperationalMetricProjectionQueryReader
{
    ValueTask<IReadOnlyList<OperationalMetricProjectionSummary>> ReadPeriodSummariesAsync(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        OperationalMetricPeriodId periodId,
        OperationalMetricEvaluationContextKey contextKey,
        CancellationToken cancellationToken);

    ValueTask<OperationalMetricProjection?> ReadDetailAsync(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey key,
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

    ValueTask<OperationalMetricReportDetail?> ReadMetricDetailAsync(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        OperationalMetricPeriodId periodId,
        OperationalMetricEvaluationContextKey contextKey,
        OperationalMetricDefinitionId definitionId,
        CancellationToken cancellationToken);
}
