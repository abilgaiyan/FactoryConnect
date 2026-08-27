using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core;

public sealed class ProductionQuantityFactProcessingRuntime
{
    private readonly IProductionQuantityEvidenceReader _reader;
    private readonly ShiftOccurrenceResolver _shiftResolver;
    private readonly IProductionContextProcessingStore _store;
    private readonly ObservationStreamId _streamId;
    private readonly int _batchSize;
    private ObservationProcessingCheckpoint? _checkpoint;
    private bool _checkpointRestored;

    public ProductionQuantityFactProcessingRuntime(
        ObservationProcessorId processorId,
        IProductionQuantityEvidenceReader reader,
        ShiftOccurrenceResolver shiftResolver,
        IProductionContextProcessingStore store,
        ObservationStreamId streamId,
        int batchSize)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(shiftResolver);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        if (string.IsNullOrWhiteSpace(processorId.Value))
        {
            throw new ArgumentException("Processor ID is required.", nameof(processorId));
        }

        ProcessorId = processorId;
        _reader = reader;
        _shiftResolver = shiftResolver;
        _store = store;
        _streamId = streamId;
        _batchSize = batchSize;
    }

    public ObservationProcessorId ProcessorId { get; }

    public async Task<int> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        await RestoreCheckpointAsync(cancellationToken);

        var batch = await _reader.ReadAsync(
            _streamId,
            _checkpoint?.Position,
            _batchSize,
            cancellationToken);

        if (batch.Count == 0)
        {
            return 0;
        }

        ValidateBatch(batch);

        var metricFacts = new List<DurableMetricInputFact>();
        var metricInputs = new List<DurableMetricInputAppend>();
        var metricInputStreamId = MetricInputStreamId.ForMachine(_streamId.MachineId);

        foreach (var item in batch)
        {
            var evidence = item.Evidence;
            var occurrence = await ResolveOccurrenceAsync(evidence, cancellationToken);
            var derived = DurableMetricInputFactDeriver
                .Derive([], [evidence])
                .Select(fact => fact with
                {
                    ShiftScheduleAssignmentId = occurrence.SourceAssignmentId,
                })
                .ToArray();

            metricFacts.AddRange(derived);
            metricInputs.AddRange(MetricInputAppendFactory.Create(
                metricInputStreamId,
                derived,
                [occurrence]));
        }

        var nextCheckpoint = new ObservationProcessingCheckpoint(
            ProcessorId,
            _streamId,
            batch[^1].Position);

        await _store.CommitAsync(
            new ProductionContextProcessingCommit
            {
                ExpectedCheckpoint = _checkpoint,
                NextCheckpoint = nextCheckpoint,
                ContextualizedActivity = [],
                EligibilityIntervals = [],
                MetricFacts = metricFacts,
                MetricInputs = metricInputs,
            },
            cancellationToken);

        _checkpoint = nextCheckpoint;
        return batch.Count;
    }

    private async Task<ShiftOccurrence> ResolveOccurrenceAsync(
        ProductionQuantityEvidence evidence,
        CancellationToken cancellationToken)
    {
        var occurrenceDate = DateOnly.FromDateTime(evidence.OccurredAtUtc.UtcDateTime);
        var from = occurrenceDate.AddDays(-1);
        var to = occurrenceDate.AddDays(2);
        IReadOnlyList<ShiftOccurrence> occurrences;

        if (evidence.ProductionLineId is { } productionLineId)
        {
            occurrences = await _shiftResolver.ResolveAsync(
                evidence.SiteId,
                productionLineId,
                from,
                to,
                cancellationToken);
        }
        else
        {
            occurrences = await _shiftResolver.ResolveAsync(
                evidence.SiteId,
                from,
                to,
                cancellationToken);
        }

        var matches = occurrences
            .Where(occurrence =>
                occurrence.ShiftId == evidence.ShiftId &&
                evidence.OccurredAtUtc >= occurrence.StartsAtUtc &&
                evidence.OccurredAtUtc < occurrence.EndsAtUtc)
            .ToArray();

        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                "Production quantity evidence must resolve to exactly one shift occurrence.");
        }

        return matches[0];
    }

    private async ValueTask RestoreCheckpointAsync(CancellationToken cancellationToken)
    {
        if (_checkpointRestored)
        {
            return;
        }

        _checkpoint = await _store.ReadCheckpointAsync(
            ProcessorId,
            _streamId,
            cancellationToken);
        _checkpointRestored = true;
    }

    private void ValidateBatch(IReadOnlyList<DurableProductionQuantityEvidence> batch)
    {
        ObservationPosition? previous = null;
        foreach (var item in batch)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.StreamId != _streamId)
            {
                throw new InvalidOperationException("Durable quantity evidence batch contains an item outside the configured stream.");
            }

            if (previous is not null && item.Position <= previous)
            {
                throw new InvalidOperationException("Durable quantity evidence positions must be strictly increasing.");
            }

            previous = item.Position;
        }
    }
}
