using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public abstract class MappingCoverageAuthorityProviderConformanceTests
{
    [Fact]
    public async Task AuthorityIsAbsentBeforeFirstMappingProgress()
    {
        var store = CreateStore();
        var (processorId, streamId) = Identity();

        Assert.Null(await store.ReadAsync(processorId, streamId));
    }

    [Fact]
    public async Task FirstCommitAllocatesRevisionZero()
    {
        var store = CreateStore();
        var (processorId, streamId) = Identity();

        await store.CommitAsync(
            Commit(null, processorId, streamId, raw: 5, mapped: 3));

        var authority = await store.ReadAsync(processorId, streamId);
        Assert.NotNull(authority);
        Assert.Equal(new ObservationPosition(5), authority.RawConsumedThrough);
        Assert.Equal(
            new ObservationPosition(3),
            authority.MappedEvaluationInputHighWater);
        Assert.Equal(new MappingAuthorityRevision(0), authority.MappingRevision);
    }

    [Fact]
    public async Task AdvancingCoverageAllocatesNextRevisionDeterministically()
    {
        var store = CreateStore();
        var (processorId, streamId) = Identity();

        await store.CommitAsync(
            Commit(null, processorId, streamId, raw: 5, mapped: 3));
        var first = await RequiredAuthority(store, processorId, streamId);

        await store.CommitAsync(
            Commit(first, processorId, streamId, raw: 8, mapped: 6));
        var second = await RequiredAuthority(store, processorId, streamId);

        Assert.Equal(new MappingAuthorityRevision(0), first.MappingRevision);
        Assert.Equal(new MappingAuthorityRevision(1), second.MappingRevision);
        Assert.Equal(new ObservationPosition(8), second.RawConsumedThrough);
        Assert.Equal(
            new ObservationPosition(6),
            second.MappedEvaluationInputHighWater);
    }

    [Fact]
    public async Task RawOnlyProgressPreservesMappedHighWaterAndAdvancesRevision()
    {
        var store = CreateStore();
        var (processorId, streamId) = Identity();

        await store.CommitAsync(
            Commit(null, processorId, streamId, raw: 5, mapped: 3));
        var first = await RequiredAuthority(store, processorId, streamId);

        await store.CommitAsync(
            Commit(first, processorId, streamId, raw: 8, mapped: 3));
        var second = await RequiredAuthority(store, processorId, streamId);

        Assert.Equal(new ObservationPosition(8), second.RawConsumedThrough);
        Assert.Equal(
            new ObservationPosition(3),
            second.MappedEvaluationInputHighWater);
        Assert.Equal(new MappingAuthorityRevision(1), second.MappingRevision);
    }

    [Fact]
    public async Task ExactCurrentReplayDoesNotAdvanceRevision()
    {
        var store = CreateStore();
        var (processorId, streamId) = Identity();

        await store.CommitAsync(
            Commit(null, processorId, streamId, raw: 5, mapped: 3));
        var current = await RequiredAuthority(store, processorId, streamId);

        await store.CommitAsync(
            Commit(current, processorId, streamId, raw: 5, mapped: 3));

        Assert.Equal(
            current,
            await RequiredAuthority(store, processorId, streamId));
    }

    [Fact]
    public async Task StaleExactReplaySucceedsWithoutAdvancingRevision()
    {
        var store = CreateStore();
        var (processorId, streamId) = Identity();

        await store.CommitAsync(
            Commit(null, processorId, streamId, raw: 5, mapped: 3));
        var beforeTransition = await RequiredAuthority(store, processorId, streamId);
        var transition = Commit(
            beforeTransition,
            processorId,
            streamId,
            raw: 8,
            mapped: 6);

        await store.CommitAsync(transition);
        var afterFirstAttempt = await RequiredAuthority(store, processorId, streamId);

        await store.CommitAsync(transition);
        var afterRetry = await RequiredAuthority(store, processorId, streamId);

        Assert.Equal(new MappingAuthorityRevision(1), afterFirstAttempt.MappingRevision);
        Assert.Equal(afterFirstAttempt, afterRetry);
    }

    [Fact]
    public async Task LostAcknowledgementOfInitialCommitReplaysIdempotently()
    {
        var store = CreateStore();
        var (processorId, streamId) = Identity();
        var initial = Commit(
            null,
            processorId,
            streamId,
            raw: 5,
            mapped: 3);

        await store.CommitAsync(initial);
        var afterFirstAttempt = await RequiredAuthority(store, processorId, streamId);

        await store.CommitAsync(initial);
        var afterRetry = await RequiredAuthority(store, processorId, streamId);

        Assert.Equal(new MappingAuthorityRevision(0), afterFirstAttempt.MappingRevision);
        Assert.Equal(afterFirstAttempt, afterRetry);
    }

    [Fact]
    public async Task StaleConflictingProposalIsRejectedWithoutMutation()
    {
        var store = CreateStore();
        var (processorId, streamId) = Identity();

        await store.CommitAsync(
            Commit(null, processorId, streamId, raw: 5, mapped: 3));
        var staleExpected = await RequiredAuthority(store, processorId, streamId);

        await store.CommitAsync(
            Commit(staleExpected, processorId, streamId, raw: 8, mapped: 6));
        var current = await RequiredAuthority(store, processorId, streamId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(
                Commit(
                    staleExpected,
                    processorId,
                    streamId,
                    raw: 9,
                    mapped: 6)).AsTask());

        Assert.Equal(
            current,
            await RequiredAuthority(store, processorId, streamId));
    }

    [Fact]
    public async Task AuthoritiesRemainIsolatedByProcessorAndStream()
    {
        var store = CreateStore();
        var machineId = MachineId.New();
        var firstStream = new ObservationStreamId(machineId, "MTConnect:CNC-01");
        var secondStream = new ObservationStreamId(machineId, "MTConnect:CNC-02");
        var firstProcessor = new ObservationProcessorId("canonical-signals");
        var secondProcessor = new ObservationProcessorId("alternate-mapper");

        await store.CommitAsync(
            Commit(null, firstProcessor, firstStream, raw: 5, mapped: 3));
        await store.CommitAsync(
            Commit(null, firstProcessor, secondStream, raw: 7, mapped: 4));
        await store.CommitAsync(
            Commit(null, secondProcessor, firstStream, raw: 9, mapped: 8));

        var first = await RequiredAuthority(store, firstProcessor, firstStream);
        var secondStreamAuthority = await RequiredAuthority(
            store,
            firstProcessor,
            secondStream);
        var secondProcessorAuthority = await RequiredAuthority(
            store,
            secondProcessor,
            firstStream);

        Assert.Equal(new ObservationPosition(5), first.RawConsumedThrough);
        Assert.Equal(
            new ObservationPosition(7),
            secondStreamAuthority.RawConsumedThrough);
        Assert.Equal(
            new ObservationPosition(9),
            secondProcessorAuthority.RawConsumedThrough);
        Assert.Equal(new MappingAuthorityRevision(0), first.MappingRevision);
        Assert.Equal(
            new MappingAuthorityRevision(0),
            secondStreamAuthority.MappingRevision);
        Assert.Equal(
            new MappingAuthorityRevision(0),
            secondProcessorAuthority.MappingRevision);
    }

    [Fact]
    public async Task ReadAndCommitHonorPreCanceledTokenWithoutMutation()
    {
        var store = CreateStore();
        var (processorId, streamId) = Identity();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.ReadAsync(
                processorId,
                streamId,
                cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.CommitAsync(
                Commit(null, processorId, streamId, raw: 5, mapped: 3),
                cancellation.Token).AsTask());

        Assert.Null(await store.ReadAsync(processorId, streamId));
    }

    protected abstract IMappingCoverageAuthorityStore CreateStore();

    private static MappingCoverageCommit Commit(
        MappingCoverageAuthority? expected,
        ObservationProcessorId processorId,
        ObservationStreamId streamId,
        ulong raw,
        ulong? mapped) =>
        new(
            expected,
            processorId,
            streamId,
            new ObservationPosition(raw),
            mapped is null ? null : new ObservationPosition(mapped.Value));

    private static async Task<MappingCoverageAuthority> RequiredAuthority(
        IMappingCoverageAuthorityStore store,
        ObservationProcessorId processorId,
        ObservationStreamId streamId)
    {
        var authority = await store.ReadAsync(processorId, streamId);
        Assert.NotNull(authority);
        return authority;
    }

    private static (ObservationProcessorId ProcessorId, ObservationStreamId StreamId)
        Identity()
    {
        var machineId = MachineId.New();
        return (
            new ObservationProcessorId("canonical-signals"),
            new ObservationStreamId(machineId, "MTConnect:CNC-01"));
    }
}

public sealed class InMemoryMappingCoverageAuthorityProviderConformanceTests :
    MappingCoverageAuthorityProviderConformanceTests
{
    protected override IMappingCoverageAuthorityStore CreateStore() =>
        new InMemoryMappingCoverageAuthorityStore();
}
