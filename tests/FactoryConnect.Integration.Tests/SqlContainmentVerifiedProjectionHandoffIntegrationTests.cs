using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlContainmentVerifiedProjectionHandoffIntegrationTests(SqlContainmentAuthorityDatabaseFixture fixture)
    : IClassFixture<SqlContainmentAuthorityDatabaseFixture>
{
    private async Task<ContainmentArchiveBatch> PublishAsync()
    {
        var (envelopes, _) = await new SqlContainmentHistoricalResultIntegrationTests(fixture).CreateArchiveTestEvidenceAsync();
        await new SqlContainmentPublicationTransaction(fixture.ConnectionString).PublishAsync(envelopes, null, default);
        return ContainmentArchiveBatch.Preflight(envelopes);
    }

    private static ReadOnlyMemory<byte>[] Envelopes(ContainmentArchiveBatch batch) =>
        batch.Results.Select(result => (ReadOnlyMemory<byte>)result.Payload).ToArray();

    [Fact]
    public async Task ReportingReconstructsEachRetainedResultOnceAndPreservesProjectionMetadata()
    {
        var batch = await PublishAsync();
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var before = await SqlContainmentPublicationIntegrationTests.Snapshot(connection, transaction);
        using var capture = new ReconstructionCapture();
        var publication = await SqlContainmentVerifiedRead.TryReadAsync(connection, transaction, batch.Processor, default);
        Assert.NotNull(publication);
        Assert.Equal(batch.Results.Count, capture.Count);
        Assert.Equal(batch.Results.Count, publication.Projections.Count);
        foreach (var result in batch.Results)
        {
            var actual = Assert.Single(publication.Projections, projection => projection.Key == result.Document.Key);
            Assert.Equal(result.Document.SourceRevision, actual.SourceRevision);
            Assert.Equal(result.Document.Status, actual.Status);
            Assert.Equal(result.Document.Value, actual.Value);
            Assert.Equal(result.Document.Unit, actual.Unit);
            Assert.Equal(result.Document.ReasonCode, actual.ReasonCode);
            Assert.Equal(result.Document.Components.Count, actual.OperandEvidence.Count);
            Assert.Equal(result.Document.Dependencies.Count, actual.DependencyEvidence.Count);
            Assert.Equal(AvailabilityContainmentV1.PolicyId, actual.EvaluationPolicyId);
            Assert.NotNull(actual.PublicationMetadata);
            Assert.Equal(2, actual.PublicationMetadata.PublicationFormatVersion);
            Assert.Equal(Convert.ToHexString(result.Id), actual.PublicationMetadata.ResultId);
            var witnessId = result.Document.Witness is null ? null : Convert.ToHexString(
                OperationalMetricContainmentIdentity.ComputeWitnessId(OperationalMetricConflictWitnessV1Codec.Encode(result.Document.Witness)));
            Assert.Equal(witnessId, actual.PublicationMetadata.ConflictWitnessId);
            Assert.Contains(actual.Key, publication.LatestKeys);
        }
        Assert.Equal(before, await SqlContainmentPublicationIntegrationTests.Snapshot(connection, transaction));
        await transaction.RollbackAsync();
        // Existing detail/summary tests also assert witness proof and publication metadata.
    }

    [Fact]
    public async Task ImmutableHandoffsRequireExactResultKeyCutAndActiveOwnerAndDoNotCacheIndependentVerification()
    {
        var batch = await PublishAsync();
        await using var connection = fixture.CreateConnection();
        await using var foreign = fixture.CreateConnection();
        await connection.OpenAsync();
        await foreign.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var scope = new SqlContainmentHistoricalReadScope(connection, transaction);
        using var capture = new ReconstructionCapture();
        var first = await SqlContainmentArchiveTransaction.VerifyExistingAsync(connection, transaction, Envelopes(batch), default, scope);
        var firstCount = capture.Count;
        Assert.Equal(batch.Results.Count, firstCount);
        var collection = Assert.IsAssignableFrom<IList<SqlContainmentArchiveTransaction.VerifiedProjectionHandoff>>(first);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection[0] = first[0]);
        var result = batch.Results[0];
        var value = first[0].Read(connection, transaction, result.Id, result.Document.Key, batch.Cut, default);
        Assert.Equal(result.Payload, OperationalMetricDependencySnapshotV2Codec.Encode(value.Document));
        Assert.Same(value.Projection, first[0].Read(connection, transaction, result.Id, result.Document.Key, batch.Cut, default).Projection);
        Assert.Equal(firstCount, capture.Count);
        var other = batch.Results[1]; // Another metric at the same exact revision.
        Assert.Equal(batch.Cut, other.Document.SourceRevision);
        Assert.Throws<ContainmentHistoricalIntegrityException>(() =>
            first[0].Read(connection, transaction, other.Id, other.Document.Key, batch.Cut, default));
        Assert.Throws<ContainmentHistoricalIntegrityException>(() =>
            first[0].Read(connection, transaction, result.Id, other.Document.Key, batch.Cut, default));
        var otherPeriod = new OperationalMetricEvaluationKey(result.Document.Key.MachineId,
            new OperationalMetricPeriodId.ProductionDay(new ProductionDayId(new SiteId("other-period"), new DateOnly(2026, 10, 19))),
            result.Document.Key.DefinitionId, result.Document.Key.ContextKey);
        Assert.Throws<ContainmentHistoricalIntegrityException>(() =>
            first[0].Read(connection, transaction, result.Id, otherPeriod, batch.Cut, default));
        foreach (var cut in new[]
        {
            new MetricAggregationCheckpoint(batch.Cut.ProcessorId, batch.Cut.StreamId, new MetricInputPosition(batch.Cut.Position.Value + 1)),
            new MetricAggregationCheckpoint(new MetricAggregationProcessorId("other-processor"), batch.Cut.StreamId, batch.Cut.Position),
            new MetricAggregationCheckpoint(batch.Cut.ProcessorId,
                new MetricInputStreamId(batch.Cut.StreamId.MachineId, "other-stream"), batch.Cut.Position)
        })
            Assert.Throws<ContainmentHistoricalIntegrityException>(() =>
                first[0].Read(connection, transaction, result.Id, result.Document.Key, cut, default));
        Assert.Throws<InvalidOperationException>(() =>
            first[0].Read(foreign, transaction, result.Id, result.Document.Key, batch.Cut, default));
        var second = await SqlContainmentArchiveTransaction.VerifyExistingAsync(connection, transaction, Envelopes(batch), default, scope);
        Assert.Equal(2 * batch.Results.Count, capture.Count);
        Assert.NotSame(value.Projection, second[0].Read(connection, transaction, result.Id, result.Document.Key, batch.Cut, default).Projection);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            first[0].Read(connection, transaction, result.Id, result.Document.Key, batch.Cut, cancellation.Token));
        await transaction.CommitAsync();
        Assert.Throws<InvalidOperationException>(() =>
            first[0].Read(connection, transaction, result.Id, result.Document.Key, batch.Cut, default));
        await using var next = connection.BeginTransaction(IsolationLevel.Serializable);
        Assert.Throws<InvalidOperationException>(() =>
            first[0].Read(connection, next, result.Id, result.Document.Key, batch.Cut, default));
        await next.RollbackAsync();
    }

    [Fact]
    public async Task CancellationAfterReconstructionReturnsNoPartialHandoffAndWritesNothing()
    {
        var batch = await PublishAsync();
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var before = await SqlContainmentPublicationIntegrationTests.Snapshot(connection, transaction);
        using var cancellation = new CancellationTokenSource();
        using var capture = new ReconstructionCapture(cancellation.Cancel);
        IReadOnlyList<SqlContainmentArchiveTransaction.VerifiedProjectionHandoff>? handoffs = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            handoffs = await SqlContainmentArchiveTransaction.VerifyExistingAsync(connection, transaction, Envelopes(batch), cancellation.Token);
        });
        Assert.True(capture.Count > 0);
        Assert.Null(handoffs);
        Assert.Equal(before, await SqlContainmentPublicationIntegrationTests.Snapshot(connection, transaction));
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("canonical-archive")]
    [InlineData("historical-fact")]
    [InlineData("witness-payload")]
    [InlineData("witness-index")]
    [InlineData("missing-reference")]
    [InlineData("missing-witness")]
    public async Task CorruptionRejectsTheWholeHandoffWithoutRepair(string kind)
    {
        var batch = await PublishAsync();
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await using (var damage = connection.CreateCommand())
        {
            damage.Transaction = transaction;
            damage.Parameters.AddWithValue("@Processor", batch.Processor.Value);
            damage.Parameters.AddWithValue("@Aggregation", batch.Cut.ProcessorId.Value);
            const string processor = "(SELECT OperationalMetricProjectionProcessorRowId FROM dbo.OperationalMetricProjectionProcessor WHERE ProcessorKey=@Processor)";
            const string aggregation = "(SELECT MetricAggregationProcessorRowId FROM dbo.MetricAggregationProcessor WHERE ProcessorKey=@Aggregation)";
            damage.CommandText = kind switch
            {
                "canonical-archive" => "DISABLE TRIGGER dbo.TR_OMPRA_Immutable ON dbo.OperationalMetricPublicationResultArchive; UPDATE dbo.OperationalMetricPublicationResultArchive SET ResultPayload=0x01 WHERE OperationalMetricProjectionProcessorRowId=" + processor + ";",
                "historical-fact" => "UPDATE f SET MetricValue=N'999' FROM dbo.MetricInputFact f JOIN dbo.MetricAggregationContribution c ON c.MetricInputFactRowId=f.MetricInputFactRowId WHERE c.MetricAggregationProcessorRowId=" + aggregation + ";",
                "witness-payload" => "DISABLE TRIGGER dbo.TR_OMCWA_Immutable ON dbo.OperationalMetricConflictWitnessArchive; UPDATE dbo.OperationalMetricConflictWitnessArchive SET WitnessPayload=0x01 WHERE MetricAggregationProcessorRowId=" + aggregation + ";",
                "missing-witness" => "DISABLE TRIGGER dbo.TR_OMCWA_Immutable ON dbo.OperationalMetricConflictWitnessArchive; ALTER TABLE dbo.OperationalMetricPublicationResultArchive NOCHECK CONSTRAINT FK_OMPRA_RootWitness; ALTER TABLE dbo.OperationalMetricResultWitnessReference NOCHECK CONSTRAINT FK_OMRWR_Witness; ALTER TABLE dbo.OperationalMetricProjection NOCHECK CONSTRAINT FK_OMP_ConflictWitness; DELETE FROM dbo.OperationalMetricConflictWitnessArchive WHERE MetricAggregationProcessorRowId=" + aggregation + ";",
                "witness-index" => "DISABLE TRIGGER dbo.TR_OMCWA_Immutable ON dbo.OperationalMetricConflictWitnessArchive; ALTER TABLE dbo.OperationalMetricConflictWitnessArchive NOCHECK CONSTRAINT CK_OMCWA_Codec; UPDATE dbo.OperationalMetricConflictWitnessArchive SET WitnessCodecVersion=2 WHERE MetricAggregationProcessorRowId=" + aggregation + ";",
                _ => "DISABLE TRIGGER dbo.TR_OMRWR_Immutable ON dbo.OperationalMetricResultWitnessReference; DELETE x FROM dbo.OperationalMetricResultWitnessReference x JOIN dbo.OperationalMetricPublicationResultArchive r ON r.ResultId=x.ResultId WHERE r.OperationalMetricProjectionProcessorRowId=" + processor + ";"
            };
            await damage.ExecuteNonQueryAsync();
        }
        var damaged = await SqlContainmentPublicationIntegrationTests.Snapshot(connection, transaction);
        using var capture = new ReconstructionCapture();
        IReadOnlyList<SqlContainmentArchiveTransaction.VerifiedProjectionHandoff>? handoffs = null;
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(async () =>
        {
            handoffs = await SqlContainmentArchiveTransaction.VerifyExistingAsync(connection, transaction, Envelopes(batch), default);
        });
        Assert.Null(handoffs);
        // Witness payload/index comparisons occur after historical reconstruction; no early result escapes.
        if (kind is "witness-payload" or "witness-index") Assert.Equal(batch.Results.Count, capture.Count);
        Assert.Equal(damaged, await SqlContainmentPublicationIntegrationTests.Snapshot(connection, transaction));
        await transaction.RollbackAsync(); // Restores evidence and trigger state in this disposable fixture.
    }

    private sealed class ReconstructionCapture : IDisposable
    {
        private readonly Activity _parent = new Activity("verified-projection-handoff-test").SetIdFormat(ActivityIdFormat.W3C).Start();
        private readonly ActivityListener _listener;
        private readonly ConcurrentQueue<Activity> _completed = new();

        internal ReconstructionCapture(Action? onReconstructed = null)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "FactoryConnect.Reporting",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.TraceId != _parent.TraceId || activity.OperationName != "historical.result.reconstruct") return;
                    _completed.Enqueue(activity);
                    onReconstructed?.Invoke();
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }

        internal int Count => _completed.Count;
        public void Dispose() { _listener.Dispose(); _parent.Dispose(); }
    }
}
