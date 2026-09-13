using System.Collections;
using System.Reflection;
using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class InMemoryMachineStateActivityAuthorityStoreConformanceTests
{
    [Fact]
    public async Task FreshAndForwardPublicationAllocateMonotonicRevisions()
    {
        var store = new InMemoryMachineStateActivityAuthorityStore();
        var id = Identity();
        var fresh = Proposal(id, null, null, 10, MachineState.Running, 7);

        var first = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(fresh));
        Assert.Equal(EvaluationAuthorityPublicationDisposition.NewPublication, first.Disposition);
        Assert.Equal(0UL, first.Snapshot.EvaluationAuthority.ProjectionRevision.Value);

        var forward = Proposal(
            id,
            new ObservationPosition(10),
            new StateProjectionAuthorityRevision(0),
            20,
            MachineState.Fault,
            8);
        var second = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(forward));

        Assert.Equal(EvaluationAuthorityPublicationDisposition.NewPublication, second.Disposition);
        Assert.Equal(1UL, second.Snapshot.EvaluationAuthority.ProjectionRevision.Value);
        Assert.Equal(new ObservationPosition(20), second.Snapshot.Projection.Position);
        Assert.Equal(MachineState.Fault, second.Snapshot.Projection.State);
    }

    [Fact]
    public async Task IdenticalPayloadWithStaleExpectedTokensIsExactReplay()
    {
        var store = new InMemoryMachineStateActivityAuthorityStore();
        var id = Identity();
        var output = StateChange(id, 20, MachineState.Unknown, MachineState.Running);
        var initial = Proposal(id, null, null, 20, MachineState.Running, 7, [output]);
        var accepted = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(initial));

        var replay = Proposal(
            id,
            new ObservationPosition(20),
            new StateProjectionAuthorityRevision(999),
            20,
            MachineState.Running,
            7,
            [output]);
        var result = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(replay));

        Assert.Equal(EvaluationAuthorityPublicationDisposition.ExactReplay, result.Disposition);
        Assert.Equal(
            accepted.Snapshot.EvaluationAuthority.ProjectionRevision,
            result.Snapshot.EvaluationAuthority.ProjectionRevision);
        Assert.Single(store.ReadStateChanges(id.ProcessorId, id.StreamId));
    }

    [Fact]
    public async Task StaleTokensWithDifferentPayloadAreConflict()
    {
        var store = new InMemoryMachineStateActivityAuthorityStore();
        var id = Identity();
        await store.PublishAsync(Proposal(id, null, null, 20, MachineState.Running, 7));

        var changed = Proposal(
            id,
            new ObservationPosition(19),
            new StateProjectionAuthorityRevision(999),
            21,
            MachineState.Fault,
            8);

        Assert.IsType<MachineStateActivityAuthorityPublicationConflict>(
            await store.PublishAsync(changed));
        var current = await store.ReadAsync(id.ProcessorId, id.StreamId);
        Assert.NotNull(current);
        Assert.Equal(new ObservationPosition(20), current.Projection.Position);
        Assert.Equal(0UL, current.EvaluationAuthority.ProjectionRevision.Value);
    }

    [Fact]
    public async Task MatchingCasNonForwardAndOutputOutsideCurrentAdvancementAreConflict()
    {
        var store = new InMemoryMachineStateActivityAuthorityStore();
        var id = Identity();
        await store.PublishAsync(Proposal(id, null, null, 20, MachineState.Running, 7));

        var samePosition = Proposal(
            id,
            new ObservationPosition(20),
            new StateProjectionAuthorityRevision(0),
            20,
            MachineState.Fault,
            8);
        Assert.IsType<MachineStateActivityAuthorityPublicationConflict>(
            await store.PublishAsync(samePosition));

        var oldOutput = StateChange(id, 20, MachineState.Running, MachineState.Fault);
        var forwardWithOldOutput = Proposal(
            id,
            new ObservationPosition(20),
            new StateProjectionAuthorityRevision(0),
            30,
            MachineState.Fault,
            8,
            [oldOutput]);
        Assert.IsType<MachineStateActivityAuthorityPublicationConflict>(
            await store.PublishAsync(forwardWithOldOutput));

        Assert.Empty(store.ReadStateChanges(id.ProcessorId, id.StreamId));
        Assert.Equal(
            new ObservationPosition(20),
            (await store.ReadAsync(id.ProcessorId, id.StreamId))!.Projection.Position);
    }

    [Fact]
    public void IntrinsicValidationRejectsCrossComponentMismatchAndDuplicateOrdering()
    {
        var id = Identity();
        var projection = Projection(id, 20, MachineState.Running);
        var wrongIdentity = new EvaluationAuthorityReplayIdentity(
            id.ProcessorId,
            id.StreamId,
            new ObservationPosition(21),
            MachineState.Running,
            7,
            Policy());

        Assert.Throws<ArgumentException>(() =>
            new MachineStateActivityAuthorityPublication(
                null, null, projection, [], [], wrongIdentity));

        var duplicate = StateChange(id, 20, MachineState.Unknown, MachineState.Running);
        Assert.Throws<ArgumentException>(() =>
            Proposal(id, null, null, 20, MachineState.Running, 7, [duplicate, duplicate]));
    }

    [Fact]
    public async Task StructuralDifferencesAcrossProjectionStateOutputsActivityAndAuthorityAreNotReplay()
    {
        var id = Identity();
        var stateChange = StateChange(id, 20, MachineState.Unknown, MachineState.Running);
        var activity = Activity(id, 20, MachineState.Running);

        await AssertNotReplay(
            id,
            Proposal(id, null, null, 20, MachineState.Running, 7, [stateChange], [activity]),
            Proposal(id, null, null, 20, MachineState.Running, 7, [stateChange], [activity], activeStartedAt: Stamp.AddSeconds(1)));
        await AssertNotReplay(
            id,
            Proposal(id, null, null, 20, MachineState.Running, 7, [stateChange], [activity]),
            Proposal(id, null, null, 20, MachineState.Running, 7, [], [activity]));
        await AssertNotReplay(
            id,
            Proposal(id, null, null, 20, MachineState.Running, 7, [stateChange], [activity]),
            Proposal(id, null, null, 20, MachineState.Running, 7, [stateChange], []));
        await AssertNotReplay(
            id,
            Proposal(id, null, null, 20, MachineState.Running, 7, [stateChange], [activity]),
            Proposal(id, null, null, 20, MachineState.Running, 8, [stateChange], [activity]));
    }

    [Fact]
    public async Task ExactReplayPrecedesRevisionExhaustionAndChangedForwardExhaustsWithoutMutation()
    {
        var store = new InMemoryMachineStateActivityAuthorityStore();
        var id = Identity();
        var currentProposal = Proposal(id, null, null, 20, MachineState.Running, 7);
        await store.PublishAsync(currentProposal);
        ForceRevision(store, ulong.MaxValue);

        var replay = Proposal(
            id,
            new ObservationPosition(999),
            new StateProjectionAuthorityRevision(1),
            20,
            MachineState.Running,
            7);
        var replayResult = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(replay));
        Assert.Equal(EvaluationAuthorityPublicationDisposition.ExactReplay, replayResult.Disposition);
        Assert.Equal(ulong.MaxValue, replayResult.Snapshot.EvaluationAuthority.ProjectionRevision.Value);

        var forward = Proposal(
            id,
            new ObservationPosition(20),
            new StateProjectionAuthorityRevision(ulong.MaxValue),
            30,
            MachineState.Fault,
            8);
        Assert.IsType<MachineStateActivityAuthorityRevisionExhausted>(
            await store.PublishAsync(forward));

        var current = await store.ReadAsync(id.ProcessorId, id.StreamId);
        Assert.Equal(new ObservationPosition(20), current!.Projection.Position);
        Assert.Equal(ulong.MaxValue, current.EvaluationAuthority.ProjectionRevision.Value);
    }

    [Fact]
    public async Task ProviderPublishesProjectionOutputsAndAuthorityAsOneQuiescentTuple()
    {
        var store = new InMemoryMachineStateActivityAuthorityStore();
        var id = Identity();
        var stateChange = StateChange(id, 10, MachineState.Unknown, MachineState.Running);
        var activity = Activity(id, 10, MachineState.Running);

        var result = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(
                Proposal(id, null, null, 10, MachineState.Running, 3, [stateChange], [activity])));

        var snapshot = await store.ReadAsync(id.ProcessorId, id.StreamId);
        Assert.Equal(result.Snapshot, snapshot);
        Assert.Equal([stateChange], store.ReadStateChanges(id.ProcessorId, id.StreamId));
        Assert.Equal([activity], store.ReadActivityPeriods(id.ProcessorId, id.StreamId));
    }

    [Fact]
    public async Task ProcessorAndStreamLineagesAreIsolated()
    {
        var store = new InMemoryMachineStateActivityAuthorityStore();
        var machine = MachineId.New();
        var first = (
            new ObservationProcessorId("state-a"),
            new ObservationStreamId(machine, "MTConnect:CNC-01"));
        var otherStream = (
            first.Item1,
            new ObservationStreamId(machine, "MTConnect:CNC-02"));
        var otherProcessor = (
            new ObservationProcessorId("state-b"),
            first.Item2);

        await store.PublishAsync(Proposal(first, null, null, 10, MachineState.Running, 1));
        await store.PublishAsync(Proposal(otherStream, null, null, 20, MachineState.Fault, 2));
        await store.PublishAsync(Proposal(otherProcessor, null, null, 30, MachineState.Idle, 3));

        Assert.Equal(new ObservationPosition(10), (await store.ReadAsync(first.Item1, first.Item2))!.Projection.Position);
        Assert.Equal(new ObservationPosition(20), (await store.ReadAsync(otherStream.Item1, otherStream.Item2))!.Projection.Position);
        Assert.Equal(new ObservationPosition(30), (await store.ReadAsync(otherProcessor.Item1, otherProcessor.Item2))!.Projection.Position);
    }

    private static async Task AssertNotReplay(
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) id,
        MachineStateActivityAuthorityPublication baseline,
        MachineStateActivityAuthorityPublication changed)
    {
        var store = new InMemoryMachineStateActivityAuthorityStore();
        await store.PublishAsync(baseline);
        var result = await store.PublishAsync(changed);
        Assert.IsNotType<MachineStateActivityAuthorityPublicationAccepted>(result);
        Assert.IsType<MachineStateActivityAuthorityPublicationConflict>(result);
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
        DateTimeOffset? activeStartedAt = null) =>
        new(
            expectedPosition,
            expectedRevision,
            Projection(id, position, state, activeStartedAt),
            stateChanges ?? [],
            activityPeriods ?? [],
            new EvaluationAuthorityReplayIdentity(
                id.ProcessorId,
                id.StreamId,
                new ObservationPosition(position),
                state,
                instance,
                Policy()));

    private static MachineStateActivityProjection Projection(
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) id,
        ulong position,
        MachineState state,
        DateTimeOffset? activeStartedAt = null) =>
        new(
            id.ProcessorId,
            id.StreamId,
            new ObservationPosition(position),
            [],
            state,
            activeStartedAt.HasValue ? state : null,
            activeStartedAt);

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

    private static CurrentStatePolicyReference Policy() =>
        new("continuity/default", "1.0");

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

    private static readonly DateTimeOffset Stamp =
        new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
}
