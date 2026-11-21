using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using FactoryConnect.Core;

namespace FactoryConnect.Edge;

/// <summary>
/// Reconstructs activity input from retained raw evidence after a process restart.
/// The durable canonical-mapping checkpoint bounds eligibility. Mapping replay uses
/// a private in-memory authority and never advances a durable checkpoint.
/// </summary>
public sealed class RetainedActivityObservationReader(
    IDurableObservationReader rawReader,
    IObservationProcessingCheckpointStore checkpoints,
    ObservationProcessorId mappingProcessorId,
    Func<IMappedMachineObservationSink, IObservationProcessor> createReplayProcessor)
    : IDurableMappedObservationReader
{
    public async ValueTask<MappedObservationReadBatch> ReadAsync(
        MappedObservationReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var frontier = await checkpoints.ReadCheckpointAsync(
            mappingProcessorId, request.StreamId, cancellationToken);
        if (frontier is null ||
            (request.AfterPosition is not null && request.AfterPosition >= frontier.Position))
        {
            return new MappedObservationReadBatch(request.StreamId, [], false);
        }

        var sink = new InMemoryMappedMachineObservationSink();
        var replay = createReplayProcessor(sink);
        var after = request.AfterPosition;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = await rawReader.ReadAsync(
                new ObservationReadRequest(request.StreamId, after, request.BatchSize),
                cancellationToken);
            var eligible = raw.Observations.Where(item => item.Position <= frontier.Position).ToArray();
            if (eligible.Length == 0)
            {
                return new MappedObservationReadBatch(request.StreamId, [], false);
            }
            await replay.ProcessAsync(eligible, cancellationToken);
            var mapped = await sink.ReadAsync(request, cancellationToken);
            var last = eligible[^1].Position;
            var hasMore = last < frontier.Position;
            if (mapped.Observations.Count > 0 || !hasMore)
            {
                return new MappedObservationReadBatch(request.StreamId, mapped.Observations, hasMore);
            }
            after = last;
        }
    }
}
