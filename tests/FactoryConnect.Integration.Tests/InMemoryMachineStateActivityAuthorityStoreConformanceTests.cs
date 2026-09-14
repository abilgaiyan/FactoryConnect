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
    public void IntrinsicValidationRejectsCrossComponentMismatchAndUnorderedOrDuplicateCollections()
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

        var duplicateStateChange = StateChange(id, 20, MachineState.Unknown, MachineState.Running);
        Assert.Throws<ArgumentException>(() =>
            Proposal(
                id,
                null,
                null,
                20,
                MachineState.Running,
                7,
                [duplicateStateChange, duplicateStateChange]));

        var laterStateChange = StateChange(id, 20, MachineState.Idle, MachineState.Running);
        var earlierStateChange = StateChange(id, 19, MachineState.Unknown, MachineState.Idle);
        Assert.Throws<ArgumentException>(() =>
            Proposal(
                id,
                null,
                null,
                20,
                MachineState.Running,
                7,
                [laterStateChange, earlierStateChange]));

        var duplicateActivity = Activity(id, 20, MachineState.Running);
        Assert.Throws<ArgumentException>(() =>
            Proposal(
                id,
                null,
                null,
                20,
                MachineState.Running,
                7,
                activityPeriods: [duplicateActivity, duplicateActivity]));

        var laterActivity = Activity(id, 20, MachineState.Running);
        var earlierActivity = Activity(id, 19, MachineState.Idle);
        Assert.Throws<ArgumentException>(() =>
            Proposal(
                id,
                null,
                null,
                20,
                MachineState.Running,
                7,
                activityPeriods: [laterActivity, earlierActivity]));

        var signalA = Signal("a", true);
        var signalB = Signal("b", false);
        Assert.Throws<ArgumentException>(() =>
            Proposal(
                id,
                null,
                null,
                20,
                MachineState.Running,
                7,
                signals: [signalB, signalA]));
        Assert.Throws<ArgumentException>(() =>
            Proposal(
                id,
                null,
                null,
                20,
                MachineState.Running,
                7,
                signals: [signalA, Signal("A", false)]));
    }

    [Fact]
    public async Task ProjectionSignalAndActiveContextDifferencesAreNotReplay()
    {
        var id = Identity();
        var signal = Signal("spindle", true);
        var changedSignal = Signal("spindle", false);

        await AssertNotReplay(
            Proposal(id, null, null, 20, MachineState.Running, 7, signals: [signal]),
            Proposal(id, null, null, 20, MachineState.Running, 7, signals: [changedSignal]));

        await AssertNotReplay(
            Proposal(
                id,
                null,
                null,
                20,
                MachineState.Running,
                7,
                activeState: MachineState.Running,
                activeStartedAt: Stamp),
            Proposal(
                id,
                null,
                null,
                20,
                MachineState.Running,
                7,
                activeState: MachineState.Idle,
                activeStartedAt: Stamp));

        await AssertNotReplay(
            Proposal(
                id,
                null,
                null,
                20,
                MachineState.Running,
                7,
                activeState: MachineState.Running,
                activeStartedAt: Stamp),
            Proposal(
                id,
                null,
                null,
                20,
                MachineState.Running,
                7,
                activeState: MachineState.Running,
                activeStartedAt: Stamp.AddSeconds(1)));
    }

    [Fact]
    public async Task StateChangePayloadDifferencesIncludingAdditionalOutputAreNotReplay()
    {
        var id = Identity();
        var first = StateChange(id, 19, MachineState.Unknown, MachineState.Idle);
        var second = StateChange(id, 20, MachineState.Idle, MachineState.Running);
        var changedSecond = StateChange(id, 20, MachineState.Idle, MachineState.Fault);

        await AssertNotReplay(
            Proposal(id, null, null, 20, MachineState.Running, 7, [first, second]),
            Proposal(id, null, null, 20, MachineState.Running, 7, [first, changedSecond]));

        await AssertNotReplay(
            Proposal(id, null, null, 20, MachineState.Running, 7, [first]),
            Proposal(id, null, null, 20, MachineState.Running, 7, [first, second]));
    }

    [Fact]
    public async Task ActivityPayloadDifferencesIncludingAdditionalOutputAreNotReplay()
    {
        var id = Identity();
        var first = Activity(id, 19, MachineState.Idle);
        var second = Activity(id, 20, MachineState.Running);
        var changedSecond = Activity(id, 20, MachineState.Fault);

        await AssertNotReplay(
            Proposal(id, null, null, 20, MachineState.Running, 7, activityPeriods: [first, second]),
            Proposal(id, null, null, 20, MachineState.Running, 7, activityPeriods: [first, changedSecond]));

        await AssertNotReplay(
            Proposal(id, null, null, 20, MachineState.Running, 7, activityPeriods: [first]),
            Proposal(id, null, null, 20, MachineState.Running, 7, activityPeriods: [first, second]));
    }

    [Fact]
    public async Task MissingOutputsAndAuthorityIdentityDifferencesAreNotReplay()
    {
        var id = Identity();
        var stateChange = StateChange(id, 20, MachineState.Unknown, MachineState.Running);
        var activity = Activity(id, 20, MachineState.Running);
        var baseline = Proposal(
            id,
            null,
            null,
            20,
            MachineState.Running,
            7,
            [stateChange],
            [activity]);

        await AssertNotReplay(
            baseline,
            Proposal(id, null, null, 20, MachineState.Running, 7, [], [activity]));
        await AssertNotReplay(
            baseline,
            Proposal(id, null, null, 20, MachineState.Running, 7, [stateChange], []));
        await AssertNotReplay(
            baseline,
            Proposal(id, null, null, 20, MachineState.Running, 8, [stateChange], [activity]));
        await AssertNotReplay(
            baseline,
            Proposal(
                id,
                null,
                null,
                20,
                MachineState.Running,
                7,
                [stateChange],
                [activity],
                policy: new CurrentStatePolicyReference("continuity/reset", "1.0")));
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
        IReadOnlyList<MachineSignalValue>? signals = null,
        MachineState? activeState = null,
        DateTimeOffset? activeStartedAt = null,
        CurrentStatePolicyReference? policy = null) =>
        new(
            expectedPosition,
            expectedRevision,
            Projection(id, position, state, signals, activeState, activeStartedAt),
            stateChanges ?? [],
            activityPeriods ?? [],
            new EvaluationAuthorityReplayIdentity(
                id.ProcessorId,
                id.StreamId,
                new ObservationPosition(position),
                state,
                instance,
                policy ?? Policy()));

    private static MachineStateActivityProjection Projection(
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) id,
        ulong position,
        MachineState state,
        IReadOnlyList<MachineSignalValue>? signals = null,
        MachineState? activeState = null,
        DateTimeOffset? activeStartedAt = null) =>
        new(
            id.ProcessorId,
            id.StreamId,
            new ObservationPosition(position),
            signals ?? [],
            state,
            activeState,
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
