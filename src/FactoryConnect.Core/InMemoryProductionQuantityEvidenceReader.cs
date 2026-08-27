using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class InMemoryProductionQuantityEvidenceReader : IProductionQuantityEvidenceReader
{
    private readonly List<DurableProductionQuantityEvidence> _items = [];

    public void Add(DurableProductionQuantityEvidence item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
    }

    public Task<IReadOnlyList<DurableProductionQuantityEvidence>> ReadAsync(
        ObservationStreamId streamId,
        ObservationPosition? afterPosition,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<DurableProductionQuantityEvidence> result = _items
            .Where(item =>
                item.StreamId == streamId &&
                (afterPosition is null || item.Position > afterPosition))
            .OrderBy(static item => item.Position)
            .Take(batchSize)
            .ToArray();

        return Task.FromResult(result);
    }
}
