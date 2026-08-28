using System.Collections.ObjectModel;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class CoherentOperationalMetricEvaluationBatchSource : IOperationalMetricEvaluationBatchSource
{
    private readonly IOperationalMetricComponentSnapshotReader _snapshotReader;
    private readonly MetricAggregationProcessorId _aggregationProcessorId;
    private readonly MetricInputStreamId _sourceStreamId;
    private readonly ReadOnlyCollection<OperationalMetricEvaluationPlan> _plans;
    private readonly ReadOnlyCollection<OperationalMetricOperandDefinition> _snapshotOperands;
    private readonly OperationalMetricEvaluationKey _anchorKey;

    public CoherentOperationalMetricEvaluationBatchSource(
        IOperationalMetricDefinitionCatalog catalog,
        IOperationalMetricComponentSnapshotReader snapshotReader,
        MetricAggregationProcessorId aggregationProcessorId,
        MetricInputStreamId sourceStreamId,
        OperationalMetricPeriodId periodId,
        OperationalMetricEvaluationContextKey contextKey)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(snapshotReader);
        ArgumentNullException.ThrowIfNull(aggregationProcessorId);
        ArgumentNullException.ThrowIfNull(sourceStreamId);
        ArgumentNullException.ThrowIfNull(periodId);
        ArgumentNullException.ThrowIfNull(contextKey);
        contextKey.Validate();

        if (contextKey != OperationalMetricEvaluationContextKey.Unpartitioned)
        {
            throw new NotSupportedException(
                "FC-027.4D can evaluate only the unpartitioned FC-026 aggregate grain.");
        }

        var scope = periodId switch
        {
            OperationalMetricPeriodId.Shift => OperationalMetricEvaluationScope.Shift,
            OperationalMetricPeriodId.ProductionDay => OperationalMetricEvaluationScope.ProductionDay,
            _ => throw new InvalidOperationException("Unsupported operational metric period type."),
        };
        var definitions = catalog.GetEvaluationOrder(scope);
        if (definitions.Count == 0)
        {
            throw new InvalidOperationException(
                "Coherent operational metric evaluation requires at least one definition for the target scope.");
        }

        var planner = new OperationalMetricEvaluationPlanner(catalog);
        var plans = definitions
            .Select(definition => planner.CreatePlan(new OperationalMetricEvaluationKey(
                sourceStreamId.MachineId,
                periodId,
                definition.Id,
                contextKey)))
            .ToArray();

        var requirements = new Dictionary<string, OperationalMetricComponentRequirement>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            foreach (var requirement in plan.ComponentRequirements)
            {
                if (requirements.TryGetValue(requirement.ComponentKey, out var existing))
                {
                    if (existing != requirement)
                    {
                        throw new InvalidDataException(
                            $"Component '{requirement.ComponentKey}' has incompatible requirements across the metric definition set.");
                    }

                    continue;
                }

                requirements.Add(requirement.ComponentKey, requirement);
            }
        }

        var snapshotOperands = requirements.Values
            .OrderBy(requirement => requirement.ComponentKey, StringComparer.Ordinal)
            .ThenBy(requirement => requirement.RequiredDimension)
            .ThenBy(requirement => requirement.RequiredUnit, StringComparer.Ordinal)
            .Select(requirement => new OperationalMetricOperandDefinition
            {
                OperandName = requirement.ComponentKey,
                Source = new OperationalMetricOperandSource.Component(requirement.ComponentKey),
                RequiredDimension = requirement.RequiredDimension,
                RequiredUnit = requirement.RequiredUnit,
            })
            .ToArray();

        _snapshotReader = snapshotReader;
        _aggregationProcessorId = aggregationProcessorId;
        _sourceStreamId = sourceStreamId;
        _plans = new ReadOnlyCollection<OperationalMetricEvaluationPlan>(plans);
        _snapshotOperands = new ReadOnlyCollection<OperationalMetricOperandDefinition>(snapshotOperands);
        _anchorKey = plans[0].RootKey;
    }

    public async ValueTask<OperationalMetricEvaluationBatch?> ReadAsync(
        OperationalMetricEvaluationBatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.SourceProcessorId != _aggregationProcessorId ||
            request.SourceStreamId != _sourceStreamId)
        {
            throw new InvalidOperationException(
                "Evaluation batch request belongs to a different FC-026 processor or stream than the coherent batch source.");
        }

        var snapshot = await _snapshotReader.ReadAsync(
            new OperationalMetricComponentSnapshotRequest(
                _anchorKey,
                _aggregationProcessorId,
                _snapshotOperands),
            cancellationToken).ConfigureAwait(false);

        if (snapshot.EvaluationKey != _anchorKey ||
            snapshot.Revision.ProcessorId != _aggregationProcessorId ||
            snapshot.Revision.StreamId != _sourceStreamId)
        {
            throw new InvalidDataException(
                "Coherent evaluation snapshot does not match the configured FC-026 source identity.");
        }

        if (request.KnownRevision is not null &&
            snapshot.Revision.Position < request.KnownRevision.Position)
        {
            throw new InvalidDataException(
                "Coherent evaluation snapshot precedes the caller's known durable FC-026 revision.");
        }

        var componentsByKey = snapshot.Components.ToDictionary(
            component => component.SourceIdentity.ComponentKey,
            StringComparer.Ordinal);
        var evaluations = new List<OperationalMetricEvaluation>(_plans.Count);

        foreach (var plan in _plans)
        {
            var components = plan.ComponentRequirements
                .Select(requirement => requirement.ComponentKey)
                .Where(componentsByKey.ContainsKey)
                .Select(componentKey => componentsByKey[componentKey])
                .ToArray();
            var rootSnapshot = new OperationalMetricComponentSnapshot(
                plan.RootKey,
                snapshot.Revision,
                components);
            var session = new OperationalMetricEvaluationSession(plan, rootSnapshot);
            OperationalMetricEvaluator.ValidateSnapshotComponents(session);
            evaluations.Add(OperationalMetricEvaluator.EvaluateDefinition(
                session,
                plan.RootDefinition.Id));
        }

        return new OperationalMetricEvaluationBatch(
            snapshot.Revision,
            evaluations);
    }
}
