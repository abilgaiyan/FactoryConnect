using FactoryConnect.Abstractions;

namespace FactoryConnect.Infrastructure;

/// <summary>
/// Uncomposed in-memory reference provider for the FC-031 joint state/activity
/// authority. Composition is intentionally deferred to FC-031.2D.5.
/// </summary>
public sealed class InMemoryMachineStateActivityAuthorityStore :
    IMachineStateActivityAuthorityStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<AuthorityKey, Entry> _entries = [];
    private readonly Action<MachineStateActivityAuthorityFaultPoint>? _fault;

    public InMemoryMachineStateActivityAuthorityStore()
    {
    }

    internal InMemoryMachineStateActivityAuthorityStore(
        Action<MachineStateActivityAuthorityFaultPoint> fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        _fault = fault;
    }

    public ValueTask<MachineStateActivityAuthoritySnapshot?> ReadAsync(
        ObservationProcessorId stateProcessorId,
        ObservationStreamId observationStreamId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stateProcessorId);
        ArgumentNullException.ThrowIfNull(observationStreamId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _entries.TryGetValue(
                new AuthorityKey(stateProcessorId, observationStreamId),
                out var entry);
            return ValueTask.FromResult(
                entry is null ? null : CreateSnapshot(entry));
        }
    }

    public ValueTask<MachineStateActivityAuthorityPublicationResult> PublishAsync(
        MachineStateActivityAuthorityPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var key = new AuthorityKey(
                publication.Projection.ProcessorId,
                publication.Projection.StreamId);
            _entries.TryGetValue(key, out var current);

            if (current is not null && IsExactReplay(current, publication))
            {
                return ValueTask.FromResult<MachineStateActivityAuthorityPublicationResult>(
                    new MachineStateActivityAuthorityPublicationAccepted(
                        CreateSnapshot(current),
                        EvaluationAuthorityPublicationDisposition.ExactReplay));
            }

            if (!MatchesExpected(current, publication))
            {
                return ValueTask.FromResult<MachineStateActivityAuthorityPublicationResult>(
                    new MachineStateActivityAuthorityPublicationConflict(
                        current is null ? null : CreateSnapshot(current)));
            }

            if (!IsValidAdvancement(current, publication))
            {
                return ValueTask.FromResult<MachineStateActivityAuthorityPublicationResult>(
                    new MachineStateActivityAuthorityPublicationConflict(
                        current is null ? null : CreateSnapshot(current)));
            }

            if (current is not null &&
                current.Authority.ProjectionRevision.Value == ulong.MaxValue)
            {
                return ValueTask.FromResult<MachineStateActivityAuthorityPublicationResult>(
                    new MachineStateActivityAuthorityRevisionExhausted(
                        CreateSnapshot(current)));
            }

            InvokeFault(MachineStateActivityAuthorityFaultPoint.BeforeNewPublicationMaterialization);

            var revision = current is null
                ? 0UL
                : current.Authority.ProjectionRevision.Value + 1UL;
            var identity = publication.EvaluationIdentity;
            var authority = new EvaluationAuthority(
                identity.StateProcessorId,
                identity.ObservationStreamId,
                identity.EvaluatedThrough,
                identity.MachineState,
                identity.LastConsumedInstanceId,
                identity.AppliedContinuityPolicy,
                new StateProjectionAuthorityRevision(revision));

            InvokeFault(MachineStateActivityAuthorityFaultPoint.AfterAuthorityConstruction);

            var stateChanges = publication.StateChanges.ToArray();
            var activityPeriods = publication.ActivityPeriods.ToArray();

            InvokeFault(MachineStateActivityAuthorityFaultPoint.AfterPublicationOutputCopies);

            var stateChangeHistory = Append(current?.StateChangeHistory, stateChanges);
            var activityPeriodHistory = Append(current?.ActivityPeriodHistory, activityPeriods);

            InvokeFault(MachineStateActivityAuthorityFaultPoint.AfterCumulativeHistoryConstruction);

            var next = new Entry(
                publication.Projection,
                authority,
                stateChanges,
                activityPeriods,
                stateChangeHistory,
                activityPeriodHistory);

            InvokeFault(MachineStateActivityAuthorityFaultPoint.ImmediatelyBeforeDictionaryAssignment);

            _entries[key] = next;

            return ValueTask.FromResult<MachineStateActivityAuthorityPublicationResult>(
                new MachineStateActivityAuthorityPublicationAccepted(
                    CreateSnapshot(next),
                    EvaluationAuthorityPublicationDisposition.NewPublication));
        }
    }

    public DurableMachineStateChangedEvent[] ReadStateChanges(
        ObservationProcessorId stateProcessorId,
        ObservationStreamId observationStreamId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(
                    new AuthorityKey(stateProcessorId, observationStreamId),
                    out var entry)
                ? entry.StateChangeHistory.ToArray()
                : [];
        }
    }

    public DurableMachineActivityPeriod[] ReadActivityPeriods(
        ObservationProcessorId stateProcessorId,
        ObservationStreamId observationStreamId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(
                    new AuthorityKey(stateProcessorId, observationStreamId),
                    out var entry)
                ? entry.ActivityPeriodHistory.ToArray()
                : [];
        }
    }

    private void InvokeFault(MachineStateActivityAuthorityFaultPoint point) =>
        _fault?.Invoke(point);

    private static bool MatchesExpected(
        Entry? current,
        MachineStateActivityAuthorityPublication publication)
    {
        if (current is null)
        {
            return publication.ExpectedProjectionPosition is null &&
                   publication.ExpectedAuthorityRevision is null;
        }

        return publication.ExpectedProjectionPosition == current.Projection.Position &&
               publication.ExpectedAuthorityRevision == current.Authority.ProjectionRevision;
    }

    private static bool IsValidAdvancement(
        Entry? current,
        MachineStateActivityAuthorityPublication publication)
    {
        if (current is null)
        {
            return true;
        }

        if (publication.Projection.Position <= current.Projection.Position)
        {
            return false;
        }

        return publication.StateChanges.All(
                   item => item.Position > current.Projection.Position) &&
               publication.ActivityPeriods.All(
                   item => item.Position > current.Projection.Position);
    }

    private static bool IsExactReplay(
        Entry current,
        MachineStateActivityAuthorityPublication publication)
    {
        var identity = publication.EvaluationIdentity;
        return ProjectionEquals(current.Projection, publication.Projection) &&
               current.LastStateChanges.SequenceEqual(publication.StateChanges) &&
               current.LastActivityPeriods.SequenceEqual(publication.ActivityPeriods) &&
               current.Authority.StateProcessorId == identity.StateProcessorId &&
               current.Authority.ObservationStreamId == identity.ObservationStreamId &&
               current.Authority.EvaluatedThrough == identity.EvaluatedThrough &&
               current.Authority.MachineState == identity.MachineState &&
               current.Authority.LastConsumedInstanceId == identity.LastConsumedInstanceId &&
               current.Authority.AppliedContinuityPolicy == identity.AppliedContinuityPolicy;
    }

    private static bool ProjectionEquals(
        MachineStateActivityProjection left,
        MachineStateActivityProjection right) =>
        left.ProcessorId == right.ProcessorId &&
        left.StreamId == right.StreamId &&
        left.Position == right.Position &&
        left.State == right.State &&
        left.ActiveState == right.ActiveState &&
        left.ActiveStartedAt == right.ActiveStartedAt &&
        left.Signals.SequenceEqual(right.Signals);

    private static MachineStateActivityAuthoritySnapshot CreateSnapshot(Entry entry) =>
        new(entry.Projection, entry.Authority);

    private static T[] Append<T>(IReadOnlyList<T>? existing, IReadOnlyList<T> additions)
    {
        if (existing is null || existing.Count == 0)
        {
            return additions.ToArray();
        }

        var result = new T[existing.Count + additions.Count];
        for (var i = 0; i < existing.Count; i++)
        {
            result[i] = existing[i];
        }

        for (var i = 0; i < additions.Count; i++)
        {
            result[existing.Count + i] = additions[i];
        }

        return result;
    }

    private readonly record struct AuthorityKey(
        ObservationProcessorId ProcessorId,
        ObservationStreamId StreamId);

    private sealed class Entry
    {
        public Entry(
            MachineStateActivityProjection projection,
            EvaluationAuthority authority,
            DurableMachineStateChangedEvent[] lastStateChanges,
            DurableMachineActivityPeriod[] lastActivityPeriods,
            DurableMachineStateChangedEvent[] stateChangeHistory,
            DurableMachineActivityPeriod[] activityPeriodHistory)
        {
            Projection = projection;
            Authority = authority;
            LastStateChanges = lastStateChanges;
            LastActivityPeriods = lastActivityPeriods;
            StateChangeHistory = stateChangeHistory;
            ActivityPeriodHistory = activityPeriodHistory;
        }

        public MachineStateActivityProjection Projection { get; }

        public EvaluationAuthority Authority { get; set; }

        public DurableMachineStateChangedEvent[] LastStateChanges { get; }

        public DurableMachineActivityPeriod[] LastActivityPeriods { get; }

        public DurableMachineStateChangedEvent[] StateChangeHistory { get; }

        public DurableMachineActivityPeriod[] ActivityPeriodHistory { get; }
    }
}

internal enum MachineStateActivityAuthorityFaultPoint
{
    BeforeNewPublicationMaterialization = 0,
    AfterAuthorityConstruction = 1,
    AfterPublicationOutputCopies = 2,
    AfterCumulativeHistoryConstruction = 3,
    ImmediatelyBeforeDictionaryAssignment = 4,
}
