using System.Collections.ObjectModel;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class OperationalMetricEvaluator : IOperationalMetricEvaluator
{
    private readonly IOperationalMetricDefinitionCatalog _catalog;
    private readonly IMetricAggregationStore _aggregationStore;
    private readonly MetricAggregationProcessorId _aggregationProcessorId;

    public OperationalMetricEvaluator(
        IOperationalMetricDefinitionCatalog catalog,
        IMetricAggregationStore aggregationStore,
        MetricAggregationProcessorId aggregationProcessorId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(aggregationStore);
        ArgumentNullException.ThrowIfNull(aggregationProcessorId);

        _catalog = catalog;
        _aggregationStore = aggregationStore;
        _aggregationProcessorId = aggregationProcessorId;
    }

    public async ValueTask<OperationalMetricEvaluation> EvaluateAsync(
        OperationalMetricEvaluationKey evaluationKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluationKey);

        var definition = _catalog.GetRequired(evaluationKey.DefinitionId);
        if (!definition.SupportedScopes.Supports(evaluationKey.Scope))
        {
            return Failure(
                evaluationKey,
                definition.ResultUnit,
                OperationalMetricEvaluationStatus.Unavailable,
                OperationalMetricEvaluationReasonCode.UnsupportedScope,
                null,
                []);
        }

        if (evaluationKey.ContextKey != OperationalMetricEvaluationContextKey.Unpartitioned)
        {
            throw new NotSupportedException(
                "FC-027.2 can evaluate only the unpartitioned FC-026 aggregate grain.");
        }

        if (definition.Formula is not OperationalMetricFormula.Ratio ratio)
        {
            throw new NotSupportedException(
                "FC-027.2 supports only component-based Ratio formulas. Dependent metric formulas are introduced in FC-027.3.");
        }

        var components = await ReadComponentsAsync(
            evaluationKey,
            definition,
            cancellationToken).ConfigureAwait(false);

        return EvaluateRatio(evaluationKey, definition, ratio, components);
    }

    private async ValueTask<OperationalMetricComponentSet> ReadComponentsAsync(
        OperationalMetricEvaluationKey evaluationKey,
        OperationalMetricDefinition definition,
        CancellationToken cancellationToken)
    {
        var components = new List<OperationalMetricComponent>(definition.Operands.Count);

        foreach (var operand in definition.Operands)
        {
            if (operand.Source is not OperationalMetricOperandSource.Component source)
            {
                throw new NotSupportedException(
                    "FC-027.2 Ratio operands must bind directly to FC-026 components.");
            }

            MetricAggregateValue? aggregate = evaluationKey.PeriodId switch
            {
                OperationalMetricPeriodId.Shift shift =>
                    await _aggregationStore.ReadShiftAggregateAsync(
                        _aggregationProcessorId,
                        new ShiftMetricAggregateKey(
                            evaluationKey.MachineId,
                            shift.ShiftOccurrenceId,
                            source.ComponentKey),
                        cancellationToken).ConfigureAwait(false),
                OperationalMetricPeriodId.ProductionDay productionDay =>
                    await _aggregationStore.ReadProductionDayAggregateAsync(
                        _aggregationProcessorId,
                        new ProductionDayMetricAggregateKey(
                            evaluationKey.MachineId,
                            productionDay.ProductionDayId,
                            source.ComponentKey),
                        cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unsupported operational metric period type."),
            };

            if (aggregate is null)
            {
                continue;
            }

            if (!string.Equals(aggregate.Unit, operand.RequiredUnit, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Component '{source.ComponentKey}' has unit '{aggregate.Unit}', expected '{operand.RequiredUnit}'.");
            }

            components.Add(new OperationalMetricComponent(
                operand.OperandName,
                source.ComponentKey,
                operand.RequiredDimension,
                aggregate));
        }

        return new OperationalMetricComponentSet(evaluationKey, components);
    }

    private static OperationalMetricEvaluation EvaluateRatio(
        OperationalMetricEvaluationKey evaluationKey,
        OperationalMetricDefinition definition,
        OperationalMetricFormula.Ratio ratio,
        OperationalMetricComponentSet components)
    {
        var byOperand = components.Components.ToDictionary(
            component => component.OperandName,
            StringComparer.Ordinal);

        if (!byOperand.TryGetValue(ratio.NumeratorOperand, out var numerator))
        {
            return MissingOperand(evaluationKey, definition, ratio.NumeratorOperand, byOperand.Values);
        }

        if (!byOperand.TryGetValue(ratio.DenominatorOperand, out var denominator))
        {
            return MissingOperand(evaluationKey, definition, ratio.DenominatorOperand, byOperand.Values);
        }

        var evidence = new ReadOnlyCollection<MetricOperandEvidence>(
        [
            ToEvidence(numerator),
            ToEvidence(denominator),
        ]);

        if (denominator.Aggregate.Value == 0m)
        {
            return Failure(
                evaluationKey,
                definition.ResultUnit,
                OperationalMetricEvaluationStatus.Unavailable,
                OperationalMetricEvaluationReasonCode.ZeroDenominator,
                denominator.OperandName,
                evidence);
        }

        var rawValue = numerator.Aggregate.Value / denominator.Aggregate.Value;
        if (definition.DomainConstraints.MinimumInclusive is decimal minimum && rawValue < minimum ||
            definition.DomainConstraints.MaximumInclusive is decimal maximum && rawValue > maximum)
        {
            return Failure(
                evaluationKey,
                definition.ResultUnit,
                OperationalMetricEvaluationStatus.Unavailable,
                OperationalMetricEvaluationReasonCode.DomainViolation,
                null,
                evidence);
        }

        var value = Math.Round(
            rawValue,
            definition.PrecisionPolicy.DurableDecimalScale,
            definition.PrecisionPolicy.RoundingMode);

        return new OperationalMetricEvaluation(
            evaluationKey,
            OperationalMetricEvaluationStatus.Calculated,
            value,
            definition.ResultUnit,
            null,
            null,
            evidence);
    }

    private static OperationalMetricEvaluation MissingOperand(
        OperationalMetricEvaluationKey evaluationKey,
        OperationalMetricDefinition definition,
        string operandName,
        IEnumerable<OperationalMetricComponent> availableComponents)
    {
        var operand = definition.Operands.Single(candidate =>
            string.Equals(candidate.OperandName, operandName, StringComparison.Ordinal));
        var reasonCode = operand.Source is OperationalMetricOperandSource.Component component &&
            string.Equals(component.ComponentKey, MetricInputKeys.ProductionReferenceTime, StringComparison.Ordinal)
                ? OperationalMetricEvaluationReasonCode.MissingReferenceTime
                : OperationalMetricEvaluationReasonCode.MissingOperand;

        return Failure(
            evaluationKey,
            definition.ResultUnit,
            OperationalMetricEvaluationStatus.InsufficientEvidence,
            reasonCode,
            operandName,
            availableComponents.Select(ToEvidence));
    }

    private static MetricOperandEvidence ToEvidence(OperationalMetricComponent component) => new(
        component.OperandName,
        component.ComponentKey,
        component.Dimension,
        component.Aggregate.Value,
        component.Aggregate.Unit,
        component.Aggregate.InputCount,
        component.Aggregate.FirstInputTimestamp,
        component.Aggregate.LastInputTimestamp);

    private static OperationalMetricEvaluation Failure(
        OperationalMetricEvaluationKey evaluationKey,
        string unit,
        OperationalMetricEvaluationStatus status,
        OperationalMetricEvaluationReasonCode reasonCode,
        string? reasonOperandName,
        IEnumerable<MetricOperandEvidence> evidence) => new(
            evaluationKey,
            status,
            null,
            unit,
            reasonCode,
            reasonOperandName,
            evidence);
}
