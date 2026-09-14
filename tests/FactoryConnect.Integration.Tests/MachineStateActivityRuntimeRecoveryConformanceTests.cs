using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Machines;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class MachineStateActivityRuntimeRecoveryConformanceTests
{
    private static readonly DateTimeOffset Stamp =
        new(2026, 9, 14, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task F01_FreshBootstrapPublishesCoherentJointAuthority()
    {
        var context = Context();
        var store = new InMemoryMachineStateActivityAuthorityStore();
        var mapped = new InMemoryMappedMachineObservationSink();
        await mapped.WriteAsync([Observation(context, 1, 7, 101, true, Stamp)]);
        var runtime = Runtime(mapped, store, store, context, batchSize: 10);

        var batch = await runtime.RunCycleAsync();

        Assert.Single(batch.Observations);
        var snapshot = await store.ReadAsync(context.ProcessorId, context.StreamId);
        Assert.NotNull(snapshot);
        Assert.Equal(new ObservationPosition(1), snapshot.Projection.Position);
        Assert.Equal(MachineState.Running, snapshot.Projection.State);
        Assert.Equal(snapshot.Projection.Position, snapshot.EvaluationAuthority.EvaluatedThrough);
        Assert.Equal(snapshot.Projection.State, snapshot.EvaluationAuthority.MachineState);
        Assert.Equal(7UL, snapshot.EvaluationAuthority.LastConsumedInstanceId);
        Assert.Equal(
            CanonicalCurrentStateContinuityPolicies.Preserve.Reference,
            snapshot.EvaluationAuthority.AppliedContinuityPolicy);
        Assert.Equal(0UL, snapshot.EvaluationAuthority.ProjectionRevision.Value);
        Assert.Single(store.ReadStateChanges(context.ProcessorId, context.StreamId));
        Assert.Empty(store.ReadActivityPeriods(context.ProcessorId, context.StreamId));
    }

    [Fact]
    public async Task F02_F05_F06_RestartResumesAfterJointPositionAndPreservesContinuityContext()
    {
        var context = Context();
        var store = new InMemoryMachineStateActivityAuthorityStore();
        var mapped = new InMemoryMappedMachineObservationSink();
        await mapped.WriteAsync([Observation(context, 1, 7, 101, true, Stamp)]);
        await Runtime(mapped, store, store, context, batchSize: 10).RunCycleAsync();

        await mapped.WriteAsync(
            [Observation(context, 2, 8, 202, false, Stamp.AddSeconds(5))]);
        var trackingReader = new TrackingMappedObservationReader(mapped);
        var restarted = Runtime(trackingReader, store, store, context, batchSize: 10);

        var batch = await restarted.RunCycleAsync();

        Assert.Equal(new ObservationPosition(1), trackingReader.LastRequest!.AfterPosition);
        var forward = Assert.Single(batch.Observations);
        Assert.Equal(new ObservationPosition(2), forward.Position);
        var snapshot = await store.ReadAsync(context.ProcessorId, context.StreamId);
        Assert.NotNull(snapshot);
        Assert.Equal(new ObservationPosition(2), snapshot.Projection.Position);
        Assert.Equal(MachineState.Stopped, snapshot.Projection.State);
        Assert.Equal(8UL, snapshot.EvaluationAuthority.LastConsumedInstanceId);
        Assert.Equal(
            CanonicalCurrentStateContinuityPolicies.Preserve.Reference,
            snapshot.EvaluationAuthority.AppliedContinuityPolicy);
        Assert.Equal("continuity/preserve", snapshot.EvaluationAuthority.AppliedContinuityPolicy.Identity);
        Assert.Equal("1.0", snapshot.EvaluationAuthority.AppliedContinuityPolicy.Version);
        Assert.Equal(1UL, snapshot.EvaluationAuthority.ProjectionRevision.Value);

        var activity = Assert.Single(
            store.ReadActivityPeriods(context.ProcessorId, context.StreamId));
        Assert.Equal(MachineState.Running, activity.Period.State);
        Assert.Equal(Stamp, activity.Period.StartedAt);
        Assert.Equal(Stamp.AddSeconds(5), activity.Period.EndedAt);
        Assert.Equal(8UL, activity.InstanceId);
        Assert.Equal(202UL, activity.Sequence);
    }

    [Fact]
    public async Task F03_F04_LostAcknowledgementRecoverySkipsCommittedPrefixAndOnlyPublishesForwardWork()
    {
        var context = Context();
        var inner = new InMemoryMachineStateActivityAuthorityStore();
        var lostAck = new LoseNextNewPublicationAcknowledgementStore(inner);
        var mapped = new InMemoryMappedMachineObservationSink();
        await mapped.WriteAsync([Observation(context, 1, 7, 101, true, Stamp)]);
        var first = Runtime(mapped, inner, lostAck, context, batchSize: 1);

        await Assert.ThrowsAsync<LostAcknowledgementException>(
            async () => await first.RunCycleAsync());

        var committed = await inner.ReadAsync(context.ProcessorId, context.StreamId);
        Assert.NotNull(committed);
        Assert.Equal(new ObservationPosition(1), committed.Projection.Position);
        Assert.Equal(0UL, committed.EvaluationAuthority.ProjectionRevision.Value);
        Assert.Equal(1, lostAck.PublishCount);

        var noNewerReader = new TrackingMappedObservationReader(mapped);
        var noNewer = Runtime(noNewerReader, inner, lostAck, context, batchSize: 10);
        var empty = await noNewer.RunCycleAsync();

        Assert.Equal(new ObservationPosition(1), noNewerReader.LastRequest!.AfterPosition);
        Assert.Empty(empty.Observations);
        Assert.Equal(1, lostAck.PublishCount);
        var afterEmpty = await inner.ReadAsync(context.ProcessorId, context.StreamId);
        Assert.Equal(committed, afterEmpty);
        Assert.Equal(0UL, afterEmpty!.EvaluationAuthority.ProjectionRevision.Value);

        await mapped.WriteAsync(
            [Observation(context, 2, 8, 202, false, Stamp.AddSeconds(5))]);
        var forwardReader = new TrackingMappedObservationReader(mapped);
        var restarted = Runtime(forwardReader, inner, lostAck, context, batchSize: 10);
        var forwardBatch = await restarted.RunCycleAsync();

        Assert.Equal(new ObservationPosition(1), forwardReader.LastRequest!.AfterPosition);
        var forward = Assert.Single(forwardBatch.Observations);
        Assert.Equal(new ObservationPosition(2), forward.Position);
        Assert.Equal(2, lostAck.PublishCount);

        var recovered = await inner.ReadAsync(context.ProcessorId, context.StreamId);
        Assert.NotNull(recovered);
        Assert.Equal(new ObservationPosition(2), recovered.Projection.Position);
        Assert.Equal(1UL, recovered.EvaluationAuthority.ProjectionRevision.Value);
        Assert.Equal(8UL, recovered.EvaluationAuthority.LastConsumedInstanceId);
        Assert.Equal(
            CanonicalCurrentStateContinuityPolicies.Preserve.Reference,
            recovered.EvaluationAuthority.AppliedContinuityPolicy);
        Assert.Equal(2, inner.ReadStateChanges(context.ProcessorId, context.StreamId).Length);
        Assert.Single(inner.ReadActivityPeriods(context.ProcessorId, context.StreamId));
    }

    private static MappedObservationProcessingRuntime Runtime(
        IDurableMappedObservationReader reader,
        InMemoryMachineStateActivityAuthorityStore cursorStore,
        IMachineStateActivityAuthorityStore publicationStore,
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) context,
        int batchSize)
    {
        var cursor = new JointMachineStateActivityCursorReader(cursorStore);
        var processor = new JointAuthorityMachineStateActivityProcessor(
            context.ProcessorId,
            publicationStore,
            CanonicalCurrentStateContinuityPolicies.Preserve);
        return new MappedObservationProcessingRuntime(
            reader,
            cursor,
            processor,
            context.StreamId,
            new ObservationProcessingRuntimeOptions(batchSize, TimeSpan.FromSeconds(1)));
    }

    private static DurableMappedMachineObservation Observation(
        (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) context,
        ulong position,
        ulong instanceId,
        ulong sequence,
        bool running,
        DateTimeOffset timestamp) =>
        new(
            new ObservationPosition(position),
            context.StreamId,
            instanceId,
            sequence,
            new MappedMachineObservation
            {
                MachineId = context.StreamId.MachineId,
                SignalKey = CanonicalSignalKeys.Running,
                Type = SignalType.Digital,
                Value = running,
                Source = "runtime-recovery-proof",
                Address = "DI1",
                Quality = ObservationQuality.Good,
                Timestamp = timestamp,
            });

    private static (ObservationProcessorId ProcessorId, ObservationStreamId StreamId) Context()
    {
        var machineId = MachineId.New();
        return (
            new ObservationProcessorId("machine-state-activity"),
            new ObservationStreamId(machineId, "MTConnect:CNC-01"));
    }

    private sealed class TrackingMappedObservationReader(
        IDurableMappedObservationReader inner) : IDurableMappedObservationReader
    {
        public MappedObservationReadRequest? LastRequest { get; private set; }

        public ValueTask<MappedObservationReadBatch> ReadAsync(
            MappedObservationReadRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return inner.ReadAsync(request, cancellationToken);
        }
    }

    private sealed class LoseNextNewPublicationAcknowledgementStore(
        IMachineStateActivityAuthorityStore inner) : IMachineStateActivityAuthorityStore
    {
        private bool _loseNext = true;

        public int PublishCount { get; private set; }

        public ValueTask<MachineStateActivityAuthoritySnapshot?> ReadAsync(
            ObservationProcessorId stateProcessorId,
            ObservationStreamId observationStreamId,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(stateProcessorId, observationStreamId, cancellationToken);

        public async ValueTask<MachineStateActivityAuthorityPublicationResult> PublishAsync(
            MachineStateActivityAuthorityPublication publication,
            CancellationToken cancellationToken = default)
        {
            PublishCount++;
            var result = await inner.PublishAsync(publication, cancellationToken);
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

    private sealed class LostAcknowledgementException : Exception
    {
    }
}
