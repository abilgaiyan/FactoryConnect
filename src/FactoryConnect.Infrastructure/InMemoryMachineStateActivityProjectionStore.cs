using FactoryConnect.Abstractions;

namespace FactoryConnect.Infrastructure;

public sealed class InMemoryMachineStateActivityProjectionStore :
    IMachineStateActivityProjectionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<
        ProjectionKey,
        MachineStateActivityProjection> _projections = [];
    private readonly Dictionary<
        OutputKey,
        DurableMachineStateChangedEvent> _stateChanges = [];
    private readonly Dictionary<
        OutputKey,
        DurableMachineActivityPeriod> _activityPeriods = [];

    public ValueTask<MachineStateActivityProjection?> ReadAsync(
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(streamId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _projections.TryGetValue(
                new ProjectionKey(processorId, streamId),
                out var projection);

            return ValueTask.FromResult<
                MachineStateActivityProjection?>(projection);
        }
    }

    public ValueTask CommitAsync(
        MachineStateActivityProjectionCommit commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var projection = commit.Projection;
            var projectionKey = new ProjectionKey(
                projection.ProcessorId,
                projection.StreamId);
            _projections.TryGetValue(projectionKey, out var current);

            if (current != commit.ExpectedProjection)
            {
                throw new InvalidOperationException(
                    "The machine state/activity projection no longer matches the expected state.");
            }

            ValidateOutputs(commit);
            var pendingStateChanges = StageStateChanges(commit.StateChanges);
            var pendingActivityPeriods = StageActivityPeriods(
                commit.ActivityPeriods);

            foreach (var pair in pendingStateChanges)
            {
                _stateChanges.TryAdd(pair.Key, pair.Value);
            }

            foreach (var pair in pendingActivityPeriods)
            {
                _activityPeriods.TryAdd(pair.Key, pair.Value);
            }

            _projections[projectionKey] = projection;
        }

        return ValueTask.CompletedTask;
    }

    public DurableMachineStateChangedEvent[] ReadStateChanges(
        ObservationProcessorId processorId,
        ObservationStreamId streamId)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(streamId);

        lock (_gate)
        {
            return _stateChanges.Values
                .Where(item =>
                    item.ProcessorId == processorId &&
                    item.StreamId == streamId)
                .OrderBy(item => item.Position)
                .ToArray();
        }
    }

    public DurableMachineActivityPeriod[] ReadActivityPeriods(
        ObservationProcessorId processorId,
        ObservationStreamId streamId)
    {
        ArgumentNullException.ThrowIfNull(processorId);
        ArgumentNullException.ThrowIfNull(streamId);

        lock (_gate)
        {
            return _activityPeriods.Values
                .Where(item =>
                    item.ProcessorId == processorId &&
                    item.StreamId == streamId)
                .OrderBy(item => item.Position)
                .ToArray();
        }
    }

    private Dictionary<OutputKey, DurableMachineStateChangedEvent>
        StageStateChanges(
            IReadOnlyList<DurableMachineStateChangedEvent> stateChanges)
    {
        Dictionary<OutputKey, DurableMachineStateChangedEvent> pending = [];

        foreach (var item in stateChanges)
        {
            var key = new OutputKey(
                item.ProcessorId,
                item.StreamId,
                item.Position);

            if ((_stateChanges.TryGetValue(key, out var existing) &&
                 existing != item) ||
                (pending.TryGetValue(key, out var staged) &&
                 staged != item))
            {
                throw new InvalidOperationException(
                    "A different state change already exists at the same durable position.");
            }

            pending.TryAdd(key, item);
        }

        return pending;
    }

    private Dictionary<OutputKey, DurableMachineActivityPeriod>
        StageActivityPeriods(
            IReadOnlyList<DurableMachineActivityPeriod> activityPeriods)
    {
        Dictionary<OutputKey, DurableMachineActivityPeriod> pending = [];

        foreach (var item in activityPeriods)
        {
            var key = new OutputKey(
                item.ProcessorId,
                item.StreamId,
                item.Position);

            if ((_activityPeriods.TryGetValue(key, out var existing) &&
                 existing != item) ||
                (pending.TryGetValue(key, out var staged) &&
                 staged != item))
            {
                throw new InvalidOperationException(
                    "A different activity period already exists at the same durable position.");
            }

            pending.TryAdd(key, item);
        }

        return pending;
    }

    private static void ValidateOutputs(
        MachineStateActivityProjectionCommit commit)
    {
        foreach (var item in commit.StateChanges)
        {
            ValidateOutputIdentity(
                commit,
                item.ProcessorId,
                item.StreamId,
                item.Position);
        }

        foreach (var item in commit.ActivityPeriods)
        {
            ValidateOutputIdentity(
                commit,
                item.ProcessorId,
                item.StreamId,
                item.Position);
        }
    }

    private static void ValidateOutputIdentity(
        MachineStateActivityProjectionCommit commit,
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        ObservationPosition position)
    {
        if (processorId != commit.Projection.ProcessorId ||
            streamId != commit.Projection.StreamId ||
            position > commit.Projection.Position ||
            (commit.ExpectedProjection is not null &&
             position <= commit.ExpectedProjection.Position))
        {
            throw new InvalidOperationException(
                "Derived output must belong to the committed projection advancement.");
        }
    }

    private readonly record struct ProjectionKey(
        ObservationProcessorId ProcessorId,
        ObservationStreamId StreamId);

    private readonly record struct OutputKey(
        ObservationProcessorId ProcessorId,
        ObservationStreamId StreamId,
        ObservationPosition Position);
}
