using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class InMemoryProductionContextProcessingStore : IProductionContextProcessingStore
{
    private readonly Dictionary<(string ProcessorId, ObservationStreamId StreamId), ObservationProcessingCheckpoint> _checkpoints = [];
    private readonly Dictionary<ContextualizedActivityIntervalId, ContextualizedActivityInterval> _contextualized = [];
    private readonly Dictionary<ProductionTimeEligibilityIntervalId, ProductionTimeEligibilityInterval> _eligibility = [];
    private readonly Dictionary<MetricInputFactId, DurableMetricInputFact> _metricFacts = [];

    public IReadOnlyCollection<ContextualizedActivityInterval> ContextualizedActivity => _contextualized.Values;
    public IReadOnlyCollection<ProductionTimeEligibilityInterval> EligibilityIntervals => _eligibility.Values;
    public IReadOnlyCollection<DurableMetricInputFact> MetricFacts => _metricFacts.Values;

    public Task<ObservationProcessingCheckpoint?> ReadCheckpointAsync(
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(streamId);
        cancellationToken.ThrowIfCancellationRequested();

        _checkpoints.TryGetValue((processorId.Value, streamId), out var checkpoint);
        return Task.FromResult(checkpoint);
    }

    public Task CommitAsync(
        ProductionContextProcessingCommit commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(commit.NextCheckpoint);

        var next = commit.NextCheckpoint;
        var key = (next.ProcessorId.Value, next.StreamId);
        _checkpoints.TryGetValue(key, out var current);

        if (!CheckpointEquals(current, commit.ExpectedCheckpoint))
        {
            throw new InvalidOperationException("Production context processing checkpoint conflict.");
        }

        if (current is not null && next.Position <= current.Position)
        {
            throw new InvalidOperationException("Production context processing checkpoint must advance.");
        }

        ValidateUnique(commit.ContextualizedActivity.Select(static item => item.Id), "contextualized activity");
        ValidateUnique(commit.EligibilityIntervals.Select(static item => item.Id), "eligibility interval");
        ValidateUnique(commit.MetricFacts.Select(static item => item.Id), "metric fact");

        foreach (var item in commit.ContextualizedActivity)
        {
            ArgumentNullException.ThrowIfNull(item);
            ValidateReplay(_contextualized, item.Id, item, "contextualized activity");
        }

        foreach (var item in commit.EligibilityIntervals)
        {
            ArgumentNullException.ThrowIfNull(item);
            ValidateReplay(_eligibility, item.Id, item, "eligibility interval");
        }

        foreach (var item in commit.MetricFacts)
        {
            ArgumentNullException.ThrowIfNull(item);
            ValidateReplay(_metricFacts, item.Id, item, "metric fact");
        }

        foreach (var item in commit.ContextualizedActivity)
        {
            _contextualized.TryAdd(item.Id, item);
        }

        foreach (var item in commit.EligibilityIntervals)
        {
            _eligibility.TryAdd(item.Id, item);
        }

        foreach (var item in commit.MetricFacts)
        {
            _metricFacts.TryAdd(item.Id, item);
        }

        _checkpoints[key] = next;
        return Task.CompletedTask;
    }

    private static bool CheckpointEquals(
        ObservationProcessingCheckpoint? left,
        ObservationProcessingCheckpoint? right) =>
        left is null
            ? right is null
            : right is not null &&
              left.ProcessorId == right.ProcessorId &&
              left.StreamId == right.StreamId &&
              left.Position == right.Position;

    private static void ValidateReplay<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> existing,
        TKey id,
        TValue value,
        string kind)
        where TKey : notnull
        where TValue : notnull
    {
        if (existing.TryGetValue(id, out var persisted) && !EqualityComparer<TValue>.Default.Equals(persisted, value))
        {
            throw new InvalidOperationException(
                $"Production context processing {kind} identity collides with different content.");
        }
    }

    private static void ValidateUnique<T>(IEnumerable<T> ids, string kind)
        where T : notnull
    {
        var seen = new HashSet<T>();
        foreach (var id in ids)
        {
            if (!seen.Add(id))
            {
                throw new InvalidOperationException($"Production context processing commit contains duplicate {kind} identity.");
            }
        }
    }
}
