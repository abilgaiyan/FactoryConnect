using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class MachineStateActivityContinuityBehavioralConformanceTests
{
    private static readonly DateTimeOffset Stamp =
        new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FreshBootstrapBuildsDeterministicJointPublication()
    {
        var context = Context();
        var calculator = new MachineStateActivityContinuityCalculator(context.ProcessorId);

        var result = Publication(
            calculator.Calculate(
                null,
                [Observation(context, 1, 7, CanonicalSignalKeys.Running, true, Stamp)],
                PreservePolicy()));

        Assert.Null(result.ExpectedProjectionPosition);
        Assert.Null(result.ExpectedAuthorityRevision);
        Assert.Equal(new ObservationPosition(1), result.Projection.Position);
        Assert.Equal(MachineState.Running, result.Projection.State);
        Assert.Equal(7UL, result.EvaluationIdentity.LastConsumedInstanceId);
        Assert.Equal(PreservePolicy().Reference, result.EvaluationIdentity.AppliedContinuityPolicy);
        var change = Assert.Single(result.StateChanges);
        Assert.Equal(MachineState.Unknown, change.StateChanged.PreviousState);
        Assert.Equal(MachineState.Running, change.StateChanged.CurrentState);
        Assert.Empty(result.ActivityPeriods);
    }

    [Fact]
    public void SameInstanceRestoresProjectionContextAndContinuesActivity()
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
            Stamp);

        var result = Publication(
            calculator.Calculate(
                current,
                [Observation(context, 2, 7, CanonicalSignalKeys.Running, false, Stamp.AddSeconds(1))],
                PreservePolicy()));

        Assert.Equal(MachineState.Stopped, result.Projection.State);
        Assert.Equal(7UL, result.EvaluationIdentity.LastConsumedInstanceId);
        var change = Assert.Single(result.StateChanges);
        Assert.Equal(MachineState.Running, change.StateChanged.PreviousState);
        Assert.Equal(MachineState.Stopped, change.StateChanged.CurrentState);
        var period = Assert.Single(result.ActivityPeriods);
        Assert.Equal(MachineState.Running, period.Period.State);
        Assert.Equal(Stamp, period.Period.StartedAt);
        Assert.Equal(Stamp.AddSeconds(1), period.Period.EndedAt);
    }

    [Fact]
    public void PreserveKeepsPriorSignalsAcrossInstanceBoundary()
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
            Stamp);

        var result = Publication(
            calculator.Calculate(
                current,
                [Observation(context, 2, 8, CanonicalSignalKeys.Idle, true, Stamp.AddSeconds(1))],
                PreservePolicy()));

        Assert.Equal(MachineState.Running, result.Projection.State);
        Assert.Equal(2, result.Projection.Signals.Count);
        Assert.Empty(result.StateChanges);
        Assert.Empty(result.ActivityPeriods);
        Assert.Equal(8UL, result.EvaluationIdentity.LastConsumedInstanceId);
    }

    [Fact]
    public void ResetClearsPriorContextWithoutClosingOldActivity()
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
            Stamp);

        var result = Publication(
            calculator.Calculate(
                current,
                [Observation(context, 2, 8, CanonicalSignalKeys.Idle, true, Stamp.AddSeconds(1))],
                ResetPolicy()));

        Assert.Equal(MachineState.Idle, result.Projection.State);
        Assert.Single(result.Projection.Signals);
        var change = Assert.Single(result.StateChanges);
        Assert.Equal(MachineState.Unknown, change.StateChanged.PreviousState);
        Assert.Equal(MachineState.Idle, change.StateChanged.CurrentState);
        Assert.Empty(result.ActivityPeriods);
        Assert.Equal(MachineState.Idle, result.Projection.ActiveState);
        Assert.Equal(Stamp.AddSeconds(1), result.Projection.ActiveStartedAt);
        Assert.Equal(8UL, result.EvaluationIdentity.LastConsumedInstanceId);
        Assert.Equal(ResetPolicy().Reference, result.EvaluationIdentity.AppliedContinuityPolicy);
    }

    [Fact]
    public void ResetToUnknownProducesNoSyntheticTransition()
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
            Stamp);

        var result = Publication(
            calculator.Calculate(
                current,
                [Observation(context, 2, 8, "part-count", true, Stamp.AddSeconds(1))],
                ResetPolicy()));

        Assert.Equal(MachineState.Unknown, result.Projection.State);
        Assert.Null(result.Projection.ActiveState);
        Assert.Null(result.Projection.ActiveStartedAt);
        Assert.Empty(result.StateChanges);
        Assert.Empty(result.ActivityPeriods);
    }

    [Fact]
    public void MultipleResetBoundariesApplyPolicyPerForwardObservation()
    {
        var context = Context();
        var calculator = new MachineStateActivityContinuityCalculator(context.ProcessorId);

        var result = Publication(
            calculator.Calculate(
                null,
                [
                    Observation(context, 1, 7, CanonicalSignalKeys.Running, true, Stamp),
                    Observation(context, 2, 8, CanonicalSignalKeys.Idle, true, Stamp.AddSeconds(1)),
                    Observation(context, 3, 9, CanonicalSignalKeys.Fault, true, Stamp.AddSeconds(2)),
                ],
                ResetPolicy()));

        Assert.Equal(MachineState.Fault, result.Projection.State);
        Assert.Equal(9UL, result.EvaluationIdentity.LastConsumedInstanceId);
        Assert.Equal(3, result.StateChanges.Count);
        Assert.All(
            result.StateChanges,
            change => Assert.Equal(MachineState.Unknown, change.StateChanged.PreviousState));
        Assert.Empty(result.ActivityPeriods);
    }

    [Fact]
    public void FullOverlapIsNoAdvanceAndPartialOverlapEvaluatesOnlyForwardSuffix()
    {
        var context = Context();
        var calculator = new MachineStateActivityContinuityCalculator(context.ProcessorId);
        var current = Snapshot(
            context,
            2,
            7,
            MachineState.Running,
            [Signal(CanonicalSignalKeys.Running, true, Stamp)],
            MachineState.Running,
            Stamp);

        var noAdvance = calculator.Calculate(
            current,
            [
                Observation(context, 1, 99, CanonicalSignalKeys.Fault, true, Stamp),
                Observation(context, 2, 99, CanonicalSignalKeys.Fault, true, Stamp.AddSeconds(1)),
            ],
            ResetPolicy());
        Assert.IsType<MachineStateActivityContinuityNoAdvance>(noAdvance);

        var forward = Publication(
            calculator.Calculate(
                current,
                [
                    Observation(context, 1, 99, CanonicalSignalKeys.Fault, true, Stamp),
                    Observation(context, 2, 99, CanonicalSignalKeys.Fault, true, Stamp.AddSeconds(1)),
                    Observation(context, 3, 8, CanonicalSignalKeys.Idle, true, Stamp.AddSeconds(2)),
                ],
                ResetPolicy()));

        Assert.Equal(new ObservationPosition(2), forward.ExpectedProjectionPosition);
        Assert.Equal(new StateProjectionAuthorityRevision(4), forward.ExpectedAuthorityRevision);
        Assert.Equal(new ObservationPosition(3), forward.Projection.Position);
        Assert.Equal(8UL, forward.EvaluationIdentity.LastConsumedInstanceId);
        Assert.DoesNotContain(
            forward.Projection.Signals,
            signal => string.Equals(signal.Key, CanonicalSignalKeys.Fault, StringComparison.OrdinalIgnoreCase));
        Assert.All(forward.StateChanges, change => Assert.Equal(new ObservationPosition(3), change.Position));
    }

    [Fact]
    public async Task BatchPartitioningPreservesSemanticStateAndCumulativeHistoriesButNotRevision()
    {
        var context = Context();
        var policy = PreservePolicy();
        var observations = new[]
        {
            Observation(context, 1, 7, CanonicalSignalKeys.Running, true, Stamp),
            Observation(context, 2, 7, CanonicalSignalKeys.Running, false, Stamp.AddSeconds(1)),
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

        var one = (await oneStore.ReadAsync(context.ProcessorId, context.StreamId))!;
        var split = (await splitStore.ReadAsync(context.ProcessorId, context.StreamId))!;

        Assert.Equal(one.Projection.Signals, split.Projection.Signals);
        Assert.Equal(one.Projection.State, split.Projection.State);
        Assert.Equal(one.Projection.ActiveState, split.Projection.ActiveState);
        Assert.Equal(one.Projection.ActiveStartedAt, split.Projection.ActiveStartedAt);
        Assert.Equal(one.EvaluationAuthority.EvaluatedThrough, split.EvaluationAuthority.EvaluatedThrough);
        Assert.Equal(one.EvaluationAuthority.MachineState, split.EvaluationAuthority.MachineState);
        Assert.Equal(one.EvaluationAuthority.LastConsumedInstanceId, split.EvaluationAuthority.LastConsumedInstanceId);
        Assert.Equal(one.EvaluationAuthority.AppliedContinuityPolicy, split.EvaluationAuthority.AppliedContinuityPolicy);
        Assert.Equal(
            oneStore.ReadStateChanges(context.ProcessorId, context.StreamId),
            splitStore.ReadStateChanges(context.ProcessorId, context.StreamId));
        Assert.Equal(
            oneStore.ReadActivityPeriods(context.ProcessorId, context.StreamId),
            splitStore.ReadActivityPeriods(context.ProcessorId, context.StreamId));
        Assert.NotEqual(
            one.EvaluationAuthority.ProjectionRevision,
            split.EvaluationAuthority.ProjectionRevision);
    }

    [Fact]
    public async Task UncomposedProcessorMapsProviderResultsWithoutRetryAndSkipsPublishOnNoAdvance()
    {
        var context = Context();
        var observation = Observation(
            context,
            2,
            7,
            CanonicalSignalKeys.Running,
            false,
            Stamp.AddSeconds(1));

        foreach (var disposition in new[]
                 {
                     EvaluationAuthorityPublicationDisposition.NewPublication,
                     EvaluationAuthorityPublicationDisposition.ExactReplay,
                 })
        {
            var store = new ResultStore(
                null,
                publication => new MachineStateActivityAuthorityPublicationAccepted(
                    SnapshotFrom(publication, 0),
                    disposition));
            var processor = new JointAuthorityMachineStateActivityProcessor(
                context.ProcessorId,
                store,
                PreservePolicy());

            await processor.ProcessAsync([observation]);
            Assert.Equal(1, store.PublishCount);
        }

        var current = Snapshot(
            context,
            2,
            7,
            MachineState.Running,
            [Signal(CanonicalSignalKeys.Running, true, Stamp)],
            MachineState.Running,
            Stamp);
        var noAdvanceStore = new ResultStore(
            current,
            _ => throw new InvalidOperationException("Publish must not be called for NoAdvance."));
        var noAdvanceProcessor = new JointAuthorityMachineStateActivityProcessor(
            context.ProcessorId,
            noAdvanceStore,
            PreservePolicy());
        await noAdvanceProcessor.ProcessAsync([observation]);
        Assert.Equal(0, noAdvanceStore.PublishCount);

        var conflictStore = new ResultStore(
            null,
            _ => new MachineStateActivityAuthorityPublicationConflict(null));
        var conflictProcessor = new JointAuthorityMachineStateActivityProcessor(
            context.ProcessorId,
            conflictStore,
            PreservePolicy());
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await conflictProcessor.ProcessAsync([observation]));
        Assert.Equal(1, conflictStore.PublishCount);

        var exhaustedCurrent = Snapshot(
            context,
            1,
            7,
            MachineState.Running,
            [Signal(CanonicalSignalKeys.Running, true, Stamp)],
            MachineState.Running,
            Stamp,
            ulong.MaxValue);
        var exhaustedStore = new ResultStore(
            exhaustedCurrent,
            _ => new MachineStateActivityAuthorityRevisionExhausted(exhaustedCurrent));
        var exhaustedProcessor = new JointAuthorityMachineStateActivityProcessor(
            context.ProcessorId,
            exhaustedStore,
            PreservePolicy());
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await exhaustedProcessor.ProcessAsync([observation]));
        Assert.Equal(1, exhaustedStore.PublishCount);
    }

    private static MachineStateActivityAuthorityPublication Publication(
        MachineStateActivityContinuityCalculationResult result) =>
        Assert.IsType<MachineStateActivityContinuityPublication>(result).Publication;

    private static async Task Publish(
        InMemoryMachineStateActivityAuthorityStore store,
        MachineStateActivityAuthorityPublication publication) =>
        Assert.IsType<MachineStateActivityAuthorityPublicationAccepted>(
            await store.PublishAsync(publication));

    private static MachineStateActivityAuthoritySnapshot SnapshotFrom(
        MachineStateActivityAuthorityPublication publication,
        ulong revision) =>
        new(
            publication.Projection,
            new EvaluationAuthority(
                publication.EvaluationIdentity.StateProcessorId,
                publication.EvaluationIdentity.ObservationStreamId,
                publication.EvaluationIdentity.EvaluatedThrough,
                publication.EvaluationIdentity.MachineState,
                publication.EvaluationIdentity.LastConsumedInstanceId,
                publication.EvaluationIdentity.AppliedContinuityPolicy,
                new StateProjectionAuthorityRevision(revision)));

    private static MachineStateActivityAuthoritySnapshot Snapshot(
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) context,
        ulong position,
        ulong instanceId,
        MachineState state,
        IReadOnlyList<MachineSignalValue> signals,
        MachineState? activeState,
        DateTimeOffset? activeStartedAt,
        ulong revision = 4) =>
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
                PreservePolicy().Reference,
                new StateProjectionAuthorityRevision(revision)));

    private static DurableMappedMachineObservation Observation(
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) context,
        ulong position,
        ulong instanceId,
        string key,
        bool value,
        DateTimeOffset timestamp) =>
        new(
            new ObservationPosition(position),
            context.StreamId,
            instanceId,
            position,
            new MappedMachineObservation
            {
                MachineId = context.StreamId.MachineId,
                SignalKey = key,
                Type = SignalType.Digital,
                Value = value,
                Source = "behavioral-proof",
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
            Source = "behavioral-proof",
            Quality = ObservationQuality.Good,
            Timestamp = timestamp,
        };

    private static CurrentStateContinuityPolicy PreservePolicy() =>
        new(new CurrentStatePolicyReference("continuity/preserve", "1.0"), StateContinuityMode.Preserve);

    private static CurrentStateContinuityPolicy ResetPolicy() =>
        new(new CurrentStatePolicyReference("continuity/reset", "1.0"), StateContinuityMode.Reset);

    private static (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) Context()
    {
        var machineId = MachineId.New();
        return (
            new ObservationProcessorId("state-behavioral"),
            new ObservationStreamId(machineId, "MTConnect:CNC-01"));
    }

    private sealed class ResultStore : IMachineStateActivityAuthorityStore
    {
        private readonly MachineStateActivityAuthoritySnapshot? _current;
        private readonly Func<MachineStateActivityAuthorityPublication, MachineStateActivityAuthorityPublicationResult> _publish;

        public ResultStore(
            MachineStateActivityAuthoritySnapshot? current,
            Func<MachineStateActivityAuthorityPublication, MachineStateActivityAuthorityPublicationResult> publish)
        {
            _current = current;
            _publish = publish;
        }

        public int PublishCount { get; private set; }

        public ValueTask<MachineStateActivityAuthoritySnapshot?> ReadAsync(
            ObservationProcessorId stateProcessorId,
            ObservationStreamId observationStreamId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_current);
        }

        public ValueTask<MachineStateActivityAuthorityPublicationResult> PublishAsync(
            MachineStateActivityAuthorityPublication publication,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishCount++;
            return ValueTask.FromResult(_publish(publication));
        }
    }
}
