using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class InMemoryProductionContextActivityReader : IProductionContextActivityReader
{
    private readonly List<DurableMachineActivityPeriod> _periods = [];

    public InMemoryProductionContextActivityReader(
        IEnumerable<DurableMachineActivityPeriod>? periods = null)
    {
        if (periods is null)
        {
            return;
        }

        _periods.AddRange(periods);
    }

    public void Add(DurableMachineActivityPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);
        _periods.Add(period);
    }

    public Task<IReadOnlyList<DurableMachineActivityPeriod>> ReadAsync(
        ObservationStreamId streamId,
        ObservationPosition? afterPosition,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        IReadOnlyList<DurableMachineActivityPeriod> result = _periods
            .Where(period =>
                period.StreamId == streamId &&
                (afterPosition is null || period.Position > afterPosition))
            .OrderBy(static period => period.Position)
            .Take(batchSize)
            .ToArray();

        return Task.FromResult(result);
    }
}
