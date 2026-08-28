using System.Collections.ObjectModel;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class CoherentOperationalMetricEvaluationBatchSource : IOperationalMetricEvaluationBatchSource
{
    private readonly IOperationalMetricDefinitionCatalog _catalog;
    private readonly IMetricAggregationRevisionReader _revisionReader;
    private readonly IRevisionedOperationalMetricComponentSnapshotReader _snapshotReader;
    private readonly MetricAggregationProcessorId _aggregationProcessorId;
    private readonly MetricInputStreamId _sourceStreamId;
    private readonly OperationalMetricEvaluationContextKey _contextKey;
    private readonly OperationalMetricEvaluationPlanner _planner;

    public CoherentOperationalMetricEvaluationBatchSource(
        IOperationalMetricDefinitionCatalog catalog,
        IMetricAggregationRevisionReader revisionReader,
        IRevisionedOperationalMetricComponentSnapshotReader snapshotReader,
        MetricAggregationProcessorId aggregationProcessorId,
        MetricInputStreamId sourceStreamId,
        OperationalMetricEvaluationContextKey contextKey)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(revisionReader);
        ArgumentNullException.ThrowIfNull(snapshotReader);
        ArgumentNullException.ThrowIfNull(aggregationProcessorId);
        ArgumentNullException.ThrowIfNull(sourceStreamId);
        ArgumentNullException.ThrowIfNull(contextKey);
        contextKey.Validate();

        if (contextKey != OperationalMetricEvaluationContextKey.Unpartitioned)
        {
            throw new NotSupportedException(
                "FC-027.4D can evaluate only the unpartitioned FC-026 aggregate grain.");
        }

        _catalog = catalog;
        _revisionReader = revisionReader;
        _snapshotReader = snapshotReader;
        _aggregationProcessorId = aggregationProcessorId;
        _sourceStreamId = sourceStreamId;
        _contextKey = contextKey;
        _planner = new OperationalMetricEvaluationPlanner(catalog);
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

        var change = await _revisionReader.ReadNextAsync(
            _aggregationProcessorId,
            _sourceStreamId,
            request.KnownRevision,
            cancellationToken).ConfigureAwait(false);

        if (change is null && request.KnownRevision is not null)
        {
            change = await _revisionReader.ReadExactAsync(
                request.KnownRevision,
                cancellationToken).ConfigureAwait(false);
        }

        if (change is null)
        {
            return null;
        }

        ValidateRevision(change, request.KnownRevision);

        var evaluations = new List<OperationalMetricEvaluation>();
        foreach (var periodId in GetAffectedPeriods(change))
        {
            var periodEvaluations = await EvaluatePeriodAsync(
                periodId,
                change.Revision,
                cancellationToken).ConfigureAwait(false);
            evaluations.AddRange(periodEvaluations);
        }

        return new OperationalMetricEvaluationBatch(
            change.Revision,
            evaluations);
    }

    private async ValueTask<ReadOnlyCollection<OperationalMetricEvaluation>> EvaluatePeriodAsync(
        OperationalMetricPeriodId periodId,
        MetricAggregationCheckpoint revision,
        CancellationToken cancellationToken)
    {
        var scope = periodId switch
        {
            OperationalMetricPeriodId.Shift => OperationalMetricEvaluationScope.Shift,
            OperationalMetricPeriodId.ProductionDay => OperationalMetricEvaluationScope.ProductionDay,
            _ => throw new InvalidOperationException("Unsupported operational metric period type."),
        };
        var definitions = _catalog.GetEvaluationOrder(scope);
        if (definitions.Count == 0)
        {
            throw new InvalidOperationException(
                "Coherent operational metric evaluation requires at least one definition for the affected period scope.");
        }

        var plans = definitions
            .Select(definition => _planner.CreatePlan(new OperationalMetricEvaluationKey(
                _sourceStreamId.MachineId,
                periodId,
                definition.Id,
                _contextKey)))
            .ToArray();
        var snapshotOperands = CreateSnapshotOperands(plans);
        var anchorKey = plans[0].RootKey;
        var snapshot = await _snapshotReader.ReadAtRevisionAsync(
            new OperationalMetricComponentSnapshotRequest(
                anchorKey,
                _aggregationProcessorId,
                snapshotOperands),
            revision,
            cancellationToken).ConfigureAwait(false);

        if (snapshot.EvaluationKey != anchorKey ||
            snapshot.Revision != revision)
        {
            throw new InvalidDataException(
                "Exact-revision component snapshot does not match the requested period identity and FC-026 revision.");
        }

        var componentsByKey = snapshot.Components.ToDictionary(
            component => component.SourceIdentity.ComponentKey,
            StringComparer.Ordinal);
        var evaluations = new List<OperationalMetricEvaluation>(plans.Length);

        foreach (var plan in plans)
        {
            var components = plan.ComponentRequirements
                .Select(requirement => requirement.ComponentKey)
                .Where(componentsByKey.ContainsKey)
                .Select(componentKey => componentsByKey[componentKey])
                .ToArray();
            var rootSnapshot = new OperationalMetricComponentSnapshot(
                plan.RootKey,
                revision,
                components);
            var session = new OperationalMetricEvaluationSession(plan, rootSnapshot);
            OperationalMetricEvaluator.ValidateSnapshotComponents(session);
            evaluations.Add(OperationalMetricEvaluator.EvaluateDefinition(
                session,
                plan.RootDefinition.Id));
        }

        return new ReadOnlyCollection<OperationalMetricEvaluation>(evaluations);
    }

    private static ReadOnlyCollection<OperationalMetricOperandDefinition> CreateSnapshotOperands(
        IEnumerable<OperationalMetricEvaluationPlan> plans)
    {
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

        var operands = requirements.Values
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

        return new ReadOnlyCollection<OperationalMetricOperandDefinition>(operands);
    }

    private static IEnumerable<OperationalMetricPeriodId> GetAffectedPeriods(
        MetricAggregationRevisionChange change)
    {
        foreach (var shift in change.ShiftOccurrenceIds
                     .OrderBy(value => value.SiteId.Value, StringComparer.Ordinal)
                     .ThenBy(value => value.StartsAtUtc)
                     .ThenBy(value => value.EndsAtUtc)
                     .ThenBy(value => value.ShiftScheduleAssignmentId.Value, StringComparer.Ordinal)
                     .ThenBy(value => value.ShiftId.Value, StringComparer.Ordinal))
        {
            yield return new OperationalMetricPeriodId.Shift(shift);
        }

        foreach (var day in change.ProductionDayIds
                     .OrderBy(value => value.SiteId.Value, StringComparer.Ordinal)
                     .ThenBy(value => value.BusinessDate))
        {
            yield return new OperationalMetricPeriodId.ProductionDay(day);
        }
    }

    private void ValidateRevision(
        MetricAggregationRevisionChange change,
        MetricAggregationCheckpoint? knownRevision)
    {
        if (change.Revision.ProcessorId != _aggregationProcessorId ||
            change.Revision.StreamId != _sourceStreamId)
        {
            throw new InvalidDataException(
                "Aggregation revision change belongs to a different FC-026 processor or stream than the coherent batch source.");
        }

        if (knownRevision is not null &&
            change.Revision.Position < knownRevision.Position)
        {
            throw new InvalidDataException(
                "Aggregation revision change precedes the caller's known durable FC-026 revision.");
        }
    }
}
