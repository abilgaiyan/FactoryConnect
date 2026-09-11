using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class InMemoryAcquisitionContactAuthorityTests
{
    [Fact]
    public async Task FirstEmptyCommitCreatesContactAuthorityWithoutRawHighWater()
    {
        var store = new InMemoryObservationIngestionStore();
        var streamId = StreamId();
        var checkpoint = new ObservationCheckpoint(streamId, 42, 101);
        var contactTime = Instant(10);

        await store.CommitAsync(
            new ObservationIngestionBatch(
                null,
                checkpoint,
                [],
                contactTime));

        var authority = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(authority);

        Assert.Equal(streamId, authority.ObservationStreamId);
        Assert.Equal(contactTime, authority.SuccessfulContactTime);
        Assert.Null(authority.RawAcceptedThrough);
        Assert.Equal(0UL, authority.AcquisitionRevision.Value);
    }

    [Fact]
    public async Task RawAcceptedThroughUsesDurableObservationPosition()
    {
        var store = new InMemoryObservationIngestionStore();
        var streamId = StreamId();

        await store.CommitAsync(InitialBatch(streamId, Instant(10)));

        var authority = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(authority);
        Assert.NotNull(authority.RawAcceptedThrough);

        Assert.Equal(2UL, authority.RawAcceptedThrough.Value);
    }

    [Fact]
    public async Task SameCheckpointLaterContactChangesAuthorityWithoutChangingRawHighWater()
    {
        var store = new InMemoryObservationIngestionStore();
        var streamId = StreamId();
        var initial = InitialBatch(streamId, Instant(10));

        await store.CommitAsync(initial);
        var before = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(before);

        var earlierClockValue = Instant(5);
        await store.CommitAsync(
            new ObservationIngestionBatch(
                initial.Checkpoint,
                initial.Checkpoint,
                [],
                earlierClockValue));

        var after = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(after);

        Assert.Equal(earlierClockValue, after.SuccessfulContactTime);
        Assert.Equal(before.RawAcceptedThrough, after.RawAcceptedThrough);
        Assert.Equal(before.AcquisitionRevision.Value + 1, after.AcquisitionRevision.Value);
        Assert.Equal(initial.Checkpoint, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(2, store.ReadObservations(streamId).Length);
    }

    [Fact]
    public async Task SameInstantWithDifferentOffsetPreservesRevision()
    {
        var store = new InMemoryObservationIngestionStore();
        var streamId = StreamId();
        var instant = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        var equivalent = instant.ToOffset(TimeSpan.FromHours(5.5));
        var initial = InitialBatch(streamId, instant);

        await store.CommitAsync(initial);
        var before = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(before);

        await store.CommitAsync(
            new ObservationIngestionBatch(
                initial.Checkpoint,
                initial.Checkpoint,
                [],
                equivalent));

        var after = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(after);

        Assert.Equal(before.AcquisitionRevision, after.AcquisitionRevision);
        Assert.Equal(
            before.SuccessfulContactTime.UtcDateTime.Ticks,
            after.SuccessfulContactTime.UtcDateTime.Ticks);
    }

    [Fact]
    public async Task ExactStaleReplayPreservesCommittedAuthority()
    {
        var store = new InMemoryObservationIngestionStore();
        var streamId = StreamId();
        var initial = InitialBatch(streamId, Instant(10));
        var next = new ObservationCheckpoint(streamId, 42, 105);
        var continuation = new ObservationIngestionBatch(
            initial.Checkpoint,
            next,
            [
                new SequencedMachineObservation(
                    103,
                    Observation(streamId.MachineId, "execution")),
                new SequencedMachineObservation(
                    104,
                    Observation(streamId.MachineId, "load")),
            ],
            Instant(20));

        await store.CommitAsync(initial);
        await store.CommitAsync(continuation);
        var before = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(before);

        await store.CommitAsync(continuation);

        var after = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(after);

        Assert.Equal(before, after);
        Assert.Equal(4, store.ReadObservations(streamId).Length);
    }

    [Fact]
    public async Task SupersededReplayIsRejectedWithoutChangingAuthority()
    {
        var store = new InMemoryObservationIngestionStore();
        var streamId = StreamId();
        var initial = InitialBatch(streamId, Instant(10));

        await store.CommitAsync(initial);
        await store.CommitAsync(
            new ObservationIngestionBatch(
                initial.Checkpoint,
                initial.Checkpoint,
                [],
                Instant(20)));
        var before = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(before);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(initial).AsTask());

        Assert.Equal(
            before,
            await store.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Equal(initial.Checkpoint, await store.ReadCheckpointAsync(streamId));
        Assert.Equal(2, store.ReadObservations(streamId).Length);
    }

    [Fact]
    public async Task SameCheckpointCannotAddNewRawObservation()
    {
        var store = new InMemoryObservationIngestionStore();
        var streamId = StreamId();
        var initial = InitialBatch(streamId, Instant(10));

        await store.CommitAsync(initial);
        var before = await store.ReadAcquisitionContactAuthorityAsync(streamId);
        Assert.NotNull(before);

        var augmented = new ObservationIngestionBatch(
            initial.Checkpoint,
            initial.Checkpoint,
            [
                new SequencedMachineObservation(
                    100,
                    Observation(streamId.MachineId, "availability")),
            ],
            Instant(20));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(augmented).AsTask());

        Assert.Equal(
            before,
            await store.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Equal(2, store.ReadObservations(streamId).Length);
    }

    [Fact]
    public async Task AcquisitionAuthoritiesRemainIsolatedByStream()
    {
        var store = new InMemoryObservationIngestionStore();
        var machineId = MachineId.New();
        var first = new ObservationStreamId(machineId, "MTConnect:CNC-01");
        var second = new ObservationStreamId(machineId, "MTConnect:CNC-02");

        await store.CommitAsync(InitialBatch(first, Instant(10)));
        await store.CommitAsync(InitialBatch(second, Instant(20)));

        var firstAuthority = await store.ReadAcquisitionContactAuthorityAsync(first);
        Assert.NotNull(firstAuthority);
        var secondAuthority = await store.ReadAcquisitionContactAuthorityAsync(second);
        Assert.NotNull(secondAuthority);

        Assert.Equal(Instant(10), firstAuthority.SuccessfulContactTime);
        Assert.Equal(Instant(20), secondAuthority.SuccessfulContactTime);
        Assert.Equal(0UL, firstAuthority.AcquisitionRevision.Value);
        Assert.Equal(0UL, secondAuthority.AcquisitionRevision.Value);
    }

    [Fact]
    public async Task PreCanceledCommitDoesNotCreateAuthority()
    {
        var store = new InMemoryObservationIngestionStore();
        var streamId = StreamId();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.CommitAsync(
                InitialBatch(streamId, Instant(10)),
                cancellation.Token).AsTask());

        Assert.Null(await store.ReadAcquisitionContactAuthorityAsync(streamId));
        Assert.Null(await store.ReadCheckpointAsync(streamId));
        Assert.Empty(store.ReadObservations(streamId));
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
