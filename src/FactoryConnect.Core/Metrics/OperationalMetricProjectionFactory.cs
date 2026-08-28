using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class OperationalMetricProjectionFactory
{
    private readonly IOperationalMetricDefinitionCatalog _catalog;
    private readonly OperationalMetricProjectionProcessorId _processorId;

    public OperationalMetricProjectionFactory(
        IOperationalMetricDefinitionCatalog catalog,
        OperationalMetricProjectionProcessorId processorId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(processorId);

        _catalog = catalog;
        _processorId = processorId;
    }

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

        return new OperationalMetricProjection(
            _processorId,
            evaluation.Key,
            evaluation.Status,
            durableValue,
            evaluation.Unit,
            evaluation.ReasonCode,
            evaluation.ReasonOperandName,
            evaluation.SourceRevision);
    }
}
