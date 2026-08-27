using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class InMemoryProductionContextProcessingStore :
    IProductionContextProcessingStore,
    IMetricInputReader
{
    private readonly Dictionary<(string ProcessorId, ObservationStreamId StreamId), ObservationProcessingCheckpoint> _checkpoints = [];
    private readonly Dictionary<ContextualizedActivityIntervalId, ContextualizedActivityInterval> _contextualized = [];
    private readonly Dictionary<ProductionTimeEligibilityIntervalId, ProductionTimeEligibilityInterval> _eligibility = [];
    private readonly Dictionary<MetricInputFactId, DurableMetricInputFact> _metricFacts = [];
    private readonly Dictionary<MetricInputStreamId, List<PositionedMetricInputFact>> _metricInputStreams = [];
    private readonly Dictionary<MetricInputFactId, PositionedMetricInputFact> _positionedByFactId = [];

    public IReadOnlyCollection<ContextualizedActivityInterval> ContextualizedActivity => _contextualized.Values;
    public IReadOnlyCollection<ProductionTimeEligibilityInterval> EligibilityIntervals => _eligibility.Values;
    public IReadOnlyCollection<DurableMetricInputFact> MetricFacts => _metricFacts.Values;
    public IReadOnlyCollection<PositionedMetricInputFact> PositionedMetricInputs => _positionedByFactId.Values;

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

    public ValueTask<MetricInputReadBatch> ReadAsync(
        MetricInputReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_metricInputStreams.TryGetValue(request.StreamId, out var stream) || stream.Count == 0)
        {
            if (request.AfterPosition is not null)
            {
                throw new InvalidOperationException(
                    "Metric input read position is beyond the durable stream tail.");
            }

            return ValueTask.FromResult(new MetricInputReadBatch(
                request.StreamId,
                null,
                null,
                []));
        }

        var tail = stream[^1].Position;
        if (request.AfterPosition is not null && request.AfterPosition > tail)
        {
            throw new InvalidOperationException(
                "Metric input read position is beyond the durable stream tail.");
        }

        var facts = stream
            .Where(item => request.AfterPosition is null || item.Position > request.AfterPosition)
            .Take(request.MaxCount)
            .ToArray();

        var through = facts.Length == 0
            ? request.AfterPosition
            : facts[^1].Position;

        return ValueTask.FromResult(new MetricInputReadBatch(
            request.StreamId,
            request.AfterPosition,
            through,
            facts));
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
        ValidateUnique(commit.MetricInputs.Select(static item => item.Fact.Id), "metric input");

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

        var stagedInputs = StageMetricInputs(commit.MetricInputs);

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

        foreach (var item in stagedInputs)
        {
            if (_positionedByFactId.ContainsKey(item.Fact.Id))
            {
                continue;
            }

            if (!_metricInputStreams.TryGetValue(item.StreamId, out var stream))
            {
                stream = [];
                _metricInputStreams.Add(item.StreamId, stream);
            }

            stream.Add(item);
            _positionedByFactId.Add(item.Fact.Id, item);
        }

        _checkpoints[key] = next;
        return Task.CompletedTask;
    }

    private IReadOnlyList<PositionedMetricInputFact> StageMetricInputs(
        IReadOnlyList<DurableMetricInputAppend> appends)
    {
        var staged = new List<PositionedMetricInputFact>(appends.Count);
        var nextPositions = _metricInputStreams.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Count == 0 ? 1UL : pair.Value[^1].Position.Value + 1UL);

        foreach (var append in appends)
        {
            ArgumentNullException.ThrowIfNull(append);

            if (_positionedByFactId.TryGetValue(append.Fact.Id, out var persisted))
            {
                var replay = new PositionedMetricInputFact(
                    append.StreamId,
                    persisted.Position,
                    append.Fact,
                    append.ShiftOccurrenceId,
                    append.ProductionDayId);

                if (persisted != replay)
                {
                    throw new InvalidOperationException(
                        "Production context processing metric input identity collides with different content.");
                }

                staged.Add(persisted);
                continue;
            }

            if (!nextPositions.TryGetValue(append.StreamId, out var nextPosition))
            {
                nextPosition = 1UL;
            }

            var positioned = new PositionedMetricInputFact(
                append.StreamId,
                new MetricInputPosition(nextPosition),
                append.Fact,
                append.ShiftOccurrenceId,
                append.ProductionDayId);

            staged.Add(positioned);
            nextPositions[append.StreamId] = checked(nextPosition + 1UL);
        }

        return staged;
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
