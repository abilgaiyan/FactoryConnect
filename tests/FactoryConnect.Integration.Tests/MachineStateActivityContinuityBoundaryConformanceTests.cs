using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class MachineStateActivityContinuityBoundaryConformanceTests
{
    private static readonly DateTimeOffset Stamp =
        new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PreserveClosesRetainedActivityAtNewInstanceObservationAndAttributesOutputToNewInstance()
    {
        var context = Context();
        var calculator = new MachineStateActivityContinuityCalculator(context.ProcessorId);
        var current = Snapshot(
            context,
            1,
            7,
            MachineState.Running,
            [Signal(CanonicalSignalKeys.Running, true, Stamp)],
            MachineState.Running,
            Stamp,
            PreservePolicy().Reference,
            revision: 4);
        var next = Observation(
            context,
            2,
            8,
            sequence: 222,
            CanonicalSignalKeys.Running,
            false,
            Stamp.AddSeconds(5));

        var publication = Publication(
            calculator.Calculate(current, [next], PreservePolicy()));

        Assert.Equal(MachineState.Stopped, publication.Projection.State);
        Assert.Equal(8UL, publication.EvaluationIdentity.LastConsumedInstanceId);

        var change = Assert.Single(publication.StateChanges);
        Assert.Equal(MachineState.Running, change.StateChanged.PreviousState);
        Assert.Equal(MachineState.Stopped, change.StateChanged.CurrentState);
        Assert.Equal(new ObservationPosition(2), change.Position);
        Assert.Equal(8UL, change.InstanceId);
        Assert.Equal(222UL, change.Sequence);

        var activity = Assert.Single(publication.ActivityPeriods);
        Assert.Equal(MachineState.Running, activity.Period.State);
        Assert.Equal(Stamp, activity.Period.StartedAt);
        Assert.Equal(Stamp.AddSeconds(5), activity.Period.EndedAt);
        Assert.Equal(new ObservationPosition(2), activity.Position);
        Assert.Equal(8UL, activity.InstanceId);
        Assert.Equal(222UL, activity.Sequence);
    }

    [Fact]
    public async Task PreservePartitionAtInstanceBoundaryPreservesSemanticStateAndCumulativeHistories()
    {
        var context = Context();
        var policy = PreservePolicy();
        var observations = new[]
        {
            Observation(
                context,
                1,
                7,
                sequence: 101,
                CanonicalSignalKeys.Running,
                true,
                Stamp),
            Observation(
                context,
                2,
                8,
                sequence: 202,
                CanonicalSignalKeys.Running,
                false,
                Stamp.AddSeconds(5)),
        };

        var oneStore = new InMemoryMachineStateActivityAuthorityStore();
        var oneCalculator = new MachineStateActivityContinuityCalculator(context.ProcessorId);
        await Publish(oneStore, Publication(oneCalculator.Calculate(null, observations, policy)));

        var splitStore = new InMemoryMachineStateActivityAuthorityStore();
        var splitCalculator = new MachineStateActivityContinuityCalculator(context.ProcessorId);
        await Publish(splitStore, Publication(splitCalculator.Calculate(null, [observations[0]], policy)));
        var splitCurrent = await splitStore.ReadAsync(context.ProcessorId, context.StreamId);
        await Publish(
            splitStore,
            Publication(splitCalculator.Calculate(splitCurrent, [observations[1]], policy)));

        await AssertSemanticConvergence(oneStore, splitStore, context);

        var single = (await oneStore.ReadAsync(context.ProcessorId, context.StreamId))!;
        var split = (await splitStore.ReadAsync(context.ProcessorId, context.StreamId))!;
        Assert.NotEqual(
            single.EvaluationAuthority.ProjectionRevision,
            split.EvaluationAuthority.ProjectionRevision);

        var singleActivity = Assert.Single(oneStore.ReadActivityPeriods(context.ProcessorId, context.StreamId));
        var splitActivity = Assert.Single(splitStore.ReadActivityPeriods(context.ProcessorId, context.StreamId));
        Assert.Equal(singleActivity, splitActivity);
        Assert.Equal(8UL, singleActivity.InstanceId);
        Assert.Equal(202UL, singleActivity.Sequence);
        Assert.Equal(Stamp, singleActivity.Period.StartedAt);
        Assert.Equal(Stamp.AddSeconds(5), singleActivity.Period.EndedAt);
    }

    [Fact]
    public async Task ResetPartitionAtInstanceBoundaryAbandonsOldContextExactlyOnceAndConvergesSemantically()
    {
        var context = Context();
        var policy = ResetPolicy();
        var observations = new[]
        {
            Observation(
                context,
                1,
                7,
                sequence: 101,
                CanonicalSignalKeys.Running,
                true,
                Stamp),
            Observation(
                context,
                2,
                8,
                sequence: 202,
                CanonicalSignalKeys.Idle,
                true,
                Stamp.AddSeconds(5)),
        };

        var oneStore = new InMemoryMachineStateActivityAuthorityStore();
        var oneCalculator = new MachineStateActivityContinuityCalculator(context.ProcessorId);
        await Publish(oneStore, Publication(oneCalculator.Calculate(null, observations, policy)));

        var splitStore = new InMemoryMachineStateActivityAuthorityStore();
        var splitCalculator = new MachineStateActivityContinuityCalculator(context.ProcessorId);
        await Publish(splitStore, Publication(splitCalculator.Calculate(null, [observations[0]], policy)));
        var splitCurrent = await splitStore.ReadAsync(context.ProcessorId, context.StreamId);
        await Publish(
            splitStore,
            Publication(splitCalculator.Calculate(splitCurrent, [observations[1]], policy)));

        await AssertSemanticConvergence(oneStore, splitStore, context);

        var single = (await oneStore.ReadAsync(context.ProcessorId, context.StreamId))!;
        var split = (await splitStore.ReadAsync(context.ProcessorId, context.StreamId))!;
        Assert.NotEqual(
            single.EvaluationAuthority.ProjectionRevision,
            split.EvaluationAuthority.ProjectionRevision);

        Assert.Equal(MachineState.Idle, single.Projection.State);
        Assert.Single(single.Projection.Signals);
        Assert.Equal(CanonicalSignalKeys.Idle, single.Projection.Signals[0].Key);
        Assert.Equal(8UL, single.EvaluationAuthority.LastConsumedInstanceId);
        Assert.Equal(policy.Reference, single.EvaluationAuthority.AppliedContinuityPolicy);
        Assert.Empty(oneStore.ReadActivityPeriods(context.ProcessorId, context.StreamId));
        Assert.Empty(splitStore.ReadActivityPeriods(context.ProcessorId, context.StreamId));

        var stateChanges = oneStore.ReadStateChanges(context.ProcessorId, context.StreamId);
        Assert.Equal(2, stateChanges.Count);
        Assert.Equal(MachineState.Unknown, stateChanges[0].StateChanged.PreviousState);
        Assert.Equal(MachineState.Running, stateChanges[0].StateChanged.CurrentState);
        Assert.Equal(MachineState.Unknown, stateChanges[1].StateChanged.PreviousState);
        Assert.Equal(MachineState.Idle, stateChanges[1].StateChanged.CurrentState);
        Assert.Equal(8UL, stateChanges[1].InstanceId);
        Assert.Equal(202UL, stateChanges[1].Sequence);
    }

    private static async Task AssertSemanticConvergence(
        InMemoryMachineStateActivityAuthorityStore oneStore,
        InMemoryMachineStateActivityAuthorityStore splitStore,
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) context)
    {
        var one = (await oneStore.ReadAsync(context.ProcessorId, context.StreamId))!;
        var split = (await splitStore.ReadAsync(context.ProcessorId, context.StreamId))!;

        Assert.Equal(one.Projection.Signals, split.Projection.Signals);
        Assert.Equal(one.Projection.State, split.Projection.State);
        Assert.Equal(one.Projection.ActiveState, split.Projection.ActiveState);
        Assert.Equal(one.Projection.ActiveStartedAt, split.Projection.ActiveStartedAt);
        Assert.Equal(one.EvaluationAuthority.EvaluatedThrough, split.EvaluationAuthority.EvaluatedThrough);
        Assert.Equal(one.EvaluationAuthority.MachineState, split.EvaluationAuthority.MachineState);
        Assert.Equal(
            one.EvaluationAuthority.LastConsumedInstanceId,
            split.EvaluationAuthority.LastConsumedInstanceId);
        Assert.Equal(
            one.EvaluationAuthority.AppliedContinuityPolicy,
            split.EvaluationAuthority.AppliedContinuityPolicy);
        Assert.Equal(
            oneStore.ReadStateChanges(context.ProcessorId, context.StreamId),
            splitStore.ReadStateChanges(context.ProcessorId, context.StreamId));
        Assert.Equal(
            oneStore.ReadActivityPeriods(context.ProcessorId, context.StreamId),
            splitStore.ReadActivityPeriods(context.ProcessorId, context.StreamId));
    }

    private static MachineStateActivityAuthorityPublication Publication(
        MachineStateActivityContinuityCalculationResult result) =>
        Assert.IsType<MachineStateActivityContinuityPublication>(result).Publication;

    private static async Task Publish(
        InMemoryMachineStateActivityAuthorityStore store,
        MachineStateActivityAuthorityPublication publication) =>
        Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(publication));

    private static MachineStateActivityAuthoritySnapshot Snapshot(
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) context,
        ulong position,
        ulong instanceId,
        MachineState state,
        IReadOnlyList<MachineSignalValue> signals,
        MachineState? activeState,
        DateTimeOffset? activeStartedAt,
        CurrentStatePolicyReference policy,
        ulong revision) =>
        new(
            new MachineStateActivityProjection(
                context.ProcessorId,
                context.StreamId,
                new ObservationPosition(position),
                signals.OrderBy(signal => signal.Key, StringComparer.OrdinalIgnoreCase).ToArray(),
                state,
                activeState,
                activeStartedAt),
            new EvaluationAuthority(
                context.ProcessorId,
                context.StreamId,
                new ObservationPosition(position),
                state,
                instanceId,
                policy,
                new StateProjectionAuthorityRevision(revision)));

    private static DurableMappedMachineObservation Observation(
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) context,
        ulong position,
        ulong instanceId,
        ulong sequence,
        string key,
        bool value,
        DateTimeOffset timestamp) =>
        new(
            new ObservationPosition(position),
            context.StreamId,
            instanceId,
            sequence,
            new MappedMachineObservation
            {
                MachineId = context.StreamId.MachineId,
                SignalKey = key,
                Type = SignalType.Digital,
                Value = value,
                Source = "continuity-boundary-proof",
                Address = key,
                Quality = ObservationQuality.Good,
                Timestamp = timestamp,
            });

    private static MachineSignalValue Signal(
        string key,
        bool value,
        DateTimeOffset timestamp) =>
        new()
        {
            Key = key,
            Type = SignalType.Digital,
            Value = value,
            Source = "continuity-boundary-proof",
            Quality = ObservationQuality.Good,
            Timestamp = timestamp,
        };

    private static CurrentStateContinuityPolicy PreservePolicy() =>
        new(
            new CurrentStatePolicyReference("continuity/preserve", "1.0"),
            StateContinuityMode.Preserve);

    private static CurrentStateContinuityPolicy ResetPolicy() =>
        new(
            new CurrentStatePolicyReference("continuity/reset", "1.0"),
            StateContinuityMode.Reset);

    private static (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) Context()
    {
        var machineId = MachineId.New();
        return (
            new ObservationProcessorId("state-continuity-boundary"),
            new ObservationStreamId(machineId, "MTConnect:CNC-01"));
    }
}
