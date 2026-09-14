using FactoryConnect.Abstractions;

namespace FactoryConnect.Infrastructure;

/// <summary>
/// Read-only cursor seam over the FC-031 joint state/activity authority.
/// </summary>
public sealed class JointMachineStateActivityCursorReader :
    IMachineStateActivityCursorReader
{
    private readonly IMachineStateActivityAuthorityStore _authorityStore;

    public JointMachineStateActivityCursorReader(
        IMachineStateActivityAuthorityStore authorityStore)
    {
        ArgumentNullException.ThrowIfNull(authorityStore);
        _authorityStore = authorityStore;
    }

    public async ValueTask<ObservationPosition?> ReadAsync(
        ObservationProcessorId stateProcessorId,
        ObservationStreamId observationStreamId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stateProcessorId);
        ArgumentNullException.ThrowIfNull(observationStreamId);

        var snapshot = await _authorityStore
            .ReadAsync(stateProcessorId, observationStreamId, cancellationToken)
            .ConfigureAwait(false);

        return snapshot?.Projection.Position;
    }
}
