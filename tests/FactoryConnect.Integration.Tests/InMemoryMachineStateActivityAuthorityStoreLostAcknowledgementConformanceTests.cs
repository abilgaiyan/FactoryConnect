using System.Collections;
using System.Reflection;
using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class InMemoryMachineStateActivityAuthorityStoreLostAcknowledgementConformanceTests
{
    [Fact]
    public async Task C01_FreshCommittedPublicationLostAcknowledgementReplaysExactly()
    {
        var inner = new InMemoryMachineStateActivityAuthorityStore();
        var wrapper = new LoseNextNewPublicationAcknowledgementStore(inner);
        var id = Identity();
        var stateChange = StateChange(id, 10, MachineState.Unknown, MachineState.Running);
        var activity = Activity(id, 10, MachineState.Running);
        var proposal = Proposal(
            id,
            null,
            null,
            10,
            MachineState.Running,
            7,
            [stateChange],
            [activity],
            [Signal("spindle", true)],
            MachineState.Running,
            Stamp);

        await Assert.ThrowsAsync<LostAcknowledgementException>(
            async () => await wrapper.PublishAsync(proposal));

        var committed = await CaptureAsync(inner, id);
        Assert.Equal(0UL, committed.Snapshot.EvaluationAuthority.ProjectionRevision.Value);

        var replay = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await inner.PublishAsync(proposal));

        Assert.Equal(EvaluationAuthorityPublicationDisposition.ExactReplay, replay.Disposition);
        Assert.Equal(committed.Snapshot, replay.Snapshot);
        await AssertUnchangedAsync(inner, id, committed);
    }

    [Fact]
    public async Task C02_ForwardCommittedPublicationLostAcknowledgementReplaysExactly()
    {
        var inner = new InMemoryMachineStateActivityAuthorityStore();
        var id = Identity();
        var initialState = StateChange(id, 10, MachineState.Unknown, MachineState.Running);
        var initialActivity = Activity(id, 10, MachineState.Running);
        await inner.PublishAsync(
            Proposal(
                id,
                null,
                null,
                10,
                MachineState.Running,
                7,
                [initialState],
                [initialActivity]));

        var wrapper = new LoseNextNewPublicationAcknowledgementStore(inner);
        var forwardState = StateChange(id, 20, MachineState.Running, MachineState.Fault);
        var forwardActivity = Activity(id, 20, MachineState.Fault);
        var forward = Proposal(
            id,
            new ObservationPosition(10),
            new StateProjectionAuthorityRevision(0),
            20,
            MachineState.Fault,
            8,
            [forwardState],
            [forwardActivity],
            [Signal("fault", true)],
            MachineState.Fault,
            Stamp.AddMinutes(1));

        await Assert.ThrowsAsync<LostAcknowledgementException>(
            async () => await wrapper.PublishAsync(forward));

        var committed = await CaptureAsync(inner, id);
        Assert.Equal(1UL, committed.Snapshot.EvaluationAuthority.ProjectionRevision.Value);
        Assert.Equal([initialState, forwardState], committed.StateChanges);
        Assert.Equal([initialActivity, forwardActivity], committed.ActivityPeriods);

        var replay = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await inner.PublishAsync(forward));

        Assert.Equal(EvaluationAuthorityPublicationDisposition.ExactReplay, replay.Disposition);
        Assert.Equal(committed.Snapshot, replay.Snapshot);
        await AssertUnchangedAsync(inner, id, committed);
    }

    [Fact]
    public async Task C03_RepeatedRetainedProposalReplayKeepsCompleteAuthorityStable()
    {
        var inner = new InMemoryMachineStateActivityAuthorityStore();
        var wrapper = new LoseNextNewPublicationAcknowledgementStore(inner);
        var id = Identity();
        var stateChange = StateChange(id, 10, MachineState.Unknown, MachineState.Running);
        var activity = Activity(id, 10, MachineState.Running);
        var proposal = Proposal(
            id,
            null,
            null,
            10,
            MachineState.Running,
            7,
            [stateChange],
            [activity]);

        await Assert.ThrowsAsync<LostAcknowledgementException>(
            async () => await wrapper.PublishAsync(proposal));
        var committed = await CaptureAsync(inner, id);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var replay = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
                await inner.PublishAsync(proposal));
            Assert.Equal(EvaluationAuthorityPublicationDisposition.ExactReplay, replay.Disposition);
            Assert.Equal(committed.Snapshot.EvaluationAuthority.ProjectionRevision, replay.Snapshot.EvaluationAuthority.ProjectionRevision);
            await AssertUnchangedAsync(inner, id, committed);
        }
    }

    [Fact]
    public async Task C04_LostAcknowledgementReplayAtMaximumRevisionRemainsExactReplay()
    {
        var inner = new InMemoryMachineStateActivityAuthorityStore();
        var id = Identity();
        var proposal = Proposal(id, null, null, 10, MachineState.Running, 7);
        await inner.PublishAsync(proposal);
        ForceRevision(inner, ulong.MaxValue);
        var committed = await CaptureAsync(inner, id);

        var retained = Proposal(
            id,
            new ObservationPosition(999),
            new StateProjectionAuthorityRevision(123),
            10,
            MachineState.Running,
            7);
        var replay = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await inner.PublishAsync(retained));

        Assert.Equal(EvaluationAuthorityPublicationDisposition.ExactReplay, replay.Disposition);
        Assert.Equal(ulong.MaxValue, replay.Snapshot.EvaluationAuthority.ProjectionRevision.Value);
        await AssertUnchangedAsync(inner, id, committed);
    }

    [Fact]
    public async Task C05_ChangedPayloadIsNotReplayAndThenUsesInheritedClassification()
    {
        var inner = new InMemoryMachineStateActivityAuthorityStore();
        var id = Identity();
        var baseline = Proposal(id, null, null, 10, MachineState.Running, 7);
        await inner.PublishAsync(baseline);

        var samePositionChangedPayload = Proposal(
            id,
            null,
            null,
            10,
            MachineState.Fault,
            8);
        var conflict = await inner.PublishAsync(samePositionChangedPayload);
        Assert.IsType<MachineStateActivityAuthorityPublicationConflict>(conflict);
        Assert.IsNotType<MachineStateActivityAuthorityPublicationAccepted>(conflict);

        var forwardChangedPayload = Proposal(
            id,
            new ObservationPosition(10),
            new StateProjectionAuthorityRevision(0),
            20,
            MachineState.Fault,
            8,
            [StateChange(id, 20, MachineState.Running, MachineState.Fault)],
            [Activity(id, 20, MachineState.Fault)]);
        var forward = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await inner.PublishAsync(forwardChangedPayload));

        Assert.Equal(EvaluationAuthorityPublicationDisposition.NewPublication, forward.Disposition);
        Assert.Equal(1UL, forward.Snapshot.EvaluationAuthority.ProjectionRevision.Value);
    }

    [Fact]
    public async Task C06_LostAcknowledgementReplayIsIsolatedByProcessorAndStreamLineage()
    {
        var inner = new InMemoryMachineStateActivityAuthorityStore();
        var machine = MachineId.New();
        var target = (
            new ObservationProcessorId("state-a"),
            new ObservationStreamId(machine, "MTConnect:CNC-01"));
        var otherStream = (
            target.Item1,
            new ObservationStreamId(machine, "MTConnect:CNC-02"));
        var otherProcessor = (
            new ObservationProcessorId("state-b"),
            target.Item2);

        await inner.PublishAsync(Proposal(otherStream, null, null, 20, MachineState.Fault, 2));
        await inner.PublishAsync(Proposal(otherProcessor, null, null, 30, MachineState.Idle, 3));
        var otherStreamBefore = await CaptureAsync(inner, otherStream);
        var otherProcessorBefore = await CaptureAsync(inner, otherProcessor);

        var wrapper = new LoseNextNewPublicationAcknowledgementStore(inner);
        var targetProposal = Proposal(
            target,
            null,
            null,
            10,
            MachineState.Running,
            1,
            [StateChange(target, 10, MachineState.Unknown, MachineState.Running)],
            [Activity(target, 10, MachineState.Running)]);

        await Assert.ThrowsAsync<LostAcknowledgementException>(
            async () => await wrapper.PublishAsync(targetProposal));
        var targetCommitted = await CaptureAsync(inner, target);

        var replay = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await inner.PublishAsync(targetProposal));
        Assert.Equal(EvaluationAuthorityPublicationDisposition.ExactReplay, replay.Disposition);

        await AssertUnchangedAsync(inner, target, targetCommitted);
        await AssertUnchangedAsync(inner, otherStream, otherStreamBefore);
        await AssertUnchangedAsync(inner, otherProcessor, otherProcessorBefore);
    }

    private static async Task<AuthorityState> CaptureAsync(
        InMemoryMachineStateActivityAuthorityStore store,
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) id)
    {
        var snapshot = await store.ReadAsync(id.ProcessorId, id.StreamId);
        Assert.NotNull(snapshot);
        return new AuthorityState(
            snapshot,
            store.ReadStateChanges(id.ProcessorId, id.StreamId),
            store.ReadActivityPeriods(id.ProcessorId, id.StreamId));
    }

    private static async Task AssertUnchangedAsync(
        InMemoryMachineStateActivityAuthorityStore store,
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) id,
        AuthorityState before)
    {
        var after = await store.ReadAsync(id.ProcessorId, id.StreamId);
        Assert.Equal(before.Snapshot, after);
        Assert.Equal(
            before.Snapshot.EvaluationAuthority.ProjectionRevision,
            after!.EvaluationAuthority.ProjectionRevision);
        Assert.Equal(before.StateChanges, store.ReadStateChanges(id.ProcessorId, id.StreamId));
        Assert.Equal(before.ActivityPeriods, store.ReadActivityPeriods(id.ProcessorId, id.StreamId));
    }

    private static MachineStateActivityAuthorityPublication Proposal(
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) id,
        ObservationPosition? expectedPosition,
        StateProjectionAuthorityRevision? expectedRevision,
        ulong position,
        MachineState state,
        ulong instance,
        IReadOnlyList<DurableMachineStateChangedEvent>? stateChanges = null,
        IReadOnlyList<DurableMachineActivityPeriod>? activityPeriods = null,
        IReadOnlyList<MachineSignalValue>? signals = null,
        MachineState? activeState = null,
        DateTimeOffset? activeStartedAt = null,
        CurrentStatePolicyReference? policy = null) =>
        new(
            expectedPosition,
            expectedRevision,
            new MachineStateActivityProjection(
                id.ProcessorId,
                id.StreamId,
                new ObservationPosition(position),
                signals ?? [],
                state,
                activeState,
                activeStartedAt),
            stateChanges ?? [],
            activityPeriods ?? [],
            new EvaluationAuthorityReplayIdentity(
                id.ProcessorId,
                id.StreamId,
                new ObservationPosition(position),
                state,
                instance,
                policy ?? new CurrentStatePolicyReference("continuity/default", "1.0")));

    private static DurableMachineStateChangedEvent StateChange(
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) id,
        ulong position,
        MachineState previous,
        MachineState current) =>
        new(
            id.ProcessorId,
            new ObservationPosition(position),
            id.StreamId,
            7,
            position,
            new MachineStateChangedEvent(id.StreamId.MachineId, previous, current, Stamp));

    private static DurableMachineActivityPeriod Activity(
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) id,
        ulong position,
        MachineState state) =>
        new(
            id.ProcessorId,
            new ObservationPosition(position),
            id.StreamId,
            7,
            position,
            new MachineActivityPeriod(id.StreamId.MachineId, state, Stamp.AddMinutes(-1), Stamp));

    private static MachineSignalValue Signal(string key, bool value) =>
        new()
        {
            Key = key,
            Type = SignalType.Digital,
            Value = value,
            Source = "test",
            Quality = ObservationQuality.Good,
            Timestamp = Stamp,
        };

    private static (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) Identity()
    {
        var machine = MachineId.New();
        return (
            new ObservationProcessorId("machine-state"),
            new ObservationStreamId(machine, "MTConnect:CNC-01"));
    }

    private static void ForceRevision(
        InMemoryMachineStateActivityAuthorityStore store,
        ulong revision)
    {
        var entriesField = typeof(InMemoryMachineStateActivityAuthorityStore)
            .GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var entries = entriesField.GetValue(store)!;
        var values = (IEnumerable)entries.GetType().GetProperty("Values")!.GetValue(entries)!;
        var entry = values.Cast<object>().Single();
        var authorityProperty = entry.GetType().GetProperty("Authority")!;
        var current = (EvaluationAuthority)authorityProperty.GetValue(entry)!;
        authorityProperty.SetValue(
            entry,
            new EvaluationAuthority(
                current.StateProcessorId,
                current.ObservationStreamId,
                current.EvaluatedThrough,
                current.MachineState,
                current.LastConsumedInstanceId,
                current.AppliedContinuityPolicy,
                new StateProjectionAuthorityRevision(revision)));
    }

    private sealed class LoseNextNewPublicationAcknowledgementStore : IMachineStateActivityAuthorityStore
    {
        private readonly IMachineStateActivityAuthorityStore _inner;
        private bool _loseNext = true;

        public LoseNextNewPublicationAcknowledgementStore(IMachineStateActivityAuthorityStore inner)
        {
            _inner = inner;
        }

        public ValueTask<MachineStateActivityAuthoritySnapshot?> ReadAsync(
            ObservationProcessorId stateProcessorId,
            ObservationStreamId observationStreamId,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(stateProcessorId, observationStreamId, cancellationToken);

        public async ValueTask<MachineStateActivityAuthorityPublicationResult> PublishAsync(
            MachineStateActivityAuthorityPublication publication,
            CancellationToken cancellationToken = default)
        {
            var result = await _inner.PublishAsync(publication, cancellationToken);
            if (_loseNext &&
                result is MachineStateActivityAuthorityPublicationAccepted
                {
                    Disposition: EvaluationAuthorityPublicationDisposition.NewPublication
                })
            {
                _loseNext = false;
                throw new LostAcknowledgementException();
            }

            return result;
        }
    }

    private sealed class LostAcknowledgementException : Exception;

    private sealed record AuthorityState(
        MachineStateActivityAuthoritySnapshot Snapshot,
        DurableMachineStateChangedEvent[] StateChanges,
        DurableMachineActivityPeriod[] ActivityPeriods);

    private static readonly DateTimeOffset Stamp =
        new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
}
