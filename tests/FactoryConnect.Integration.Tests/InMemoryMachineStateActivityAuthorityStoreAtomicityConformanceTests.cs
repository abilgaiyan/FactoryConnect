using System.Collections;
using System.Reflection;
using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class InMemoryMachineStateActivityAuthorityStoreAtomicityConformanceTests
{
    [Fact]
    public async Task PreCancelledFreshPublicationLeavesLineageAbsent()
    {
        var faults = new FaultController();
        var store = new InMemoryMachineStateActivityAuthorityStore(faults.Visit);
        var id = Identity();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        faults.Armed = true;

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await store.PublishAsync(
                Proposal(id, null, null, 10, MachineState.Running, 1),
                cancellation.Token));

        Assert.Equal(0, faults.VisitCount);
        Assert.Null(await store.ReadAsync(id.ProcessorId, id.StreamId));
        Assert.Empty(store.ReadStateChanges(id.ProcessorId, id.StreamId));
        Assert.Empty(store.ReadActivityPeriods(id.ProcessorId, id.StreamId));
    }

    [Fact]
    public async Task PreCancelledEstablishedPublicationLeavesExactTupleAndRevisionUnchanged()
    {
        var faults = new FaultController();
        var store = new InMemoryMachineStateActivityAuthorityStore(faults.Visit);
        var id = Identity();
        await EstablishAsync(store, id);
        var before = await CaptureAsync(store, id);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        faults.Armed = true;

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await store.PublishAsync(ForwardProposal(id), cancellation.Token));

        Assert.Equal(0, faults.VisitCount);
        await AssertUnchangedAsync(store, id, before);
    }

    [Theory]
    [InlineData((int)MachineStateActivityAuthorityFaultPoint.BeforeNewPublicationMaterialization)]
    [InlineData((int)MachineStateActivityAuthorityFaultPoint.AfterAuthorityConstruction)]
    [InlineData((int)MachineStateActivityAuthorityFaultPoint.AfterPublicationOutputCopies)]
    [InlineData((int)MachineStateActivityAuthorityFaultPoint.AfterCumulativeHistoryConstruction)]
    [InlineData((int)MachineStateActivityAuthorityFaultPoint.ImmediatelyBeforeDictionaryAssignment)]
    public async Task EstablishedPreCommitFaultLeavesExactTupleAndDoesNotConsumeRevision(int pointValue)
    {
        var point = (MachineStateActivityAuthorityFaultPoint)pointValue;
        var faults = new FaultController { ThrowAt = point };
        var store = new InMemoryMachineStateActivityAuthorityStore(faults.Visit);
        var id = Identity();
        await EstablishAsync(store, id);
        var before = await CaptureAsync(store, id);
        faults.Armed = true;

        var exception = await Assert.ThrowsAsync<InjectedPublicationFaultException>(
            async () => await store.PublishAsync(ForwardProposal(id)));

        Assert.Equal(point, exception.Point);
        await AssertUnchangedAsync(store, id, before);

        faults.Armed = false;
        var accepted = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(ForwardProposal(id)));
        Assert.Equal(EvaluationAuthorityPublicationDisposition.NewPublication, accepted.Disposition);
        Assert.Equal(1UL, accepted.Snapshot.EvaluationAuthority.ProjectionRevision.Value);
    }

    [Fact]
    public async Task FreshLineagePreCommitFaultLeavesProjectionAuthorityAndHistoriesAbsent()
    {
        var faults = new FaultController
        {
            Armed = true,
            ThrowAt = MachineStateActivityAuthorityFaultPoint.ImmediatelyBeforeDictionaryAssignment,
        };
        var store = new InMemoryMachineStateActivityAuthorityStore(faults.Visit);
        var id = Identity();
        var stateChange = StateChange(id, 10, MachineState.Unknown, MachineState.Running);
        var activity = Activity(id, 10, MachineState.Running);

        await Assert.ThrowsAsync<InjectedPublicationFaultException>(
            async () => await store.PublishAsync(
                Proposal(
                    id,
                    null,
                    null,
                    10,
                    MachineState.Running,
                    1,
                    [stateChange],
                    [activity])));

        Assert.Null(await store.ReadAsync(id.ProcessorId, id.StreamId));
        Assert.Empty(store.ReadStateChanges(id.ProcessorId, id.StreamId));
        Assert.Empty(store.ReadActivityPeriods(id.ProcessorId, id.StreamId));

        faults.Armed = false;
        var accepted = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(
                Proposal(
                    id,
                    null,
                    null,
                    10,
                    MachineState.Running,
                    1,
                    [stateChange],
                    [activity])));
        Assert.Equal(0UL, accepted.Snapshot.EvaluationAuthority.ProjectionRevision.Value);
    }

    [Fact]
    public async Task ReplayConflictAndRevisionExhaustionBypassFaultMechanism()
    {
        var faults = new FaultController();
        var store = new InMemoryMachineStateActivityAuthorityStore(faults.Visit);
        var id = Identity();
        var baseline = Proposal(id, null, null, 10, MachineState.Running, 1);
        await store.PublishAsync(baseline);
        faults.Armed = true;

        var replay = Proposal(
            id,
            new ObservationPosition(999),
            new StateProjectionAuthorityRevision(999),
            10,
            MachineState.Running,
            1);
        var replayResult = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(replay));
        Assert.Equal(EvaluationAuthorityPublicationDisposition.ExactReplay, replayResult.Disposition);
        Assert.Equal(0, faults.VisitCount);

        var conflict = Proposal(
            id,
            new ObservationPosition(9),
            new StateProjectionAuthorityRevision(999),
            20,
            MachineState.Fault,
            2);
        Assert.IsType<MachineStateActivityAuthorityPublicationConflict>(
            await store.PublishAsync(conflict));
        Assert.Equal(0, faults.VisitCount);

        ForceRevision(store, ulong.MaxValue);
        var exhausted = Proposal(
            id,
            new ObservationPosition(10),
            new StateProjectionAuthorityRevision(ulong.MaxValue),
            20,
            MachineState.Fault,
            2);
        Assert.IsType<MachineStateActivityAuthorityRevisionExhausted>(
            await store.PublishAsync(exhausted));
        Assert.Equal(0, faults.VisitCount);
    }

    private static async Task EstablishAsync(
        InMemoryMachineStateActivityAuthorityStore store,
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) id)
    {
        var stateChange = StateChange(id, 10, MachineState.Unknown, MachineState.Running);
        var activity = Activity(id, 10, MachineState.Running);
        var accepted = Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(
                Proposal(
                    id,
                    null,
                    null,
                    10,
                    MachineState.Running,
                    1,
                    [stateChange],
                    [activity])));
        Assert.Equal(0UL, accepted.Snapshot.EvaluationAuthority.ProjectionRevision.Value);
    }

    private static MachineStateActivityAuthorityPublication ForwardProposal(
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) id)
    {
        var stateChange = StateChange(id, 20, MachineState.Running, MachineState.Fault);
        var activity = Activity(id, 20, MachineState.Running);
        return Proposal(
            id,
            new ObservationPosition(10),
            new StateProjectionAuthorityRevision(0),
            20,
            MachineState.Fault,
            2,
            [stateChange],
            [activity]);
    }

    private static async Task<AuthorityState> CaptureAsync(
        InMemoryMachineStateActivityAuthorityStore store,
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) id) =>
        new(
            (await store.ReadAsync(id.ProcessorId, id.StreamId))!,
            store.ReadStateChanges(id.ProcessorId, id.StreamId),
            store.ReadActivityPeriods(id.ProcessorId, id.StreamId));

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
        IReadOnlyList<DurableMachineActivityPeriod>? activityPeriods = null) =>
        new(
            expectedPosition,
            expectedRevision,
            Projection(id, position, state),
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
        MachineState state) =>
        new(
            id.ProcessorId,
            id.StreamId,
            new ObservationPosition(position),
            [],
            state,
            state,
            Stamp);

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

    private sealed class FaultController
    {
        public bool Armed { get; set; }

        public MachineStateActivityAuthorityFaultPoint? ThrowAt { get; set; }

        public int VisitCount { get; private set; }

        public void Visit(MachineStateActivityAuthorityFaultPoint point)
        {
            if (!Armed)
            {
                return;
            }

            VisitCount++;
            if (ThrowAt is null || ThrowAt == point)
            {
                throw new InjectedPublicationFaultException(point);
            }
        }
    }

    private sealed class InjectedPublicationFaultException(
        MachineStateActivityAuthorityFaultPoint point) : Exception
    {
        public MachineStateActivityAuthorityFaultPoint Point { get; } = point;
    }

    private sealed record AuthorityState(
        MachineStateActivityAuthoritySnapshot Snapshot,
        DurableMachineStateChangedEvent[] StateChanges,
        DurableMachineActivityPeriod[] ActivityPeriods);

    private static readonly DateTimeOffset Stamp =
        new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
}
