using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;

namespace FactoryConnect.Edge;

public sealed class MtConnectDurableObservationSink :
    IMtConnectObservationSink
{
    private readonly IObservationIngestionStore _store;
    private readonly ObservationStreamId _streamId;

    public MtConnectDurableObservationSink(
        IObservationIngestionStore store,
        ObservationStreamId streamId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(streamId);

        _store = store;
        _streamId = streamId;
    }

    public ValueTask WriteAsync(
        MtConnectSampleResult result,
        ObservationCheckpoint? expectedCheckpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var checkpoint = new ObservationCheckpoint(
            _streamId,
            result.InstanceId,
            result.NextSequence);

        var observations = result.Observations
            .Select(item => new SequencedMachineObservation(
                item.Sequence,
                item.Observation))
            .ToArray();

        return _store.CommitAsync(
            new ObservationIngestionBatch(
                expectedCheckpoint,
                checkpoint,
                observations),
            cancellationToken);
    }
}
