using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core;

public sealed class ProductionQuantityFactProcessingRuntime
{
    private readonly IProductionQuantityEvidenceReader _reader;
    private readonly IProductionContextProcessingStore _store;
    private readonly ObservationStreamId _streamId;
    private readonly int _batchSize;
    private ObservationProcessingCheckpoint? _checkpoint;
    private bool _checkpointRestored;

    public ProductionQuantityFactProcessingRuntime(
        ObservationProcessorId processorId,
        IProductionQuantityEvidenceReader reader,
        IProductionContextProcessingStore store,
        ObservationStreamId streamId,
        int batchSize)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        if (string.IsNullOrWhiteSpace(processorId.Value))
        {
            throw new ArgumentException("Processor ID is required.", nameof(processorId));
        }

        ProcessorId = processorId;
        _reader = reader;
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
        var evidence = batch.Select(static item => item.Evidence).ToArray();
        var metricFacts = DurableMetricInputFactDeriver.Derive([], evidence);
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
            },
            cancellationToken);

        _checkpoint = nextCheckpoint;
        return batch.Count;
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
