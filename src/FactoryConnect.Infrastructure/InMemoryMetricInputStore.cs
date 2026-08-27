using FactoryConnect.Abstractions;

namespace FactoryConnect.Infrastructure;

public sealed class InMemoryMetricInputStore :
    IMetricInputAppender,
    IMetricInputReader
{
    private readonly object _sync = new();
    private readonly Dictionary<MetricInputStreamId, List<PositionedMetricInputFact>> _factsByStream = [];
    private readonly Dictionary<(MetricInputStreamId StreamId, MetricInputFactId FactId), PositionedMetricInputFact> _factsByIdentity = [];

    public ValueTask<PositionedMetricInputFact> AppendAsync(
        DurableMetricInputAppend append,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(append);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var identity = (append.StreamId, append.Fact.Id);

            if (_factsByIdentity.TryGetValue(identity, out var existing))
            {
                if (!IsEquivalent(existing, append))
                {
                    throw new InvalidOperationException(
                        "Metric input fact identity was reused with a conflicting durable payload or temporal ownership.");
                }

                return ValueTask.FromResult(existing);
            }

            if (!_factsByStream.TryGetValue(append.StreamId, out var streamFacts))
            {
                streamFacts = [];
                _factsByStream.Add(append.StreamId, streamFacts);
            }

            var nextValue = streamFacts.Count == 0
                ? 1UL
                : checked(streamFacts[^1].Position.Value + 1UL);
            var positioned = new PositionedMetricInputFact(
                append.StreamId,
                new MetricInputPosition(nextValue),
                append.Fact,
                append.ShiftOccurrenceId,
                append.ProductionDayId);

            streamFacts.Add(positioned);
            _factsByIdentity.Add(identity, positioned);

            return ValueTask.FromResult(positioned);
        }
    }

    public ValueTask<MetricInputReadBatch> ReadAsync(
        MetricInputReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_factsByStream.TryGetValue(request.StreamId, out var streamFacts))
            {
                return ValueTask.FromResult(new MetricInputReadBatch(
                    request.StreamId,
                    request.AfterPosition,
                    request.AfterPosition,
                    []));
            }

            var facts = streamFacts
                .Where(item => request.AfterPosition is null || item.Position > request.AfterPosition)
                .Take(request.MaxCount)
                .ToArray();
            var throughPosition = facts.Length == 0
                ? request.AfterPosition
                : facts[^1].Position;

            return ValueTask.FromResult(new MetricInputReadBatch(
                request.StreamId,
                request.AfterPosition,
                throughPosition,
                facts));
        }
    }

    private static bool IsEquivalent(
        PositionedMetricInputFact existing,
        DurableMetricInputAppend append) =>
        existing.StreamId == append.StreamId &&
        existing.Fact == append.Fact &&
        existing.ShiftOccurrenceId == append.ShiftOccurrenceId &&
        existing.ProductionDayId == append.ProductionDayId;
}
