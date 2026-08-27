using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;

namespace FactoryConnect.Edge;

public sealed class ProjectionProductionContextActivityReader(
    InMemoryMachineStateActivityProjectionStore projectionStore)
    : IProductionContextActivityReader
{
    private static readonly ObservationProcessorId SourceProcessorId =
        new("machine-state-activity");

    public Task<IReadOnlyList<DurableMachineActivityPeriod>> ReadAsync(
        ObservationStreamId streamId,
        ObservationPosition? afterPosition,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<DurableMachineActivityPeriod> result = projectionStore
            .ReadActivityPeriods(SourceProcessorId, streamId)
            .Where(item => afterPosition is null || item.Position > afterPosition)
            .OrderBy(static item => item.Position)
            .Take(batchSize)
            .ToArray();

        return Task.FromResult(result);
    }
}
