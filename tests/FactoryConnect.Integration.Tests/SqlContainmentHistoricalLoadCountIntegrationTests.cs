using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlContainmentHistoricalLoadCountIntegrationTests(SqlContainmentAuthorityDatabaseFixture fixture)
    : IClassFixture<SqlContainmentAuthorityDatabaseFixture>
{
    [Fact]
    public async Task ThreeShiftSelectionVerifiesPublicationOnceAndReloadsEachExactCutOnce()
    {
        var (first, next) = await new SqlContainmentHistoricalResultIntegrationTests(fixture).CreatePublicationAdvanceEvidenceAsync();
        var prior = ContainmentArchiveBatch.Preflight(first);
        var incoming = ContainmentArchiveBatch.Preflight(next);
        await new SqlContainmentPublicationTransaction(fixture.ConnectionString).PublishAsync(first, null, default);
        await new SqlContainmentPublicationTransaction(fixture.ConnectionString).PublishAsync(next, Checkpoint(prior), default);
        var key = incoming.Results[0].Document.Key;
        var shift = Assert.IsType<OperationalMetricPeriodId.Shift>(key.PeriodId).ShiftOccurrenceId;
        var second = new ShiftOccurrenceId(shift.SiteId, shift.ShiftScheduleAssignmentId,
            new ShiftId("batch-second"), shift.EndsAtUtc, shift.EndsAtUtc.AddHours(8));
        var third = new ShiftOccurrenceId(shift.SiteId, shift.ShiftScheduleAssignmentId,
            new ShiftId("batch-third"), second.EndsAtUtc, second.EndsAtUtc.AddHours(8));
        using var counter = new ContainmentHistoricalSqlCounter(fixture.ConnectionString);
        var verifiedCount = 0;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == OperationalMetricReportingDiagnostics.Source.Name,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "containment.publication.verify" &&
                    Equals(activity.GetTagItem("processor.id"), incoming.Processor.Value))
                    Interlocked.Increment(ref verifiedCount);
            },
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var reader = new SqlServerOperationalMetricProjectionQueryReader(counter.ConnectionString);
        var actual = await reader.ReadPeriodsSummariesAsync(incoming.Processor, key.MachineId,
            [key.PeriodId, new OperationalMetricPeriodId.Shift(second), new OperationalMetricPeriodId.Shift(third)],
            key.ContextKey, default);
        Assert.Equal(1, verifiedCount);
        AssertTwoCuts(counter, counter.Executions, prior.Cut, incoming.Cut);
        Assert.Single(counter.Executions.Select(e => e.Transaction).Distinct());
        var expected = await new SqlServerOperationalMetricProjectionQueryReader(fixture.ConnectionString)
            .ReadPeriodSummariesAsync(incoming.Processor, key.MachineId, key.PeriodId, key.ContextKey, default);
        Assert.Equal(expected.Select(s => (s.ProcessorId, s.Key, s.Status, s.Value, s.Unit, s.ReasonCode, s.ReasonOperandName, s.SourceRevision, s.PublicationMetadata)),
            actual.Select(s => (s.ProcessorId, s.Key, s.Status, s.Value, s.Unit, s.ReasonCode, s.ReasonOperandName, s.SourceRevision, s.PublicationMetadata)));
    }

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
        Assert.NotSame(initial[0].Transaction, initial[1].Transaction);

        // Replay verifies retained and incoming results in its own publication scope.
        await publisher.PublishAsync(proposed, proposed, projections, default);
        var all = counter.Executions;
        Assert.Equal(4, all.Length);
        AssertOneLoadPerCutAndTransaction(counter, all);
        Assert.Equal(4, all.Select(e => e.Transaction).Distinct().Count());
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
        Assert.Same(publication[0].Transaction, publication[1].Transaction);

        var reader = new SqlServerOperationalMetricProjectionQueryReader(counter.ConnectionString);
        var detail = await reader.ReadDetailAsync(incoming.Processor, incoming.Results[0].Document.Key, default);
        Assert.NotNull(detail);
        Assert.Equal(incoming.Cut, detail.SourceRevision);
        var reporting = counter.Executions.Skip(publication.Length).ToArray();
        AssertTwoCuts(counter, reporting, prior.Cut, incoming.Cut);
        Assert.Same(reporting[0].Transaction, reporting[1].Transaction);
        Assert.NotSame(publication[0].Transaction, reporting[0].Transaction);

        var checkpoint = await new SqlServerOperationalMetricProjectionStore(counter.ConnectionString)
            .ReadCheckpointAsync(incoming.Processor, incoming.Cut.StreamId, default);
        Assert.NotNull(checkpoint);
        Assert.Equal(incoming.Cut, checkpoint.SourceRevision);
        var checkpointReads = counter.Executions.Skip(publication.Length + reporting.Length).ToArray();
        AssertTwoCuts(counter, checkpointReads, prior.Cut, incoming.Cut);
        Assert.Same(checkpointReads[0].Transaction, checkpointReads[1].Transaction);
        Assert.NotSame(reporting[0].Transaction, checkpointReads[0].Transaction);
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
        Assert.All(executions, e => { Assert.False(e.Failed); Assert.NotNull(e.Transaction); });
        Assert.All(executions.GroupBy(e => (e.Transaction, e.Processor, e.Stream, e.Position)), group => Assert.Single(group));
    }
}
