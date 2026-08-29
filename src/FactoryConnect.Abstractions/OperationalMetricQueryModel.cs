namespace FactoryConnect.Abstractions;

public abstract record OperationalMetricQueryItem
{
    private protected OperationalMetricQueryItem(OperationalMetricProjectionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        ProcessorId = summary.ProcessorId;
        MachineId = summary.Key.MachineId;
        ContextKey = summary.Key.ContextKey;
        DefinitionId = summary.Key.DefinitionId;
        Status = summary.Status;
        Value = summary.Value;
        Unit = summary.Unit;
        ReasonCode = summary.ReasonCode;
        ReasonOperandName = summary.ReasonOperandName;
        SourceRevision = summary.SourceRevision;
    }

    public OperationalMetricProjectionProcessorId ProcessorId { get; }

    public MachineId MachineId { get; }

    public OperationalMetricEvaluationContextKey ContextKey { get; }

    public OperationalMetricDefinitionId DefinitionId { get; }

    public OperationalMetricEvaluationStatus Status { get; }

    public decimal? Value { get; }

    public string Unit { get; }

    public OperationalMetricEvaluationReasonCode? ReasonCode { get; }

    public string? ReasonOperandName { get; }

    public MetricAggregationCheckpoint SourceRevision { get; }

    public static OperationalMetricQueryItem FromSummary(
        OperationalMetricProjectionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return summary.Key.PeriodId switch
        {
            OperationalMetricPeriodId.Shift shift =>
                new ShiftOperationalMetricQueryItem(summary, shift.ShiftOccurrenceId),
            OperationalMetricPeriodId.ProductionDay productionDay =>
                new ProductionDayOperationalMetricQueryItem(
                    summary,
                    productionDay.ProductionDayId),
            _ => throw new ArgumentOutOfRangeException(nameof(summary)),
        };
    }
}

public sealed record ShiftOperationalMetricQueryItem : OperationalMetricQueryItem
{
    internal ShiftOperationalMetricQueryItem(
        OperationalMetricProjectionSummary summary,
        ShiftOccurrenceId shiftOccurrenceId)
        : base(summary)
    {
        ArgumentNullException.ThrowIfNull(shiftOccurrenceId);
        ShiftOccurrenceId = shiftOccurrenceId;
    }

    public ShiftOccurrenceId ShiftOccurrenceId { get; }
}

public sealed record ProductionDayOperationalMetricQueryItem : OperationalMetricQueryItem
{
    internal ProductionDayOperationalMetricQueryItem(
        OperationalMetricProjectionSummary summary,
        ProductionDayId productionDayId)
        : base(summary)
    {
        ArgumentNullException.ThrowIfNull(productionDayId);
        ProductionDayId = productionDayId;
    }

    public ProductionDayId ProductionDayId { get; }
}

public interface IOperationalMetricQueryReader
{
    ValueTask<ReportingPage<OperationalMetricQueryItem>> ReadAsync(
        OperationalMetricReportQuery query,
        CancellationToken cancellationToken);
}
