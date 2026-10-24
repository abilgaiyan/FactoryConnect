using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlContainmentHistoricalLoadCountIntegrationTests(SqlContainmentAuthorityDatabaseFixture fixture)
    : IClassFixture<SqlContainmentAuthorityDatabaseFixture>
{
    [Fact]
    public async Task MultiResultPublisherAndReplayEachReloadOnceInPreparationAndOnceInPublication()
    {
        var envelopes = await new SqlContainmentHistoricalResultIntegrationTests(fixture).CreateVerifiedReadEvidenceAsync(false);
        var batch = ContainmentArchiveBatch.Preflight(envelopes);
        Assert.True(batch.Results.Count > 1);
        var projections = new List<OperationalMetricProjection>();
        // Prepare runtime inputs outside observation, so setup cannot satisfy execution assertions.
        await using (var connection = fixture.CreateConnection())
        {
            await connection.OpenAsync();
            await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            foreach (var result in batch.Results)
                projections.Add((await SqlContainmentHistoricalResultValidator.ReconstructVerifiedAsync(connection, transaction,
                    result.Document.Key, batch.Cut, default)).Projection);
        }
        var proposed = Checkpoint(batch);
        using var counter = new ContainmentHistoricalSqlCounter(fixture.ConnectionString);
        var publisher = new SqlServerOperationalMetricContainmentPublisher(counter.ConnectionString);
        await publisher.PublishAsync(null, proposed, projections, default);
        var initial = counter.Executions;
        Assert.Equal(2, initial.Length);
        AssertOneLoadPerCutAndTransaction(counter, initial);
        Assert.All(initial, e => Assert.Equal(batch.Cut.Position.Value, e.Position));
        Assert.NotEqual(initial[0].TransactionOrdinal, initial[1].TransactionOrdinal);

        // Replay verifies retained and incoming results in its own publication scope.
        await publisher.PublishAsync(proposed, proposed, projections, default);
        var all = counter.Executions;
        Assert.Equal(4, all.Length);
        AssertOneLoadPerCutAndTransaction(counter, all);
        Assert.Equal(4, all.Select(e => e.TransactionOrdinal).Distinct().Count());
        Assert.All(all, e => Assert.Equal(batch.Cut.Position.Value, e.Position));
    }

    [Fact]
    public async Task AdvancingPublisherAndEachReportingRequestLoadEachRetainedOrIncomingCutExactlyOnce()
    {
        var (first, next) = await new SqlContainmentHistoricalResultIntegrationTests(fixture).CreatePublicationAdvanceEvidenceAsync();
        var prior = ContainmentArchiveBatch.Preflight(first);
        var incoming = ContainmentArchiveBatch.Preflight(next);
        Assert.NotEqual(prior.Cut, incoming.Cut);
        await new SqlContainmentPublicationTransaction(fixture.ConnectionString).PublishAsync(first, null, default);
        using var counter = new ContainmentHistoricalSqlCounter(fixture.ConnectionString);
        await new SqlContainmentPublicationTransaction(counter.ConnectionString).PublishAsync(next, Checkpoint(prior), default);
        var publication = counter.Executions;
        AssertTwoCuts(counter, publication, prior.Cut, incoming.Cut);
        Assert.Equal(publication[0].TransactionOrdinal, publication[1].TransactionOrdinal);

        var reader = new SqlServerOperationalMetricProjectionQueryReader(counter.ConnectionString);
        var detail = await reader.ReadDetailAsync(incoming.Processor, incoming.Results[0].Document.Key, default);
        Assert.NotNull(detail);
        Assert.Equal(incoming.Cut, detail.SourceRevision);
        var reporting = counter.Executions.Skip(publication.Length).ToArray();
        AssertTwoCuts(counter, reporting, prior.Cut, incoming.Cut);
        Assert.Equal(reporting[0].TransactionOrdinal, reporting[1].TransactionOrdinal);
        Assert.NotEqual(publication[0].TransactionOrdinal, reporting[0].TransactionOrdinal);

        var checkpoint = await new SqlServerOperationalMetricProjectionStore(counter.ConnectionString)
            .ReadCheckpointAsync(incoming.Processor, incoming.Cut.StreamId, default);
        Assert.NotNull(checkpoint);
        Assert.Equal(incoming.Cut, checkpoint.SourceRevision);
        var checkpointReads = counter.Executions.Skip(publication.Length + reporting.Length).ToArray();
        AssertTwoCuts(counter, checkpointReads, prior.Cut, incoming.Cut);
        Assert.Equal(checkpointReads[0].TransactionOrdinal, checkpointReads[1].TransactionOrdinal);
        Assert.NotEqual(reporting[0].TransactionOrdinal, checkpointReads[0].TransactionOrdinal);
    }

    private static OperationalMetricProjectionCheckpoint Checkpoint(ContainmentArchiveBatch batch) =>
        new(batch.Processor, batch.Cut, new OperationalMetricProjectionBatchManifest(batch.Results.Select(r => r.Document.Key)));

    private static void AssertTwoCuts(ContainmentHistoricalSqlCounter counter,
        ContainmentHistoricalSqlCounter.Execution[] executions, MetricAggregationCheckpoint prior, MetricAggregationCheckpoint incoming)
    {
        Assert.Equal(2, executions.Length);
        AssertOneLoadPerCutAndTransaction(counter, executions);
        Assert.Equal(new[] { prior.Position.Value, incoming.Position.Value }.Order(), executions.Select(e => e.Position).Order());
        Assert.Single(executions.Select(e => e.Processor).Distinct());
        Assert.Single(executions.Select(e => e.Stream).Distinct());
    }

    private static void AssertOneLoadPerCutAndTransaction(ContainmentHistoricalSqlCounter counter,
        ContainmentHistoricalSqlCounter.Execution[] executions)
    {
        Assert.NotEmpty(executions);
        Assert.Null(counter.ListenerError);
        Assert.Equal(0, counter.PendingCount);
        Assert.All(executions, e => { Assert.False(e.Failed); Assert.True(e.TransactionOrdinal > 0); Assert.True(e.HadTransactionAtStart); Assert.True(e.TransactionBoundToConnectionAtStart); Assert.NotNull(e.Elapsed); });
        Assert.All(executions.GroupBy(e => (e.TransactionOrdinal, e.Processor, e.Stream, e.Position)), group => Assert.Single(group));
    }
}
