using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricProjectionRecoveryConcurrencyIntegrationTests(SqlServerTestDatabaseFixture fixture)
    : IClassFixture<SqlServerTestDatabaseFixture>
{
    [Fact]
    public async Task TwoRecoveriesSerializeToRecoveredThenEquivalent()
    {
        var seed = await RecoveryTestData.CreateAsync(fixture.ConnectionString);
        var reached = NewSignal(); var release = NewSignal();
        var firstStore = new SqlServerOperationalMetricProjectionRecoveryStore(fixture.ConnectionString)
        { AfterGate = async token => { reached.SetResult(); await release.Task.WaitAsync(token); } };
        var first = firstStore.RecoverAsync(seed.Request, CancellationToken.None).AsTask();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var second = new SqlServerOperationalMetricProjectionRecoveryStore(fixture.ConnectionString)
            .RecoverAsync(seed.Request, CancellationToken.None).AsTask();
        try { await AssertWaitingAsync(second); }
        finally { release.TrySetResult(); }
        Assert.Equal(OperationalMetricProjectionRecoveryOutcome.Recovered, (await first).Outcome);
        Assert.Equal(OperationalMetricProjectionRecoveryOutcome.Equivalent, (await second).Outcome);
        await seed.AssertCompleteAsync();
    }

    [Fact]
    public async Task RecoveryFirstReaderCannotObservePartialInsertionOrMissingEvidence()
    {
        var seed = await RecoveryTestData.CreateAsync(fixture.ConnectionString);
        var inserted = NewSignal(); var release = NewSignal();
        var writer = new SqlServerOperationalMetricProjectionRecoveryStore(fixture.ConnectionString)
        { AfterInsert = async (count, token) => { if (count == 1) { inserted.SetResult(); await release.Task.WaitAsync(token); } } };
        var recovery = writer.RecoverAsync(seed.Request, CancellationToken.None).AsTask();
        await inserted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var detail = new SqlServerOperationalMetricProjectionQueryReader(fixture.ConnectionString)
            .ReadDetailAsync(seed.Request.ProcessorId, seed.Request.Projections[0].Key, CancellationToken.None).AsTask();
        var summary = new SqlServerOperationalMetricProjectionQueryReader(fixture.ConnectionString)
            .ReadPeriodSummariesAsync(seed.Request.ProcessorId, seed.Request.SourceRevision.StreamId.MachineId,
                new OperationalMetricPeriodId.ProductionDay(seed.Request.ProductionDayId),
                OperationalMetricEvaluationContextKey.Unpartitioned, CancellationToken.None).AsTask();
        try { await AssertWaitingAsync(detail); await AssertWaitingAsync(summary); }
        finally { release.TrySetResult(); }
        await recovery;
        Assert.NotNull(await detail); Assert.Equal(5, (await summary).Count);
        await seed.AssertCompleteAsync();
    }

    [Fact]
    public async Task ReaderFirstRecoveryWaitsAndReaderMaterializesPreRecoveryState()
    {
        var seed = await RecoveryTestData.CreateAsync(fixture.ConnectionString);
        var entered = NewSignal(); var release = NewSignal();
        await using var c = new SqlConnection(fixture.ConnectionString); await c.OpenAsync();
        var read = SqlServerOperationalMetricProjectionStableRead.ExecuteAsync(c, seed.Request.ProcessorId,
            async (connection, transaction, token) =>
            {
                var before = await SqlServerOperationalMetricProjectionQueryReader.ReadProjectionsAsync(
                    connection, transaction, seed.Request.ProcessorId, token,
                    new OperationalMetricPeriodId.ProductionDay(seed.Request.ProductionDayId));
                entered.SetResult(); await release.Task.WaitAsync(token);
                var after = await SqlServerOperationalMetricProjectionQueryReader.ReadProjectionsAsync(
                    connection, transaction, seed.Request.ProcessorId, token,
                    new OperationalMetricPeriodId.ProductionDay(seed.Request.ProductionDayId));
                Assert.Empty(before); Assert.Empty(after);
                return after.Count;
            }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var recovery = new SqlServerOperationalMetricProjectionRecoveryStore(fixture.ConnectionString)
            .RecoverAsync(seed.Request, CancellationToken.None).AsTask();
        try { await AssertWaitingAsync(recovery); }
        finally { release.TrySetResult(); }
        Assert.Equal(0, await read); await recovery; await seed.AssertCompleteAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NormalPublicationAndRecoverySerializeInBothOrders(bool recoveryFirst)
    {
        var seed = await RecoveryTestData.CreateAsync(fixture.ConnectionString);
        var entered = NewSignal(); var release = NewSignal();
        var checkpoint = await seed.Store.ReadCheckpointAsync(seed.Request.ProcessorId, seed.LiveRevision.StreamId, CancellationToken.None);
        var next = new MetricAggregationCheckpoint(seed.LiveRevision.ProcessorId, seed.LiveRevision.StreamId,
            new MetricInputPosition(seed.LiveRevision.Position.Value + 1));
        var commit = new OperationalMetricProjectionCommit(seed.Request.ProcessorId, checkpoint,
            new OperationalMetricProjectionCheckpoint(seed.Request.ProcessorId, next), []);
        var normal = new SqlServerOperationalMetricProjectionCommitTransaction(fixture.ConnectionString);
        var recoveryStore = new SqlServerOperationalMetricProjectionRecoveryStore(fixture.ConnectionString)
        { AfterGate = recoveryFirst ? async token => { entered.SetResult(); await release.Task.WaitAsync(token); } : null };
        async Task PublishAsync(bool pause)
        {
            await normal.ExecuteAsync(commit, async (context, token) =>
            {
                if (pause) { entered.SetResult(); await release.Task.WaitAsync(token); }
                await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(context, commit, token);
            }, CancellationToken.None);
        }
        Task first; Task second;
        if (recoveryFirst)
        {
            first = recoveryStore.RecoverAsync(seed.Request, CancellationToken.None).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); second = PublishAsync(false);
        }
        else
        {
            first = PublishAsync(true); await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            second = recoveryStore.RecoverAsync(seed.Request, CancellationToken.None).AsTask();
        }
        try { await AssertWaitingAsync(second); }
        finally { release.TrySetResult(); }
        await Task.WhenAll(first, second);
        Assert.Equal(next, (await seed.Store.ReadCheckpointAsync(seed.Request.ProcessorId, next.StreamId, CancellationToken.None))!.SourceRevision);
        await seed.AssertCompleteAsync();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task AssertWaitingAsync(Task task)
    {
        // A bounded observation window after an explicitly signalled held gate.
        Assert.NotSame(task, await Task.WhenAny(task, Task.Delay(150)));
    }
}
