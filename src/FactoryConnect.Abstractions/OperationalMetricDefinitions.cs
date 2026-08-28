namespace FactoryConnect.Abstractions;

public sealed record OperationalMetricDefinitionId
{
    public OperationalMetricDefinitionId(string metricKey, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        MetricKey = metricKey.Trim();
        Version = version.Trim();
    }

    public string MetricKey { get; }

    public string Version { get; }
}

public enum OperationalMetricEvaluationScope
{
    Shift,
    ProductionDay,
}

public sealed record OperationalMetricScopeSet
{
    public required bool SupportsShift { get; init; }

    public required bool SupportsProductionDay { get; init; }

    public bool Supports(OperationalMetricEvaluationScope scope) => scope switch
    {
        OperationalMetricEvaluationScope.Shift => SupportsShift,
        OperationalMetricEvaluationScope.ProductionDay => SupportsProductionDay,
        _ => false,
    };
}

public abstract record OperationalMetricPeriodId
{
    private OperationalMetricPeriodId()
    {
    }

    public sealed record Shift(ShiftOccurrenceId ShiftOccurrenceId) : OperationalMetricPeriodId;

    public sealed record ProductionDay(ProductionDayId ProductionDayId) : OperationalMetricPeriodId;
}

public sealed record OperationalMetricEvaluationContextKey
{
    public static OperationalMetricEvaluationContextKey Unpartitioned { get; } = new();

    public ProductionOrderId? ProductionOrderId { get; init; }

    public OperationId? OperationId { get; init; }

    public PartId? PartId { get; init; }

    public OperatorId? OperatorId { get; init; }

    public void Validate()
    {
        if (ProductionOrderId is { IsEmpty: true })
        {
            throw new ArgumentException("Production order ID cannot be empty when specified.", nameof(ProductionOrderId));
        }

        if (OperationId is { IsEmpty: true })
        {
            throw new ArgumentException("Operation ID cannot be empty when specified.", nameof(OperationId));
        }

        if (PartId is { IsEmpty: true })
        {
            throw new ArgumentException("Part ID cannot be empty when specified.", nameof(PartId));
        }

        if (OperatorId is { IsEmpty: true })
        {
            throw new ArgumentException("Operator ID cannot be empty when specified.", nameof(OperatorId));
        }
    }
}

public sealed record OperationalMetricEvaluationKey
{
    public OperationalMetricEvaluationKey(
        MachineId machineId,
        OperationalMetricPeriodId periodId,
        OperationalMetricDefinitionId definitionId,
        OperationalMetricEvaluationContextKey contextKey)
    {
        if (machineId.IsEmpty)
        {
            throw new ArgumentException("Machine ID is required.", nameof(machineId));
        }

        ArgumentNullException.ThrowIfNull(periodId);
        ArgumentNullException.ThrowIfNull(definitionId);
        ArgumentNullException.ThrowIfNull(contextKey);
        contextKey.Validate();

        MachineId = machineId;
        PeriodId = periodId;
        DefinitionId = definitionId;
        ContextKey = contextKey;
    }

    public MachineId MachineId { get; }

    public OperationalMetricPeriodId PeriodId { get; }

    public OperationalMetricDefinitionId DefinitionId { get; }

    public OperationalMetricEvaluationContextKey ContextKey { get; }

    public OperationalMetricEvaluationScope Scope => PeriodId switch
    {
        OperationalMetricPeriodId.Shift => OperationalMetricEvaluationScope.Shift,
        OperationalMetricPeriodId.ProductionDay => OperationalMetricEvaluationScope.ProductionDay,
        _ => throw new InvalidOperationException("Unsupported operational metric period type."),
    };
}

public enum MetricDimension
{
    Duration,
    Quantity,
    Ratio,
}

public abstract record OperationalMetricOperandSource
{
    private OperationalMetricOperandSource()
    {
    }

    public sealed record Component(string ComponentKey) : OperationalMetricOperandSource;

    public sealed record EvaluatedMetric(OperationalMetricDefinitionId DefinitionId) : OperationalMetricOperandSource;
}

public sealed record OperationalMetricOperandDefinition
{
    public required string OperandName { get; init; }

    public required OperationalMetricOperandSource Source { get; init; }

    public required MetricDimension RequiredDimension { get; init; }

    public required string RequiredUnit { get; init; }
}

public abstract record OperationalMetricFormula
{
    private OperationalMetricFormula()
    {
    }

    public sealed record Ratio(string NumeratorOperand, string DenominatorOperand) : OperationalMetricFormula;

    public sealed record Product(IReadOnlyList<string> FactorOperands) : OperationalMetricFormula;
}

public sealed record OperationalMetricDomainConstraints
{
    public decimal? MinimumInclusive { get; init; }

    public decimal? MaximumInclusive { get; init; }
}

public sealed record OperationalMetricPrecisionPolicy
{
    public required int DurableDecimalScale { get; init; }

    public required MidpointRounding RoundingMode { get; init; }
}

public sealed record OperationalMetricDefinition
{
    public required OperationalMetricDefinitionId Id { get; init; }

    public string? DisplayName { get; init; }

    public required OperationalMetricScopeSet SupportedScopes { get; init; }

    public required IReadOnlyList<OperationalMetricOperandDefinition> Operands { get; init; }

    public required string ResultUnit { get; init; }

    public required OperationalMetricFormula Formula { get; init; }

    public required OperationalMetricDomainConstraints DomainConstraints { get; init; }

    public required OperationalMetricPrecisionPolicy PrecisionPolicy { get; init; }
}

public interface IOperationalMetricDefinitionCatalog
{
    OperationalMetricDefinition GetRequired(OperationalMetricDefinitionId definitionId);

    IReadOnlyList<OperationalMetricDefinition> GetEvaluationOrder(OperationalMetricEvaluationScope scope);
}
