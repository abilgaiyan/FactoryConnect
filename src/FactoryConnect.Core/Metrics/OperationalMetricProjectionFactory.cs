using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class OperationalMetricProjectionFactory
{
    private readonly IOperationalMetricDefinitionCatalog _catalog;

    public OperationalMetricProjectionFactory(
        IOperationalMetricDefinitionCatalog catalog,
        OperationalMetricProjectionProcessorId processorId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(processorId);

        _catalog = catalog;
        ProcessorId = processorId;
    }

    public OperationalMetricProjectionProcessorId ProcessorId { get; }

    public OperationalMetricProjection Create(OperationalMetricEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        var definition = _catalog.GetRequired(evaluation.Key.DefinitionId);
        if (!string.Equals(evaluation.Unit, definition.ResultUnit, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Evaluation '{definition.Id.MetricKey}/{definition.Id.Version}' does not match its planned result unit.");
        }

        decimal? durableValue = null;
        if (evaluation.Status == OperationalMetricEvaluationStatus.Calculated)
        {
            if (evaluation.Value is not decimal logicalValue)
            {
                throw new InvalidDataException("Calculated evaluation does not contain a logical value.");
            }

            durableValue = decimal.Round(
                logicalValue,
                definition.PrecisionPolicy.DurableDecimalScale,
                definition.PrecisionPolicy.RoundingMode);
        }

        var operandEvidence = evaluation.OperandEvidence
            .Select(ToDurableComponentEvidence)
            .ToArray();
        var dependencyEvidence = evaluation.DependencyEvidence
            .Select(evidence => new OperationalMetricDependencyProjectionEvidence(
                evidence.OperandName,
                evidence.DefinitionId,
                Create(evidence.Evaluation)))
            .ToArray();

        return new OperationalMetricProjection(
            ProcessorId,
            evaluation.Key,
            evaluation.Status,
            durableValue,
            evaluation.Unit,
            evaluation.ReasonCode,
            evaluation.ReasonOperandName,
            evaluation.SourceRevision,
            operandEvidence,
            dependencyEvidence);
    }

    private static OperationalMetricComponentProjectionEvidence ToDurableComponentEvidence(
        MetricOperandEvidence evidence) => new(
            evidence.OperandName,
            evidence.SourceIdentity,
            evidence.SourceRevision,
            evidence.Dimension,
            evidence.Value,
            evidence.Unit,
            evidence.InputCount,
            evidence.FirstInputTimestamp,
            evidence.LastInputTimestamp);
}
