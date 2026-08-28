using System.Collections.ObjectModel;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class OperationalMetricEvaluator : IOperationalMetricEvaluator
{
    private readonly IOperationalMetricDefinitionCatalog _catalog;
    private readonly IOperationalMetricComponentSnapshotReader _snapshotReader;
    private readonly MetricAggregationProcessorId _aggregationProcessorId;

    public OperationalMetricEvaluator(
        IOperationalMetricDefinitionCatalog catalog,
        IOperationalMetricComponentSnapshotReader snapshotReader,
        MetricAggregationProcessorId aggregationProcessorId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(snapshotReader);
        ArgumentNullException.ThrowIfNull(aggregationProcessorId);

        _catalog = catalog;
        _snapshotReader = snapshotReader;
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
            throw new InvalidOperationException(
                $"Metric definition '{definition.Id}' does not support evaluation scope '{evaluationKey.Scope}'.");
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

        if (definition.Operands.Any(operand => operand.Source is not OperationalMetricOperandSource.Component))
        {
            throw new NotSupportedException(
                "FC-027.2 Ratio operands must bind directly to FC-026 components.");
        }

        var snapshot = await _snapshotReader.ReadAsync(
            new OperationalMetricComponentSnapshotRequest(
                evaluationKey,
                _aggregationProcessorId,
                definition.Operands),
            cancellationToken).ConfigureAwait(false);

        ValidateSnapshotIdentity(evaluationKey, snapshot);
        ValidateAllComponents(definition, snapshot);

        return EvaluateRatio(evaluationKey, definition, ratio, snapshot);
    }

    private void ValidateSnapshotIdentity(
        OperationalMetricEvaluationKey evaluationKey,
        OperationalMetricComponentSnapshot snapshot)
    {
        if (snapshot.EvaluationKey != evaluationKey ||
            snapshot.Revision.ProcessorId != _aggregationProcessorId ||
            snapshot.Revision.StreamId.MachineId != evaluationKey.MachineId)
        {
            throw new InvalidDataException(
                "Operational metric component snapshot does not match the requested evaluation identity.");
        }
    }

    private static void ValidateAllComponents(
        OperationalMetricDefinition definition,
        OperationalMetricComponentSnapshot snapshot)
    {
        var operandsByName = definition.Operands.ToDictionary(
            operand => operand.OperandName,
            StringComparer.Ordinal);

        foreach (var component in snapshot.Components)
        {
            if (!operandsByName.TryGetValue(component.OperandName, out var operand))
            {
                throw new InvalidDataException(
                    $"Snapshot returned unexpected operand '{component.OperandName}'.");
            }

            ValidateComponent(component, operand, snapshot.Revision);
        }
    }

    private static OperationalMetricEvaluation EvaluateRatio(
        OperationalMetricEvaluationKey evaluationKey,
        OperationalMetricDefinition definition,
        OperationalMetricFormula.Ratio ratio,
        OperationalMetricComponentSnapshot snapshot)
    {
        var byOperand = snapshot.Components.ToDictionary(
            component => component.OperandName,
            StringComparer.Ordinal);

        if (!byOperand.TryGetValue(ratio.NumeratorOperand, out var numerator))
        {
            return MissingOperand(
                evaluationKey,
                definition,
                ratio.NumeratorOperand,
                snapshot.Revision,
                byOperand.Values);
        }

        if (!byOperand.TryGetValue(ratio.DenominatorOperand, out var denominator))
        {
            return MissingOperand(
                evaluationKey,
                definition,
                ratio.DenominatorOperand,
                snapshot.Revision,
                byOperand.Values);
        }

        var evidence = new ReadOnlyCollection<MetricOperandEvidence>(
        [
            ToEvidence(numerator, snapshot.Revision),
            ToEvidence(denominator, snapshot.Revision),
        ]);

        if (denominator.Aggregate.Value == 0m)
        {
            return Failure(
                evaluationKey,
                definition.ResultUnit,
                OperationalMetricEvaluationStatus.Unavailable,
                OperationalMetricEvaluationReasonCode.ZeroDenominator,
                denominator.OperandName,
                snapshot.Revision,
                evidence);
        }

        var logicalValue = numerator.Aggregate.Value / denominator.Aggregate.Value;
        ValidateDomain(definition, logicalValue);

        return new OperationalMetricEvaluation(
            evaluationKey,
            OperationalMetricEvaluationStatus.Calculated,
            logicalValue,
            definition.ResultUnit,
            null,
            null,
            snapshot.Revision,
            evidence);
    }

    private static void ValidateComponent(
        OperationalMetricComponent component,
        OperationalMetricOperandDefinition operand,
        MetricAggregationCheckpoint revision)
    {
        if (operand.Source is not OperationalMetricOperandSource.Component source ||
            !string.Equals(source.ComponentKey, component.SourceIdentity.ComponentKey, StringComparison.Ordinal) ||
            component.SourceIdentity.ProcessorId != revision.ProcessorId ||
            component.SourceIdentity.MachineId != revision.StreamId.MachineId ||
            component.Dimension != operand.RequiredDimension ||
            !string.Equals(component.Aggregate.Unit, operand.RequiredUnit, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Component evidence for operand '{component.OperandName}' does not match its validated definition contract.");
        }
    }

    private static void ValidateDomain(
        OperationalMetricDefinition definition,
        decimal value)
    {
        if (definition.DomainConstraints.MinimumInclusive is decimal minimum && value < minimum ||
            definition.DomainConstraints.MaximumInclusive is decimal maximum && value > maximum)
        {
            throw new InvalidDataException(
                $"Metric '{definition.Id}' produced value '{value}' outside its validated domain constraints.");
        }
    }

    private static OperationalMetricEvaluation MissingOperand(
        OperationalMetricEvaluationKey evaluationKey,
        OperationalMetricDefinition definition,
        string operandName,
        MetricAggregationCheckpoint revision,
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
            revision,
            availableComponents.Select(component => ToEvidence(component, revision)));
    }

    private static MetricOperandEvidence ToEvidence(
        OperationalMetricComponent component,
        MetricAggregationCheckpoint revision) => new(
            component.OperandName,
            component.SourceIdentity,
            revision,
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
        MetricAggregationCheckpoint revision,
        IEnumerable<MetricOperandEvidence> evidence) => new(
            evaluationKey,
            status,
            null,
            unit,
            reasonCode,
            reasonOperandName,
            revision,
            evidence);
}
