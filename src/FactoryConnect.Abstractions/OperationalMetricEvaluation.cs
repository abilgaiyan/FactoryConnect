using System.Collections.ObjectModel;

namespace FactoryConnect.Abstractions;

public enum OperationalMetricEvaluationStatus
{
    Calculated,
    Unavailable,
    InsufficientEvidence,
}

public enum OperationalMetricEvaluationReasonCode
{
    MissingOperand,
    MissingReferenceTime,
    ZeroDenominator,
    UnsupportedScope,
}

public sealed record OperationalMetricAggregateSourceIdentity
{
    public OperationalMetricAggregateSourceIdentity(
        MetricAggregationProcessorId processorId,
        MachineId machineId,
        OperationalMetricPeriodId periodId,
        string componentKey)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        if (machineId.IsEmpty)
        {
            throw new ArgumentException("Machine identifier must not be empty.", nameof(machineId));
        }

        ArgumentNullException.ThrowIfNull(periodId);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentKey);

        ProcessorId = processorId;
        MachineId = machineId;
        PeriodId = periodId;
        ComponentKey = componentKey;
    }

    public MetricAggregationProcessorId ProcessorId { get; }

    public MachineId MachineId { get; }

    public OperationalMetricPeriodId PeriodId { get; }

    public string ComponentKey { get; }
}

public sealed record MetricOperandEvidence
{
    public MetricOperandEvidence(
        string operandName,
        OperationalMetricAggregateSourceIdentity sourceIdentity,
        MetricAggregationCheckpoint sourceRevision,
        MetricDimension dimension,
        decimal value,
        string unit,
        long inputCount,
        DateTimeOffset firstInputTimestamp,
        DateTimeOffset lastInputTimestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operandName);
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        ArgumentNullException.ThrowIfNull(sourceRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputCount);

        if (sourceIdentity.ProcessorId != sourceRevision.ProcessorId)
        {
            throw new ArgumentException(
                "Operand source identity and source revision must belong to the same aggregation processor.",
                nameof(sourceRevision));
        }

        if (lastInputTimestamp < firstInputTimestamp)
        {
            throw new ArgumentException(
                "Last input timestamp must not precede the first input timestamp.",
                nameof(lastInputTimestamp));
        }

        OperandName = operandName;
        SourceIdentity = sourceIdentity;
        SourceRevision = sourceRevision;
        Dimension = dimension;
        Value = value;
        Unit = unit;
        InputCount = inputCount;
        FirstInputTimestamp = firstInputTimestamp;
        LastInputTimestamp = lastInputTimestamp;
    }

    public string OperandName { get; }

    public OperationalMetricAggregateSourceIdentity SourceIdentity { get; }

    public MetricAggregationCheckpoint SourceRevision { get; }

    public MetricDimension Dimension { get; }

    public decimal Value { get; }

    public string Unit { get; }

    public long InputCount { get; }

    public DateTimeOffset FirstInputTimestamp { get; }

    public DateTimeOffset LastInputTimestamp { get; }
}

public sealed record OperationalMetricComponent
{
    public OperationalMetricComponent(
        string operandName,
        OperationalMetricAggregateSourceIdentity sourceIdentity,
        MetricDimension dimension,
        MetricAggregateValue aggregate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operandName);
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        ArgumentNullException.ThrowIfNull(aggregate);

        OperandName = operandName;
        SourceIdentity = sourceIdentity;
        Dimension = dimension;
        Aggregate = aggregate;
    }

    public string OperandName { get; }

    public OperationalMetricAggregateSourceIdentity SourceIdentity { get; }

    public MetricDimension Dimension { get; }

    public MetricAggregateValue Aggregate { get; }
}

public sealed record OperationalMetricComponentSnapshot
{
    public OperationalMetricComponentSnapshot(
        OperationalMetricEvaluationKey evaluationKey,
        MetricAggregationCheckpoint revision,
        IEnumerable<OperationalMetricComponent> components)
    {
        ArgumentNullException.ThrowIfNull(evaluationKey);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(components);

        var snapshot = components.ToArray();
        if (snapshot.Any(component => component is null))
        {
            throw new ArgumentException("Component snapshots cannot contain null values.", nameof(components));
        }

        var duplicate = snapshot
            .GroupBy(component => component.OperandName, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Duplicate component evidence for operand '{duplicate.Key}'.",
                nameof(components));
        }

        foreach (var component in snapshot)
        {
            if (component.SourceIdentity.ProcessorId != revision.ProcessorId ||
                component.SourceIdentity.MachineId != evaluationKey.MachineId ||
                component.SourceIdentity.PeriodId != evaluationKey.PeriodId)
            {
                throw new ArgumentException(
                    "Every component must belong to the evaluation key and coherent snapshot revision.",
                    nameof(components));
            }
        }

        EvaluationKey = evaluationKey;
        Revision = revision;
        Components = new ReadOnlyCollection<OperationalMetricComponent>(snapshot);
    }

    public OperationalMetricEvaluationKey EvaluationKey { get; }

    public MetricAggregationCheckpoint Revision { get; }

    public IReadOnlyList<OperationalMetricComponent> Components { get; }
}

public sealed record OperationalMetricComponentSnapshotRequest
{
    public OperationalMetricComponentSnapshotRequest(
        OperationalMetricEvaluationKey evaluationKey,
        MetricAggregationProcessorId processorId,
        IEnumerable<OperationalMetricOperandDefinition> operands)
    {
        ArgumentNullException.ThrowIfNull(evaluationKey);
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(operands);

        var operandSnapshot = operands.ToArray();
        if (operandSnapshot.Any(operand => operand is null))
        {
            throw new ArgumentException("Snapshot requests cannot contain null operands.", nameof(operands));
        }

        if (operandSnapshot.Any(operand => operand.Source is not OperationalMetricOperandSource.Component))
        {
            throw new ArgumentException(
                "FC-027.2 component snapshot requests may contain only component operands.",
                nameof(operands));
        }

        EvaluationKey = evaluationKey;
        ProcessorId = processorId;
        Operands = new ReadOnlyCollection<OperationalMetricOperandDefinition>(operandSnapshot);
    }

    public OperationalMetricEvaluationKey EvaluationKey { get; }

    public MetricAggregationProcessorId ProcessorId { get; }

    public IReadOnlyList<OperationalMetricOperandDefinition> Operands { get; }
}

public interface IOperationalMetricComponentSnapshotReader
{
    ValueTask<OperationalMetricComponentSnapshot> ReadAsync(
        OperationalMetricComponentSnapshotRequest request,
        CancellationToken cancellationToken);
}

public sealed record OperationalMetricEvaluation
{
    public OperationalMetricEvaluation(
        OperationalMetricEvaluationKey key,
        OperationalMetricEvaluationStatus status,
        decimal? value,
        string unit,
        OperationalMetricEvaluationReasonCode? reasonCode,
        string? reasonOperandName,
        MetricAggregationCheckpoint sourceRevision,
        IEnumerable<MetricOperandEvidence> operandEvidence)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentNullException.ThrowIfNull(sourceRevision);
        ArgumentNullException.ThrowIfNull(operandEvidence);

        var evidenceSnapshot = operandEvidence.ToArray();
        if (evidenceSnapshot.Any(evidence => evidence is null))
        {
            throw new ArgumentException("Operand evidence cannot contain null values.", nameof(operandEvidence));
        }

        if (evidenceSnapshot.Any(evidence => evidence.SourceRevision != sourceRevision))
        {
            throw new ArgumentException(
                "All operand evidence must come from the evaluation source revision.",
                nameof(operandEvidence));
        }

        if (status == OperationalMetricEvaluationStatus.Calculated)
        {
            if (value is null || reasonCode is not null || reasonOperandName is not null)
            {
                throw new ArgumentException("Calculated evaluations require a value and no failure reason.", nameof(status));
            }
        }
        else if (value is not null || reasonCode is null)
        {
            throw new ArgumentException("Non-calculated evaluations require a reason and no value.", nameof(status));
        }

        Key = key;
        Status = status;
        Value = value;
        Unit = unit;
        ReasonCode = reasonCode;
        ReasonOperandName = reasonOperandName;
        SourceRevision = sourceRevision;
        OperandEvidence = new ReadOnlyCollection<MetricOperandEvidence>(evidenceSnapshot);
    }

    public OperationalMetricEvaluationKey Key { get; }

    public OperationalMetricEvaluationStatus Status { get; }

    public decimal? Value { get; }

    public string Unit { get; }

    public OperationalMetricEvaluationReasonCode? ReasonCode { get; }

    public string? ReasonOperandName { get; }

    public MetricAggregationCheckpoint SourceRevision { get; }

    public IReadOnlyList<MetricOperandEvidence> OperandEvidence { get; }
}

public interface IOperationalMetricEvaluator
{
    ValueTask<OperationalMetricEvaluation> EvaluateAsync(
        OperationalMetricEvaluationKey evaluationKey,
        CancellationToken cancellationToken);
}
