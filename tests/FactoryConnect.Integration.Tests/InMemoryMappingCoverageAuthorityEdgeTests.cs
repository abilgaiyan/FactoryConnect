using System.Collections;
using System.Reflection;
using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class InMemoryMappingCoverageAuthorityEdgeTests
{
    [Fact]
    public async Task RevisionExhaustionAllowsExactReplayAndRejectsChangeAtomically()
    {
        var store = new InMemoryMappingCoverageAuthorityStore();
        var (processorId, streamId) = Identity();
        var initial = Commit(
            null,
            processorId,
            streamId,
            raw: 5,
            mapped: 3);

        await store.CommitAsync(initial);
        var beforeExhaustion = await RequiredAuthority(store, processorId, streamId);
        ForceRevision(store, ulong.MaxValue);
        var exhausted = await RequiredAuthority(store, processorId, streamId);

        Assert.Equal(
            new MappingAuthorityRevision(ulong.MaxValue),
            exhausted.MappingRevision);

        await store.CommitAsync(
            Commit(
                beforeExhaustion,
                processorId,
                streamId,
                raw: 5,
                mapped: 3));

        Assert.Equal(
            exhausted,
            await RequiredAuthority(store, processorId, streamId));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(
                Commit(
                    exhausted,
                    processorId,
                    streamId,
                    raw: 6,
                    mapped: 3)).AsTask());

        Assert.Equal(
            exhausted,
            await RequiredAuthority(store, processorId, streamId));
    }

    [Fact]
    public async Task AuthorityHistoryIsLimitedToProviderInstanceLifetime()
    {
        var firstStore = new InMemoryMappingCoverageAuthorityStore();
        var (processorId, streamId) = Identity();

        await firstStore.CommitAsync(
            Commit(null, processorId, streamId, raw: 5, mapped: 3));
        var first = await RequiredAuthority(firstStore, processorId, streamId);
        await firstStore.CommitAsync(
            Commit(first, processorId, streamId, raw: 8, mapped: 6));

        Assert.NotNull(await firstStore.ReadAsync(processorId, streamId));

        var reconstructedStore = new InMemoryMappingCoverageAuthorityStore();

        Assert.Null(await reconstructedStore.ReadAsync(processorId, streamId));
    }

    private static void ForceRevision(
        InMemoryMappingCoverageAuthorityStore store,
        ulong revision)
    {
        var field = typeof(InMemoryMappingCoverageAuthorityStore).GetField(
            "_authorities",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var value = field.GetValue(store);
        var dictionary = Assert.IsAssignableFrom<IDictionary>(value);
        Assert.Equal(1, dictionary.Count);

        object? key = null;
        MappingCoverageAuthority? authority = null;
        foreach (DictionaryEntry entry in dictionary)
        {
            key = entry.Key;
            authority = Assert.IsType<MappingCoverageAuthority>(entry.Value);
            break;
        }

        Assert.NotNull(key);
        Assert.NotNull(authority);

        dictionary[key] = new MappingCoverageAuthority(
            authority.MappingProcessorId,
            authority.ObservationStreamId,
            authority.RawConsumedThrough,
            authority.MappedEvaluationInputHighWater,
            new MappingAuthorityRevision(revision));
    }

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
