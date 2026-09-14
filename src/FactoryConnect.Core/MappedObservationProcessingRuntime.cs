using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

public sealed class MappedObservationProcessingRuntime
{
    private readonly IDurableMappedObservationReader _reader;
    private readonly IMachineStateActivityCursorReader _cursorReader;
    private readonly IMappedMachineObservationProcessor _processor;
    private readonly ObservationStreamId _streamId;
    private readonly ObservationProcessingRuntimeOptions _options;

    public MappedObservationProcessingRuntime(
        IDurableMappedObservationReader reader,
        IMachineStateActivityCursorReader cursorReader,
        IMappedMachineObservationProcessor processor,
        ObservationStreamId streamId,
        ObservationProcessingRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(cursorReader);
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processor.ProcessorId);

        _reader = reader;
        _cursorReader = cursorReader;
        _processor = processor;
        _streamId = streamId;
        _options = options;
    }

    public MappedObservationProcessingRuntime(
        IDurableMappedObservationReader reader,
        IMachineStateActivityProjectionStore projectionStore,
        IMappedMachineObservationProcessor processor,
        ObservationStreamId streamId,
        ObservationProcessingRuntimeOptions options)
        : this(
            reader,
            new ProjectionStoreCursorReader(projectionStore),
            processor,
            streamId,
            options)
    {
    }

    public async Task<MappedObservationReadBatch> RunCycleAsync(
        CancellationToken cancellationToken = default)
    {
        var afterPosition = await _cursorReader.ReadAsync(
            _processor.ProcessorId,
            _streamId,
            cancellationToken);
        var batch = await _reader.ReadAsync(
            new MappedObservationReadRequest(
                _streamId,
                afterPosition,
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

    private sealed class ProjectionStoreCursorReader :
        IMachineStateActivityCursorReader
    {
        private readonly IMachineStateActivityProjectionStore _projectionStore;

        public ProjectionStoreCursorReader(
            IMachineStateActivityProjectionStore projectionStore)
        {
            ArgumentNullException.ThrowIfNull(projectionStore);
            _projectionStore = projectionStore;
        }

        public async ValueTask<ObservationPosition?> ReadAsync(
            ObservationProcessorId stateProcessorId,
            ObservationStreamId observationStreamId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stateProcessorId);
            ArgumentNullException.ThrowIfNull(observationStreamId);

            var projection = await _projectionStore.ReadAsync(
                stateProcessorId,
                observationStreamId,
                cancellationToken);
            return projection?.Position;
        }
    }
}
