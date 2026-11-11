using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;

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
        var cycle = await RunCycleWithOutcomeAsync(cancellationToken);
        if (cycle.Hold is not null)
        {
            throw new InvalidOperationException(cycle.Hold.ToDiagnosticString());
        }
        return cycle.Batch;
    }

    public async Task<MappedObservationProcessingCycle> RunCycleWithOutcomeAsync(
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
            if (_processor is IMachineStateActivityOutcomeProcessor outcomeProcessor)
            {
                var result = await outcomeProcessor.ProcessWithOutcomeAsync(
                    batch.Observations, cancellationToken);
                if (result is MachineStateActivityChronologyHold hold)
                {
                    return new MappedObservationProcessingCycle(batch, hold);
                }
            }
            else
            {
                await _processor.ProcessAsync(batch.Observations, cancellationToken);
            }
        }

        return new MappedObservationProcessingCycle(batch, null);
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
