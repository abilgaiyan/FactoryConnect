using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Machines;

public abstract record MachineStateActivityContinuityCalculationResult;

public sealed record MachineStateActivityContinuityNoAdvance :
    MachineStateActivityContinuityCalculationResult
{
    public static MachineStateActivityContinuityNoAdvance Instance { get; } = new();

    private MachineStateActivityContinuityNoAdvance()
    {
    }
}

public sealed record MachineStateActivityContinuityPublication :
    MachineStateActivityContinuityCalculationResult
{
    public MachineStateActivityContinuityPublication(
        MachineStateActivityAuthorityPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        Publication = publication;
    }

    public MachineStateActivityAuthorityPublication Publication { get; }
}

/// <summary>
/// Uncomposed FC-031.2D behavioral calculator. It derives one deterministic
/// joint-authority proposal without reading or mutating provider state.
/// </summary>
public sealed class MachineStateActivityContinuityCalculator
{
    public MachineStateActivityContinuityCalculator(ObservationProcessorId processorId)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ProcessorId = processorId;
    }

    public ObservationProcessorId ProcessorId { get; }

    public MachineStateActivityContinuityCalculationResult Calculate(
        MachineStateActivityAuthoritySnapshot? current,
        IReadOnlyList<DurableMappedMachineObservation> observations,
        CurrentStateContinuityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(policy);

        if (observations.Count == 0)
        {
            return MachineStateActivityContinuityNoAdvance.Instance;
        }

        ValidateBatch(observations);

        var streamId = observations[0].StreamId;
        if (current is not null &&
            (current.Projection.ProcessorId != ProcessorId ||
             current.Projection.StreamId != streamId))
        {
            throw new ArgumentException(
                "The current authority snapshot must belong to the calculator processor and observation stream.",
                nameof(current));
        }

        var signals = new Dictionary<string, MachineSignalValue>(StringComparer.OrdinalIgnoreCase);
        var state = MachineState.Unknown;
        MachineState? activeState = null;
        DateTimeOffset? activeStartedAt = null;
        ulong? lastConsumedInstanceId = null;

        if (current is not null)
        {
            foreach (var signal in current.Projection.Signals)
            {
                signals[signal.Key] = signal;
            }

            state = current.Projection.State;
            activeState = current.Projection.ActiveState;
            activeStartedAt = current.Projection.ActiveStartedAt;
            lastConsumedInstanceId = current.EvaluationAuthority.LastConsumedInstanceId;
        }

        List<DurableMachineStateChangedEvent> stateChanges = [];
        List<DurableMachineActivityPeriod> activityPeriods = [];
        DurableMappedMachineObservation? lastProcessed = null;

        foreach (var durable in observations)
        {
            if (current is not null && durable.Position <= current.Projection.Position)
            {
                continue;
            }

            if (lastConsumedInstanceId is not null &&
                durable.InstanceId != lastConsumedInstanceId.Value &&
                policy.Mode == StateContinuityMode.Reset)
            {
                signals.Clear();
                state = MachineState.Unknown;
                activeState = null;
                activeStartedAt = null;
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
                stateChanges.Add(
                    new DurableMachineStateChangedEvent(
                        ProcessorId,
                        durable.Position,
                        durable.StreamId,
                        durable.InstanceId,
                        durable.Sequence,
                        new MachineStateChangedEvent(
                            mapped.MachineId,
                            state,
                            currentState,
                            mapped.Timestamp)));

                if (activeState is not null && activeStartedAt is not null)
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

            lastConsumedInstanceId = durable.InstanceId;
            lastProcessed = durable;
        }

        if (lastProcessed is null)
        {
            return MachineStateActivityContinuityNoAdvance.Instance;
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

        var identity = new EvaluationAuthorityReplayIdentity(
            ProcessorId,
            streamId,
            lastProcessed.Position,
            state,
            lastConsumedInstanceId!.Value,
            policy.Reference);

        return new MachineStateActivityContinuityPublication(
            new MachineStateActivityAuthorityPublication(
                current?.Projection.Position,
                current?.EvaluationAuthority.ProjectionRevision,
                projection,
                stateChanges,
                activityPeriods,
                identity));
    }

    private static void ValidateBatch(IReadOnlyList<DurableMappedMachineObservation> observations)
    {
        var streamId = observations[0]?.StreamId ??
            throw new ArgumentException("Observations may not contain null entries.", nameof(observations));
        ObservationPosition? previous = null;

        foreach (var observation in observations)
        {
            ArgumentNullException.ThrowIfNull(observation);

            if (observation.StreamId != streamId)
            {
                throw new ArgumentException(
                    "Every observation in a calculation batch must belong to the same stream.",
                    nameof(observations));
            }

            if (previous is not null && previous >= observation.Position)
            {
                throw new ArgumentException(
                    "Observations must be ordered by strictly increasing durable position.",
                    nameof(observations));
            }

            previous = observation.Position;
        }
    }
}
