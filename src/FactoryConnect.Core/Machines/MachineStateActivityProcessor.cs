using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

public sealed class MachineStateActivityProcessor :
    IMappedMachineObservationProcessor
{
    private readonly IMachineStateActivityProjectionStore _store;

    public MachineStateActivityProcessor(
        ObservationProcessorId processorId,
        IMachineStateActivityProjectionStore store)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(store);

        ProcessorId = processorId;
        _store = store;
    }

    public ObservationProcessorId ProcessorId { get; }

    public async ValueTask ProcessAsync(
        IReadOnlyList<DurableMappedMachineObservation> observations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observations);
        cancellationToken.ThrowIfCancellationRequested();

        if (observations.Count == 0)
        {
            return;
        }

        ValidateBatch(observations);

        var streamId = observations[0].StreamId;
        var expected = await _store.ReadAsync(
            ProcessorId,
            streamId,
            cancellationToken);
        var signals = new Dictionary<string, MachineSignalValue>(
            StringComparer.OrdinalIgnoreCase);
        var state = MachineState.Unknown;
        MachineState? activeState = null;
        DateTimeOffset? activeStartedAt = null;

        if (expected is not null)
        {
            foreach (var signal in expected.Signals)
            {
                signals[signal.Key] = signal;
            }

            state = expected.State;
            activeState = expected.ActiveState;
            activeStartedAt = expected.ActiveStartedAt;
        }

        List<DurableMachineStateChangedEvent> stateChanges = [];
        List<DurableMachineActivityPeriod> activityPeriods = [];
        DurableMappedMachineObservation? lastProcessed = null;

        foreach (var durable in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (expected is not null &&
                durable.Position <= expected.Position)
            {
                continue;
            }

            var mapped = durable.Observation;
            signals[mapped.SignalKey] = new MachineSignalValue
            {
                Key = mapped.SignalKey,
                Type = mapped.Type,
                Value = mapped.Value,
                Source = mapped.Source,
                Quality = mapped.Quality,
                Timestamp = mapped.Timestamp,
            };

            var currentState = MachineStateEvaluator.Evaluate(
                new MachineSignalSnapshot(
                    mapped.MachineId,
                    signals.Values.ToArray(),
                    mapped.Timestamp));

            if (currentState != state)
            {
                var stateChanged = new MachineStateChangedEvent(
                    mapped.MachineId,
                    state,
                    currentState,
                    mapped.Timestamp);
                stateChanges.Add(
                    new DurableMachineStateChangedEvent(
                        ProcessorId,
                        durable.Position,
                        durable.StreamId,
                        durable.InstanceId,
                        durable.Sequence,
                        stateChanged));

                if (activeState is not null &&
                    activeStartedAt is not null)
                {
                    activityPeriods.Add(
                        new DurableMachineActivityPeriod(
                            ProcessorId,
                            durable.Position,
                            durable.StreamId,
                            durable.InstanceId,
                            durable.Sequence,
                            new MachineActivityPeriod(
                                mapped.MachineId,
                                activeState.Value,
                                activeStartedAt.Value,
                                mapped.Timestamp)));
                }

                state = currentState;
                activeState = currentState;
                activeStartedAt = mapped.Timestamp;
            }

            lastProcessed = durable;
        }

        if (lastProcessed is null)
        {
            return;
        }

        var projection = new MachineStateActivityProjection(
            ProcessorId,
            streamId,
            lastProcessed.Position,
            signals.Values
                .OrderBy(signal => signal.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            state,
            activeState,
            activeStartedAt);

        await _store.CommitAsync(
            new MachineStateActivityProjectionCommit(
                expected,
                projection,
                stateChanges,
                activityPeriods),
            cancellationToken);
    }

    private static void ValidateBatch(
        IReadOnlyList<DurableMappedMachineObservation> observations)
    {
        var streamId = observations[0].StreamId;
        ObservationPosition? previous = null;

        foreach (var observation in observations)
        {
            ArgumentNullException.ThrowIfNull(observation);

            if (observation.StreamId != streamId)
            {
                throw new ArgumentException(
                    "Every observation in a processing batch must belong to the same stream.",
                    nameof(observations));
            }

            if (previous is not null &&
                previous >= observation.Position)
            {
                throw new ArgumentException(
                    "Observations must be ordered by strictly increasing durable position.",
                    nameof(observations));
            }

            previous = observation.Position;
        }
    }
}
