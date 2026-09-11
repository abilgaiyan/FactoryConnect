using System.Reflection;
using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class InMemoryAcquisitionContactAuthorityEdgeTests
{
    [Fact]
    public async Task ReturningToEarlierPayloadAllocatesFreshRevision()
    {
        var store = new InMemoryObservationIngestionStore();
        var streamId = StreamId();
        var initial = InitialBatch(streamId, Instant(10));

        await store.CommitAsync(initial);
        var first = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(first);

        await store.CommitAsync(
            new ObservationIngestionBatch(
                initial.Checkpoint,
                initial.Checkpoint,
                [],
                Instant(20)));
        var second = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(second);

        await store.CommitAsync(
            new ObservationIngestionBatch(
                initial.Checkpoint,
                initial.Checkpoint,
                [],
                Instant(10)));
        var third = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(third);

        Assert.Equal(0UL, first.AcquisitionRevision.Value);
        Assert.Equal(1UL, second.AcquisitionRevision.Value);
        Assert.Equal(2UL, third.AcquisitionRevision.Value);
        Assert.Equal(first.SuccessfulContactTime, third.SuccessfulContactTime);
        Assert.Equal(first.RawAcceptedThrough, third.RawAcceptedThrough);
    }

    [Fact]
    public async Task DuplicateOnlySameCheckpointContactUpdatesAuthorityWithoutAllocatingRawPosition()
    {
        var store = new InMemoryObservationIngestionStore();
        var streamId = StreamId();
        var initial = InitialBatch(streamId, Instant(10));

        await store.CommitAsync(initial);
        var before = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(before);
        var observationsBefore = store.ReadObservations(streamId);

        await store.CommitAsync(
            new ObservationIngestionBatch(
                initial.Checkpoint,
                initial.Checkpoint,
                initial.Observations,
                Instant(20)));

        var after = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(after);
        var observationsAfter = store.ReadObservations(streamId);

        Assert.Equal(before.RawAcceptedThrough, after.RawAcceptedThrough);
        Assert.Equal(before.AcquisitionRevision.Value + 1, after.AcquisitionRevision.Value);
        Assert.Equal(observationsBefore, observationsAfter);
    }

    [Fact]
    public async Task AuthorityReadHonorsPreCanceledToken()
    {
        var store = new InMemoryObservationIngestionStore();
        var streamId = StreamId();
        await store.CommitAsync(InitialBatch(streamId, Instant(10)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.ReadAcquisitionContactAuthorityAsync(
                streamId,
                cancellation.Token).AsTask());

        Assert.NotNull(await store.ReadAcquisitionContactAuthorityAsync(streamId));
    }

    [Fact]
    public async Task RevisionExhaustionRejectsAuthorityChangeAtomically()
    {
        var store = new InMemoryObservationIngestionStore();
        var streamId = StreamId();
        var initial = InitialBatch(streamId, Instant(10));

        await store.CommitAsync(initial);
        ForceRevisionExhaustionState(store, streamId);

        var checkpointBefore = await store.ReadCheckpointAsync(streamId);
        var authorityBefore = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(authorityBefore);
        var observationsBefore = store.ReadObservations(streamId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(
                new ObservationIngestionBatch(
                    initial.Checkpoint,
                    initial.Checkpoint,
                    [],
                    Instant(20))).AsTask());

        Assert.Equal(checkpointBefore, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(authorityBefore, await store.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Equal(observationsBefore, store.ReadObservations(streamId));
    }

    private static void ForceRevisionExhaustionState(
        InMemoryObservationIngestionStore store,
        ObservationStreamId streamId)
    {
        var authorities = GetPrivateDictionary<ObservationStreamId, AcquisitionContactAuthority>(
            store,
            "_acquisitionAuthorities");
        var revisions = GetPrivateDictionary<ObservationStreamId, ulong>(
            store,
            "_lastAcquisitionRevisionValues");
        var current = authorities[streamId];

        authorities[streamId] = new AcquisitionContactAuthority(
            current.ObservationStreamId,
            current.SuccessfulContactTime,
            current.RawAcceptedThrough,
            new AcquisitionAuthorityRevision(ulong.MaxValue));
        revisions[streamId] = ulong.MaxValue;
    }

    private static Dictionary<TKey, TValue> GetPrivateDictionary<TKey, TValue>(
        InMemoryObservationIngestionStore store,
        string fieldName)
        where TKey : notnull
    {
        var field = typeof(InMemoryObservationIngestionStore).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var value = field.GetValue(store);
        Assert.NotNull(value);
        return Assert.IsType<Dictionary<TKey, TValue>>(value);
    }

    private static ObservationIngestionBatch InitialBatch(
        ObservationStreamId streamId,
        DateTimeOffset successfulContactTime) =>
        new(
            null,
            new ObservationCheckpoint(streamId, 42, 103),
            [
                new SequencedMachineObservation(
                    101,
                    Observation(streamId.MachineId, "execution")),
                new SequencedMachineObservation(
                    102,
                    Observation(streamId.MachineId, "load")),
            ],
            successfulContactTime);

    private static ObservationStreamId StreamId() =>
        new(MachineId.New(), "MTConnect:CNC-01");

    private static DateTimeOffset Instant(int minute) =>
        new(2026, 9, 11, 8, minute, 0, TimeSpan.Zero);

    private static MachineObservation Observation(
        MachineId machineId,
        string address) =>
        new()
        {
            MachineId = machineId,
            Source = "MTConnect",
            Address = address,
            Type = SignalType.Text,
            Value = "ACTIVE",
            Timestamp = DateTimeOffset.UnixEpoch,
        };
}
