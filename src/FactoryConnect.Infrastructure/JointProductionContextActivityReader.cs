using FactoryConnect.Abstractions;

namespace FactoryConnect.Infrastructure;

/// <summary>
/// Read-only production-context activity reader over cumulative activity history
/// owned by the FC-031 in-memory joint authority provider.
/// </summary>
public sealed class JointProductionContextActivityReader(
    InMemoryMachineStateActivityAuthorityStore authorityStore,
    ObservationProcessorId stateProcessorId)
    : IProductionContextActivityReader
{
    public Task<IReadOnlyList<DurableMachineActivityPeriod>> ReadAsync(
        ObservationStreamId streamId,
        ObservationPosition? afterPosition,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<DurableMachineActivityPeriod> result = authorityStore
            .ReadActivityPeriods(stateProcessorId, streamId)
            .Where(item => afterPosition is null || item.Position > afterPosition)
            .OrderBy(static item => item.Position)
            .Take(batchSize)
            .ToArray();

        return Task.FromResult(result);
    }
}
