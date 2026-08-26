namespace FactoryConnect.Abstractions;

/// <summary>
/// Atomically stores machine state/activity projection progress and outputs.
/// </summary>
public interface IMachineStateActivityProjectionStore
{
    ValueTask<MachineStateActivityProjection?> ReadAsync(
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        CancellationToken cancellationToken = default);

    ValueTask CommitAsync(
        MachineStateActivityProjectionCommit commit,
        CancellationToken cancellationToken = default);
}
