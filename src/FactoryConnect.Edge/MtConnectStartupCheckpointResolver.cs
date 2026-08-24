using FactoryConnect.Abstractions;

namespace FactoryConnect.Edge;

public sealed class MtConnectStartupCheckpointResolver(
    IObservationIngestionStore store)
{
    public async ValueTask<MtConnectStartupState> ResolveAsync(
        MtConnectAcquisitionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var streamId = MtConnectObservationStreamId.Create(
            options.MachineId,
            options.DeviceKey);

        var checkpoint = await store.ReadCheckpointAsync(
            streamId,
            cancellationToken);

        if (checkpoint is not null &&
            checkpoint.StreamId != streamId)
        {
            throw new InvalidOperationException(
                "The ingestion store returned a checkpoint for " +
                "a different observation stream.");
        }

        return new MtConnectStartupState(
            streamId,
            checkpoint?.NextSequence ?? options.FromSequence,
            checkpoint);
    }
}
