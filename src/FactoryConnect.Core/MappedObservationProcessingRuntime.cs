using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class MappedObservationProcessingRuntime
{
    private readonly IDurableMappedObservationReader _reader;
    private readonly IMachineStateActivityProjectionStore _projectionStore;
    private readonly IMappedMachineObservationProcessor _processor;
    private readonly ObservationStreamId _streamId;
    private readonly ObservationProcessingRuntimeOptions _options;

    public MappedObservationProcessingRuntime(
        IDurableMappedObservationReader reader,
        IMachineStateActivityProjectionStore projectionStore,
        IMappedMachineObservationProcessor processor,
        ObservationStreamId streamId,
        ObservationProcessingRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(projectionStore);
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processor.ProcessorId);

        _reader = reader;
        _projectionStore = projectionStore;
        _processor = processor;
        _streamId = streamId;
        _options = options;
    }

    public async Task<MappedObservationReadBatch> RunCycleAsync(
        CancellationToken cancellationToken = default)
    {
        var projection = await _projectionStore.ReadAsync(
            _processor.ProcessorId,
            _streamId,
            cancellationToken);
        var batch = await _reader.ReadAsync(
            new MappedObservationReadRequest(
                _streamId,
                projection?.Position,
                _options.BatchSize),
            cancellationToken);

        if (batch.Observations.Count > 0)
        {
            await _processor.ProcessAsync(
                batch.Observations,
                cancellationToken);
        }

        return batch;
    }
}
