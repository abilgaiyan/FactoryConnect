namespace FactoryConnect.Abstractions;

/// <summary>
/// Read-only cursor seam for the authoritative joint state/activity projection.
/// </summary>
public interface IMachineStateActivityCursorReader
{
    ValueTask<ObservationPosition?> ReadAsync(
        ObservationProcessorId stateProcessorId,
        ObservationStreamId observationStreamId,
        CancellationToken cancellationToken = default);
}
