using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

// No SQL fixture: these tests exercise acceptance and isolation of loaded evidence only.
public sealed class ContainmentHistoricalInputReuseTests
{
    private static MetricAggregationCheckpoint Cut(string processor = "aggregation", string stream = "inputs",
        ulong revision = 534, MachineId? machine = null) => new(new(processor),
        new(machine ?? new MachineId(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f")), stream), new(revision));

    [Fact]
    public async Task EqualExactCutsReuseOneSuccessfulLoadAndOwnTheCollection()
    {
        var calls = 0;
        var source = new List<PositionedMetricInputFact>();
        var cache = new SqlContainmentHistoricalReadScope.EvidenceCache((_, _) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<PositionedMetricInputFact>>(source);
        });
        var first = await cache.ReadAsync(Cut(), default);
        source.Add(null!); // A loader-owned collection cannot change already accepted membership.
        var second = await cache.ReadAsync(Cut(), default);
        Assert.Same(first, second);
        Assert.Empty(second);
        Assert.Equal(1, calls);
        Assert.Throws<NotSupportedException>(() => ((IList<PositionedMetricInputFact>)first).Add(null!));
    }

    [Theory]
    [InlineData("processor")]
    [InlineData("stream")]
    [InlineData("processor-case")]
    [InlineData("stream-case")]
    [InlineData("machine")]
    [InlineData("revision")]
    public async Task EveryExactIdentityDimensionSeparatesEvidence(string dimension)
    {
        var calls = 0;
        var cache = new SqlContainmentHistoricalReadScope.EvidenceCache((_, _) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<PositionedMetricInputFact>>([]);
        });
        var other = dimension switch
        {
            "processor" => Cut(processor: "other"),
            "stream" => Cut(stream: "other"),
            "processor-case" => Cut(processor: "AGGREGATION"),
            "stream-case" => Cut(stream: "INPUTS"),
            "machine" => Cut(machine: new MachineId(Guid.NewGuid())),
            _ => Cut(revision: 537)
        };
        _ = await cache.ReadAsync(Cut(), default);
        _ = await cache.ReadAsync(other, default);
        _ = await cache.ReadAsync(Cut(), default);
        _ = await cache.ReadAsync(other, default);
        // Empty read-only collections may be runtime singletons. Loader calls establish isolation.
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SeparateScopesDoNotReuseEvidence()
    {
        var calls = 0;
        Task<IReadOnlyList<PositionedMetricInputFact>> Load(MetricAggregationCheckpoint _, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            calls++;
            return Task.FromResult<IReadOnlyList<PositionedMetricInputFact>>([]);
        }
        var first = new SqlContainmentHistoricalReadScope.EvidenceCache(Load);
        var second = new SqlContainmentHistoricalReadScope.EvidenceCache(Load);
        _ = await first.ReadAsync(Cut(), default);
        _ = await second.ReadAsync(Cut(), default);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledLoadsAreRetriedWithoutLosingExceptionIdentity(bool cancelled)
    {
        var calls = 0;
        Exception failure = cancelled ? new OperationCanceledException() : new ContainmentHistoricalIntegrityException("bad evidence");
        var cache = new SqlContainmentHistoricalReadScope.EvidenceCache((_, _) =>
        {
            calls++;
            return calls == 1 ? Task.FromException<IReadOnlyList<PositionedMetricInputFact>>(failure)
                : Task.FromResult<IReadOnlyList<PositionedMetricInputFact>>([]);
        });
        var error = await Record.ExceptionAsync(() => cache.ReadAsync(Cut(), default));
        Assert.Same(failure, error);
        Assert.Empty(await cache.ReadAsync(Cut(), default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancellationAfterLoaderReturnsDoesNotPublishEvidence()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var cache = new SqlContainmentHistoricalReadScope.EvidenceCache((_, _) =>
        {
            calls++;
            if (calls == 1) cancellation.Cancel();
            return Task.FromResult<IReadOnlyList<PositionedMetricInputFact>>([]);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.ReadAsync(Cut(), cancellation.Token));
        _ = await cache.ReadAsync(Cut(), default);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCancelledAccessDoesNotLoadOrServeAWarmCut(bool warm)
    {
        var calls = 0;
        var cache = new SqlContainmentHistoricalReadScope.EvidenceCache((_, _) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<PositionedMetricInputFact>>([]);
        });
        if (warm) _ = await cache.ReadAsync(Cut(), default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.ReadAsync(Cut(), cancellation.Token));
        Assert.Equal(warm ? 1 : 0, calls);
    }

    [Fact]
    public async Task OverlappingUseFailsClosedAndDoesNotStartAnotherSqlLoad()
    {
        var loaded = new TaskCompletionSource<IReadOnlyList<PositionedMetricInputFact>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cache = new SqlContainmentHistoricalReadScope.EvidenceCache((_, _) => { calls++; return loaded.Task; });
        var pending = cache.ReadAsync(Cut(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.ReadAsync(Cut(revision: 537), default));
        loaded.SetResult([]);
        var inputs = await pending;
        Assert.Same(inputs, await cache.ReadAsync(Cut(), default));
        Assert.Equal(1, calls);
    }
}
