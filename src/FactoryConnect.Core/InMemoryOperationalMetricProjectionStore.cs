using System.Collections.ObjectModel;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class InMemoryOperationalMetricProjectionStore :
    IOperationalMetricProjectionStore,
    IOperationalMetricProjectionQueryReader,
    IOperationalMetricReportingQueryProvider
{
    private readonly object _sync = new();
    private readonly Dictionary<OperationalMetricProjectionProcessorId, OperationalMetricProjectionCheckpoint> _checkpoints = [];
    private readonly Dictionary<(OperationalMetricProjectionProcessorId ProcessorId, OperationalMetricEvaluationKey Key), OperationalMetricProjection> _projections = [];

    public ValueTask<OperationalMetricProjectionCheckpoint?> ReadCheckpointAsync(
        OperationalMetricProjectionProcessorId processorId,
        MetricInputStreamId sourceStreamId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(sourceStreamId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_checkpoints.TryGetValue(processorId, out var checkpoint))
            {
                if (checkpoint.SourceRevision.StreamId != sourceStreamId)
                {
                    throw new InvalidOperationException(
                        "Operational metric projection processor checkpoint belongs to a different FC-026 source stream.");
                }

                return ValueTask.FromResult<OperationalMetricProjectionCheckpoint?>(checkpoint);
            }

            return ValueTask.FromResult<OperationalMetricProjectionCheckpoint?>(null);
        }
    }

    public ValueTask<OperationalMetricProjection?> ReadProjectionAsync(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            _projections.TryGetValue((processorId, key), out var projection);
            return ValueTask.FromResult<OperationalMetricProjection?>(projection);
        }
    }

    public ValueTask<IReadOnlyList<OperationalMetricProjectionSummary>> ReadPeriodSummariesAsync(
        OperationalMetricProjectionProcessorId processorId,
        MachineId machineId,
        OperationalMetricPeriodId periodId,
        OperationalMetricEvaluationContextKey contextKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        if (machineId.IsEmpty)
        {
            throw new ArgumentException("Machine ID is required.", nameof(machineId));
        }

        ArgumentNullException.ThrowIfNull(periodId);
        ArgumentNullException.ThrowIfNull(contextKey);
        contextKey.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var snapshot = _projections
                .Where(pair =>
                    pair.Key.ProcessorId == processorId &&
                    pair.Key.Key.MachineId == machineId &&
                    pair.Key.Key.PeriodId == periodId &&
                    pair.Key.Key.ContextKey == contextKey)
                .Select(static pair => new OperationalMetricProjectionSummary(pair.Value))
                .OrderBy(static summary => summary.Key.DefinitionId.MetricKey, StringComparer.Ordinal)
                .ThenBy(static summary => summary.Key.DefinitionId.Version, StringComparer.Ordinal)
                .ToArray();

            return ValueTask.FromResult<IReadOnlyList<OperationalMetricProjectionSummary>>(
                new ReadOnlyCollection<OperationalMetricProjectionSummary>(snapshot));
        }
    }

    public ValueTask<OperationalMetricProjection?> ReadDetailAsync(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey key,
        CancellationToken cancellationToken) =>
        ReadProjectionAsync(processorId, key, cancellationToken);

    public ValueTask<IReadOnlyList<OperationalMetricProjectionSummary>> ReadWindowAsync(
        OperationalMetricReportQuery query,
        OperationalMetricEvaluationKey? startAfter,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        cancellationToken.ThrowIfCancellationRequested();

        var comparer = OperationalMetricReportOrdering.GetEvaluationKeyComparer(query.Order);

        lock (_sync)
        {
            var snapshot = _projections.Values
                .Select(static projection => new OperationalMetricProjectionSummary(projection))
                .Where(summary => OperationalMetricReportQuerySemantics.Matches(query, summary))
                .Where(summary => startAfter is null || comparer.Compare(summary.Key, startAfter) > 0)
                .OrderBy(static summary => summary.Key, comparer)
                .Take(maximumCount)
                .ToArray();

            return ValueTask.FromResult<IReadOnlyList<OperationalMetricProjectionSummary>>(
                new ReadOnlyCollection<OperationalMetricProjectionSummary>(snapshot));
        }
    }

    public ValueTask CommitAsync(
        OperationalMetricProjectionCommit commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            _checkpoints.TryGetValue(commit.ProcessorId, out var current);
            if (current != commit.ExpectedCheckpoint)
            {
                throw new InvalidOperationException("Operational metric projection checkpoint conflict.");
            }

            if (current is not null &&
                (current.SourceRevision.ProcessorId != commit.ProposedCheckpoint.SourceRevision.ProcessorId ||
                 current.SourceRevision.StreamId != commit.ProposedCheckpoint.SourceRevision.StreamId))
            {
                throw new InvalidOperationException(
                    "Operational metric projection processor cannot change its FC-026 source processor or stream.");
            }

            var staged = commit.Projections.ToDictionary(
                projection => (commit.ProcessorId, projection.Key),
                projection => projection);

            foreach (var pair in staged)
            {
                _projections[pair.Key] = pair.Value;
            }

            _checkpoints[commit.ProcessorId] = commit.ProposedCheckpoint;
            return ValueTask.CompletedTask;
        }
    }
}
