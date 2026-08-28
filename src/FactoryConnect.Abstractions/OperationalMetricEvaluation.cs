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
    DomainViolation,
}

public sealed record MetricOperandEvidence
{
    public MetricOperandEvidence(
        string operandName,
        string componentKey,
        MetricDimension dimension,
        decimal value,
        string unit,
        long inputCount,
        DateTimeOffset firstInputTimestamp,
        DateTimeOffset lastInputTimestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operandName);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);

        if (inputCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputCount));
        }

        if (lastInputTimestamp < firstInputTimestamp)
        {
            throw new ArgumentException(
                "Last input timestamp must not precede the first input timestamp.",
                nameof(lastInputTimestamp));
        }

        OperandName = operandName;
        ComponentKey = componentKey;
        Dimension = dimension;
        Value = value;
        Unit = unit;
        InputCount = inputCount;
        FirstInputTimestamp = firstInputTimestamp;
        LastInputTimestamp = lastInputTimestamp;
    }

    public string OperandName { get; }

    public string ComponentKey { get; }

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
        string componentKey,
        MetricDimension dimension,
        MetricAggregateValue aggregate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operandName);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentKey);
        ArgumentNullException.ThrowIfNull(aggregate);

        OperandName = operandName;
        ComponentKey = componentKey;
        Dimension = dimension;
        Aggregate = aggregate;
    }

    public string OperandName { get; }

    public string ComponentKey { get; }

    public MetricDimension Dimension { get; }

    public MetricAggregateValue Aggregate { get; }
}

public sealed record OperationalMetricComponentSet
{
    public OperationalMetricComponentSet(
        OperationalMetricEvaluationKey evaluationKey,
        IEnumerable<OperationalMetricComponent> components)
    {
        ArgumentNullException.ThrowIfNull(evaluationKey);
        ArgumentNullException.ThrowIfNull(components);

        var snapshot = components.ToArray();
        if (snapshot.Any(component => component is null))
        {
            throw new ArgumentException("Component sets cannot contain null values.", nameof(components));
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

        EvaluationKey = evaluationKey;
        Components = new ReadOnlyCollection<OperationalMetricComponent>(snapshot);
    }

    public OperationalMetricEvaluationKey EvaluationKey { get; }

    public IReadOnlyList<OperationalMetricComponent> Components { get; }
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
        IEnumerable<MetricOperandEvidence> operandEvidence)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentNullException.ThrowIfNull(operandEvidence);

        var evidenceSnapshot = operandEvidence.ToArray();
        if (evidenceSnapshot.Any(evidence => evidence is null))
        {
            throw new ArgumentException("Operand evidence cannot contain null values.", nameof(operandEvidence));
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
        OperandEvidence = new ReadOnlyCollection<MetricOperandEvidence>(evidenceSnapshot);
    }

    public OperationalMetricEvaluationKey Key { get; }

    public OperationalMetricEvaluationStatus Status { get; }

    public decimal? Value { get; }

    public string Unit { get; }

    public OperationalMetricEvaluationReasonCode? ReasonCode { get; }

    public string? ReasonOperandName { get; }

    public IReadOnlyList<MetricOperandEvidence> OperandEvidence { get; }
}

public interface IOperationalMetricEvaluator
{
    ValueTask<OperationalMetricEvaluation> EvaluateAsync(
        OperationalMetricEvaluationKey evaluationKey,
        CancellationToken cancellationToken);
}
