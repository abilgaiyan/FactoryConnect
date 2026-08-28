using System.Collections.ObjectModel;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class OperationalMetricEvaluator : IOperationalMetricEvaluator
{
    private readonly OperationalMetricEvaluationPlanner _planner;
    private readonly OperationalMetricEvaluationSessionFactory _sessionFactory;

    public OperationalMetricEvaluator(
        IOperationalMetricDefinitionCatalog catalog,
        IOperationalMetricComponentSnapshotReader snapshotReader,
        MetricAggregationProcessorId aggregationProcessorId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(snapshotReader);
        ArgumentNullException.ThrowIfNull(aggregationProcessorId);

        _planner = new OperationalMetricEvaluationPlanner(catalog);
        _sessionFactory = new OperationalMetricEvaluationSessionFactory(snapshotReader, aggregationProcessorId);
    }

    public async ValueTask<OperationalMetricEvaluation> EvaluateAsync(
        OperationalMetricEvaluationKey evaluationKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluationKey);
        cancellationToken.ThrowIfCancellationRequested();

        if (evaluationKey.ContextKey != OperationalMetricEvaluationContextKey.Unpartitioned)
        {
            throw new NotSupportedException(
                "FC-027.3 can evaluate only the unpartitioned FC-026 aggregate grain.");
        }

        var plan = _planner.CreatePlan(evaluationKey);
        var session = await _sessionFactory.CreateAsync(plan, cancellationToken).ConfigureAwait(false);
        ValidateSnapshotComponents(session);

        return EvaluateDefinition(session, plan.RootDefinition);
    }

    internal static OperationalMetricEvaluation EvaluateDefinition(
        OperationalMetricEvaluationSession session,
        OperationalMetricDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(definition);

        if (session.TryGetEvaluation(definition.Id, out var cached))
        {
            return cached!;
        }

        session.BeginEvaluation(definition.Id);
        try
        {
            var evaluation = definition.Formula switch
            {
                OperationalMetricFormula.Ratio ratio => EvaluateRatio(session, definition, ratio),
                OperationalMetricFormula.Product => throw new NotSupportedException(
                    "FC-027.3B evaluates component-backed Ratio definitions. Product dependency composition is introduced in a later FC-027.3 slice."),
                _ => throw new InvalidDataException(
                    $"Metric '{definition.Id}' has an unsupported formula contract."),
            };

            session.CompleteEvaluation(definition.Id, evaluation);
            return evaluation;
        }
        catch
        {
            session.AbandonEvaluation(definition.Id);
            throw;
        }
    }

    private static void ValidateSnapshotComponents(OperationalMetricEvaluationSession session)
    {
        var requirements = session.Plan.ComponentRequirements.ToDictionary(
            requirement => requirement.ComponentKey,
            StringComparer.Ordinal);

        foreach (var component in session.Snapshot.Components)
        {
            if (!requirements.TryGetValue(component.SourceIdentity.ComponentKey, out var requirement))
            {
                throw new InvalidDataException(
                    $"Snapshot returned unexpected component '{component.SourceIdentity.ComponentKey}'.");
            }

            if (!string.Equals(component.OperandName, requirement.ComponentKey, StringComparison.Ordinal) ||
                component.SourceIdentity.ProcessorId != session.Snapshot.Revision.ProcessorId ||
                component.SourceIdentity.MachineId != session.Plan.RootKey.MachineId ||
                component.SourceIdentity.PeriodId != session.Plan.RootKey.PeriodId ||
                component.Dimension != requirement.RequiredDimension ||
                !string.Equals(component.Aggregate.Unit, requirement.RequiredUnit, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Component '{requirement.ComponentKey}' does not match its canonical evaluation-plan requirement.");
            }
        }
    }

    private static OperationalMetricEvaluation EvaluateRatio(
        OperationalMetricEvaluationSession session,
        OperationalMetricDefinition definition,
        OperationalMetricFormula.Ratio ratio)
    {
        if (definition.Operands.Any(operand => operand.Source is not OperationalMetricOperandSource.Component))
        {
            throw new NotSupportedException(
                "FC-027.3B Ratio evaluation requires component-backed operands. Evaluated-metric operands are introduced in FC-027.3C.");
        }

        var evaluationKey = DependencyKey(session.Plan.RootKey, definition.Id);
        var componentsByKey = session.Snapshot.Components.ToDictionary(
            component => component.SourceIdentity.ComponentKey,
            StringComparer.Ordinal);
        var operandsByName = definition.Operands.ToDictionary(
            operand => operand.OperandName,
            StringComparer.Ordinal);

        var numeratorOperand = operandsByName[ratio.NumeratorOperand];
        var denominatorOperand = operandsByName[ratio.DenominatorOperand];
        var numeratorKey = GetComponentKey(numeratorOperand);
        var denominatorKey = GetComponentKey(denominatorOperand);

        if (!componentsByKey.TryGetValue(numeratorKey, out var numerator))
        {
            return MissingOperand(
                evaluationKey,
                definition,
                numeratorOperand,
                session.Snapshot.Revision,
                componentsByKey);
        }

        if (!componentsByKey.TryGetValue(denominatorKey, out var denominator))
        {
            return MissingOperand(
                evaluationKey,
                definition,
                denominatorOperand,
                session.Snapshot.Revision,
                componentsByKey);
        }

        ValidateComponentForOperand(numerator, numeratorOperand, session.Snapshot.Revision);
        ValidateComponentForOperand(denominator, denominatorOperand, session.Snapshot.Revision);

        var evidence = new ReadOnlyCollection<MetricOperandEvidence>(
        [
            ToEvidence(numeratorOperand.OperandName, numerator, session.Snapshot.Revision),
            ToEvidence(denominatorOperand.OperandName, denominator, session.Snapshot.Revision),
        ]);

        if (denominator.Aggregate.Value == 0m)
        {
            return Failure(
                evaluationKey,
                definition.ResultUnit,
                OperationalMetricEvaluationStatus.Unavailable,
                OperationalMetricEvaluationReasonCode.ZeroDenominator,
                denominatorOperand.OperandName,
                session.Snapshot.Revision,
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
            session.Snapshot.Revision,
            evidence);
    }

    private static OperationalMetricEvaluationKey DependencyKey(
        OperationalMetricEvaluationKey rootKey,
        OperationalMetricDefinitionId definitionId) => new(
            rootKey.MachineId,
            rootKey.PeriodId,
            definitionId,
            rootKey.ContextKey);

    private static string GetComponentKey(OperationalMetricOperandDefinition operand) =>
        operand.Source is OperationalMetricOperandSource.Component component
            ? component.ComponentKey
            : throw new InvalidDataException(
                $"Operand '{operand.OperandName}' is not component-backed.");

    private static void ValidateComponentForOperand(
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
                $"Component evidence for operand '{operand.OperandName}' does not match its validated definition contract.");
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
        OperationalMetricOperandDefinition missingOperand,
        MetricAggregationCheckpoint revision,
        IReadOnlyDictionary<string, OperationalMetricComponent> availableComponents)
    {
        var componentKey = GetComponentKey(missingOperand);
        var reasonCode = string.Equals(componentKey, MetricInputKeys.ProductionReferenceTime, StringComparison.Ordinal)
            ? OperationalMetricEvaluationReasonCode.MissingReferenceTime
            : OperationalMetricEvaluationReasonCode.MissingOperand;

        var evidence = definition.Operands
            .Where(operand => operand.Source is OperationalMetricOperandSource.Component)
            .Select(operand => (Operand: operand, ComponentKey: GetComponentKey(operand)))
            .Where(candidate => availableComponents.ContainsKey(candidate.ComponentKey))
            .Select(candidate => ToEvidence(
                candidate.Operand.OperandName,
                availableComponents[candidate.ComponentKey],
                revision));

        return Failure(
            evaluationKey,
            definition.ResultUnit,
            OperationalMetricEvaluationStatus.InsufficientEvidence,
            reasonCode,
            missingOperand.OperandName,
            revision,
            evidence);
    }

    private static MetricOperandEvidence ToEvidence(
        string operandName,
        OperationalMetricComponent component,
        MetricAggregationCheckpoint revision) => new(
            operandName,
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
