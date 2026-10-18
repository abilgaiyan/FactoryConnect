using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FactoryConnect.Abstractions;
using FactoryConnect.Api.Reporting;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlContainmentVerifiedReadIntegrationTests(SqlContainmentAuthorityDatabaseFixture fixture)
    : IClassFixture<SqlContainmentAuthorityDatabaseFixture>
{
    private async Task<ContainmentArchiveBatch> Publish()
    {
        var (envelopes, _) = await new SqlContainmentHistoricalResultIntegrationTests(fixture).CreateArchiveTestEvidenceAsync();
        await new SqlContainmentPublicationTransaction(fixture.ConnectionString).PublishAsync(envelopes, null, default);
        return ContainmentArchiveBatch.Preflight(envelopes);
    }
    private SqlServerOperationalMetricProjectionQueryReader Reader => new(fixture.ConnectionString);

    [Fact]
    public async Task DetailSummaryAndCheckpointReturnVerifiedWitnessAndRecursiveOeeWithoutWrites()
    {
        var batch = await Publish();
        await using var c = fixture.CreateConnection(); await c.OpenAsync();
        var before = await SqlContainmentPublicationIntegrationTests.Snapshot(c, null);
        foreach (var result in batch.Results)
        {
            var projection = await Reader.ReadDetailAsync(batch.Processor, result.Document.Key, default);
            Assert.NotNull(projection); Assert.Equal(AvailabilityContainmentV1.PolicyId, projection.EvaluationPolicyId);
            Assert.Equal(batch.Cut, projection.SourceRevision); Assert.Equal(OperationalMetricEvaluationStatus.Unavailable, projection.Status);
            Assert.Null(projection.Value);
            if (projection.Key.DefinitionId == BuiltInOperationalMetricDefinitions.AvailabilityId)
            {
                Assert.Equal(OperationalMetricEvaluationReasonCode.RunningOutsidePlannedProduction, projection.ReasonCode);
                Assert.NotNull(projection.AvailabilityScopeConflictEvidence);
                Assert.Equal(result.Document.Witness!.Contribution, projection.AvailabilityScopeConflictEvidence.Contribution);
            }
            else
            {
                Assert.Equal(3, projection.DependencyEvidence.Count);
                var availability = projection.DependencyEvidence.Single(d => d.DefinitionId == BuiltInOperationalMetricDefinitions.AvailabilityId).Projection;
                Assert.NotNull(availability.AvailabilityScopeConflictEvidence);
            }
        }
        var first = batch.Results[0].Document;
        var summaries = await Reader.ReadPeriodSummariesAsync(batch.Processor, first.Key.MachineId, first.Key.PeriodId, first.Key.ContextKey, default);
        Assert.Equal(2, summaries.Count); Assert.All(summaries, s => Assert.Equal(batch.Cut, s.SourceRevision));
        var checkpoint = await new SqlServerOperationalMetricProjectionStore(fixture.ConnectionString).ReadCheckpointAsync(batch.Processor, batch.Cut.StreamId, default);
        Assert.NotNull(checkpoint); Assert.Equal(batch.Cut, checkpoint.SourceRevision);
        Assert.Equal(2, checkpoint.BatchManifest.ProjectionKeys.Count);
        Assert.Equal(before, await SqlContainmentPublicationIntegrationTests.Snapshot(c, null));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task EveryBuiltInMetricIsVerifiedAtShiftAndProductionDay(bool day)
    {
        var envelopes = await new SqlContainmentHistoricalResultIntegrationTests(fixture).CreateVerifiedReadEvidenceAsync(day);
        await new SqlContainmentPublicationTransaction(fixture.ConnectionString).PublishAsync(envelopes, null, default);
        var batch = ContainmentArchiveBatch.Preflight(envelopes);
        foreach (var result in batch.Results)
        {
            var actual = await Reader.ReadDetailAsync(batch.Processor, result.Document.Key, default);
            Assert.NotNull(actual); Assert.Equal(result.Document.Status, actual.Status); Assert.Equal(result.Document.Value, actual.Value);
            Assert.Equal(result.Document.ReasonCode, actual.ReasonCode); Assert.Equal(AvailabilityContainmentV1.PolicyId, actual.EvaluationPolicyId);
        }
        var first = batch.Results[0].Document;
        var summaries = await Reader.ReadPeriodSummariesAsync(batch.Processor, first.Key.MachineId, first.Key.PeriodId, first.Key.ContextKey, default);
        Assert.Equal(5, summaries.Count);
        Assert.Equal(0.5m, summaries.Single(s => s.Key.DefinitionId == BuiltInOperationalMetricDefinitions.UtilizationId).Value);
        Assert.Equal(0.4m, summaries.Single(s => s.Key.DefinitionId == BuiltInOperationalMetricDefinitions.PerformanceId).Value);
        Assert.Equal(1m, summaries.Single(s => s.Key.DefinitionId == BuiltInOperationalMetricDefinitions.QualityId).Value);
    }

    [Fact]
    public async Task ActualDetailReaderWaitsForAtomicPublicationAndCancellationDoesNotExposePartialRows()
    {
        var (envelopes, _) = await new SqlContainmentHistoricalResultIntegrationTests(fixture).CreateArchiveTestEvidenceAsync();
        var batch = ContainmentArchiveBatch.Preflight(envelopes);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new SqlContainmentPublicationTransaction(fixture.ConnectionString).PublishAsync(envelopes, null, timeout.Token, async (stage, ct) =>
        { if (stage == ContainmentPublicationStage.ProjectionsWritten) { reached.SetResult(); await release.Task.WaitAsync(ct); } });
        await reached.Task.WaitAsync(timeout.Token);
        using var cancelRead = new CancellationTokenSource();
        var reading = Reader.ReadDetailAsync(batch.Processor, batch.Results[0].Document.Key, cancelRead.Token).AsTask();
        try
        {
            await using var monitor = fixture.CreateConnection(); await monitor.OpenAsync(timeout.Token);
            await using var probe = monitor.CreateCommand();
            probe.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE database_id=DB_ID() AND blocking_session_id<>0 AND wait_type LIKE 'LCK_M_%';";
            var deadline = System.Diagnostics.Stopwatch.StartNew(); var observed = false;
            while (deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                if ((int)(await probe.ExecuteScalarAsync(timeout.Token))! > 0) { observed = true; break; }
                await Task.Delay(25, timeout.Token);
            }
            Assert.True(observed, "The actual reporting reader must block behind partial publication.");
            cancelRead.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        }
        finally { release.TrySetResult(); await writer; }
        var published = await Reader.ReadDetailAsync(batch.Processor, batch.Results[0].Document.Key, default);
        Assert.NotNull(published); Assert.Equal(batch.Cut, published.SourceRevision);
    }

    [Fact]
    public async Task ExactProcessorReadsKeepLegacyAndContainmentSeparateAndAbsentProcessorReturnsNothing()
    {
        var batch = await Publish(); var first = batch.Results[0].Document;
        var legacyId = new OperationalMetricProjectionProcessorId($"operational-metrics:{first.Key.MachineId.Value}:builtins-v1");
        var legacy = new OperationalMetricProjection(legacyId, first.Key, OperationalMetricEvaluationStatus.Calculated, 0.5m,
            "ratio", null, null, batch.Cut, [], []);
        var store = new SqlServerOperationalMetricProjectionStore(fixture.ConnectionString);
        var checkpoint = new OperationalMetricProjectionCheckpoint(legacyId, batch.Cut, new OperationalMetricProjectionBatchManifest([first.Key]));
        await store.CommitAsync(new(legacyId, null, checkpoint, [legacy]), default);
        var original = await Reader.ReadDetailAsync(legacyId, first.Key, default);
        Assert.NotNull(original); Assert.Equal(0.5m, original.Value); Assert.Null(original.EvaluationPolicyId);
        var contained = await Reader.ReadDetailAsync(batch.Processor, first.Key, default);
        Assert.NotNull(contained); Assert.Null(contained.Value); Assert.Equal(AvailabilityContainmentV1.PolicyId, contained.EvaluationPolicyId);
        Assert.Null(await Reader.ReadDetailAsync(new OperationalMetricProjectionProcessorId("unregistered-exact-processor"), first.Key, default));
        Assert.Null(await Reader.ReadDetailAsync(AvailabilityContainmentV1.ProjectionProcessorId(new MachineId(Guid.NewGuid())), first.Key, default));
        var missingKey = new OperationalMetricEvaluationKey(first.Key.MachineId, first.Key.PeriodId, BuiltInOperationalMetricDefinitions.UtilizationId, first.Key.ContextKey);
        Assert.Null(await Reader.ReadDetailAsync(batch.Processor, missingKey, default));
    }

    [Fact]
    public async Task RetainedOlderRowsAndLatestCheckpointKeepTheirIndependentHistoricalCuts()
    {
        var (first, next) = await new SqlContainmentHistoricalResultIntegrationTests(fixture).CreatePublicationAdvanceEvidenceAsync();
        var prior = ContainmentArchiveBatch.Preflight(first); var proposed = ContainmentArchiveBatch.Preflight(next);
        var publisher = new SqlContainmentPublicationTransaction(fixture.ConnectionString);
        await publisher.PublishAsync(first, null, default);
        var expected = new OperationalMetricProjectionCheckpoint(prior.Processor, prior.Cut,
            new OperationalMetricProjectionBatchManifest(prior.Results.Select(r => r.Document.Key)));
        await publisher.PublishAsync(next, expected, default);
        foreach (var old in prior.Results)
        {
            var actual = await Reader.ReadDetailAsync(prior.Processor, old.Document.Key, default);
            Assert.NotNull(actual);
            Assert.Equal(old.Document.Key == proposed.Results[0].Document.Key ? proposed.Cut : prior.Cut, actual.SourceRevision);
        }
        var checkpoint = await new SqlServerOperationalMetricProjectionStore(fixture.ConnectionString).ReadCheckpointAsync(prior.Processor, prior.Cut.StreamId, default);
        Assert.NotNull(checkpoint); Assert.Equal(proposed.Cut, checkpoint.SourceRevision);
        Assert.Equal(proposed.Results[0].Document.Key, Assert.Single(checkpoint.BatchManifest.ProjectionKeys));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingOrIncorrectProcessorPolicyNeverFallsBackToLegacy(bool policy)
    {
        var batch = await Publish();
        await using var c = fixture.CreateConnection(); await c.OpenAsync();
        await using var command = c.CreateCommand(); command.Parameters.AddWithValue("@Name", batch.Processor.Value);
        command.CommandText = "ALTER TABLE dbo.OperationalMetricProjectionProcessor NOCHECK CONSTRAINT CK_OMPP_FormatPolicy; " +
            (policy ? "UPDATE dbo.OperationalMetricProjectionProcessor SET EvaluationPolicyId=N'wrong-policy' WHERE ProcessorKey=@Name;" :
                "UPDATE dbo.OperationalMetricProjectionProcessor SET EvaluationPolicyId=NULL WHERE ProcessorKey=@Name;") +
            " ALTER TABLE dbo.OperationalMetricProjectionProcessor CHECK CONSTRAINT CK_OMPP_FormatPolicy;";
        // Foreign keys bind format numerically, so corrupt the policy only; both invalid policy shapes must reject.
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() => Reader.ReadDetailAsync(batch.Processor, batch.Results[0].Document.Key, default).AsTask());
    }

    [Theory]
    [InlineData("manifest")] [InlineData("projection")] [InlineData("evidence")]
    [InlineData("dependency")][InlineData("archive")][InlineData("reference")][InlineData("witness")][InlineData("checkpoint")]
    public async Task CorruptionFailsClosedForBothDetailAndSummariesWithoutRepair(string kind)
    {
        var batch = await Publish(); var first = batch.Results[0].Document;
        await using var c = fixture.CreateConnection(); await c.OpenAsync();
        await using (var mutate = c.CreateCommand())
        {
            mutate.Parameters.AddWithValue("@Name", batch.Processor.Value);
            var processor = "(SELECT OperationalMetricProjectionProcessorRowId FROM dbo.OperationalMetricProjectionProcessor WHERE ProcessorKey=@Name)";
            var retained = " IN (SELECT OperationalMetricProjectionRowId FROM dbo.OperationalMetricProjection WHERE OperationalMetricProjectionProcessorRowId=" + processor + ")";
            mutate.CommandText = kind switch
            {
                "checkpoint" => "DELETE dbo.OperationalMetricProjectionCheckpoint WHERE OperationalMetricProjectionProcessorRowId=" + processor,
                "manifest" => "DELETE dbo.OperationalMetricProjectionManifest WHERE OperationalMetricProjectionProcessorRowId=" + processor,
                "projection" => "UPDATE dbo.OperationalMetricProjection SET Unit=N'corrupt' WHERE OperationalMetricProjectionProcessorRowId=" + processor,
                "evidence" => "DELETE dbo.OperationalMetricProjectionEvidence WHERE OperationalMetricProjectionRowId" + retained,
                "dependency" => "UPDATE dbo.OperationalMetricProjectionEvidence SET DependencySnapshotHash=0x0000000000000000000000000000000000000000000000000000000000000000 WHERE EvidenceKind=2 AND OperationalMetricProjectionRowId" + retained,
                "archive" => "DISABLE TRIGGER dbo.TR_OMPRA_Immutable ON dbo.OperationalMetricPublicationResultArchive; UPDATE dbo.OperationalMetricPublicationResultArchive SET ResultPayload=0x01 WHERE OperationalMetricProjectionProcessorRowId=" + processor + "; ENABLE TRIGGER dbo.TR_OMPRA_Immutable ON dbo.OperationalMetricPublicationResultArchive;",
                "reference" => "DISABLE TRIGGER dbo.TR_OMRWR_Immutable ON dbo.OperationalMetricResultWitnessReference; DELETE x FROM dbo.OperationalMetricResultWitnessReference x JOIN dbo.OperationalMetricPublicationResultArchive r ON r.ResultId=x.ResultId WHERE r.OperationalMetricProjectionProcessorRowId=" + processor + "; ENABLE TRIGGER dbo.TR_OMRWR_Immutable ON dbo.OperationalMetricResultWitnessReference;",
                _ => "DISABLE TRIGGER dbo.TR_OMCWA_Immutable ON dbo.OperationalMetricConflictWitnessArchive; UPDATE w SET WitnessPayload=0x01 FROM dbo.OperationalMetricConflictWitnessArchive w JOIN dbo.OperationalMetricProjectionProcessor p ON p.MetricAggregationProcessorRowId=w.MetricAggregationProcessorRowId WHERE p.OperationalMetricProjectionProcessorRowId=" + processor + "; ENABLE TRIGGER dbo.TR_OMCWA_Immutable ON dbo.OperationalMetricConflictWitnessArchive;"
            };
            await mutate.ExecuteNonQueryAsync();
        }
        var damaged = await SqlContainmentPublicationIntegrationTests.Snapshot(c, null);
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() => Reader.ReadDetailAsync(batch.Processor, first.Key, default).AsTask());
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() => Reader.ReadPeriodSummariesAsync(batch.Processor, first.Key.MachineId, first.Key.PeriodId, first.Key.ContextKey, default).AsTask());
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() => new SqlServerOperationalMetricProjectionStore(fixture.ConnectionString).ReadCheckpointAsync(batch.Processor, batch.Cut.StreamId, default).AsTask());
        Assert.Equal(damaged, await SqlContainmentPublicationIntegrationTests.Snapshot(c, null));
    }

    [Fact]
    public async Task UnpublishedRegistrationIsEmptyButArchiveOnlyPreparationIsNotReportingAuthority()
    {
        var (envelopes, _) = await new SqlContainmentHistoricalResultIntegrationTests(fixture).CreateArchiveTestEvidenceAsync();
        var batch = ContainmentArchiveBatch.Preflight(envelopes);
        await using var c = fixture.CreateConnection(); await c.OpenAsync();
        await using (var command = c.CreateCommand())
        {
            command.CommandText = """
                INSERT dbo.OperationalMetricProjectionProcessor
                (ProcessorKeyBinary,ProcessorKey,MetricAggregationProcessorRowId,MetricInputStreamRowId,PublicationFormatVersion,EvaluationPolicyId)
                SELECT @Key,@Name,MetricAggregationProcessorRowId,MetricInputStreamRowId,2,@Policy
                FROM dbo.MetricAggregationProcessor WHERE ProcessorKeyBinary=@AP;
                """;
            command.Parameters.Add("@Key", SqlDbType.VarBinary, StringOrderKeyV2Codec.MaximumEncodedLength).Value = StringOrderKeyV2Codec.Encode(batch.Processor.Value);
            command.Parameters.AddWithValue("@Name", batch.Processor.Value); command.Parameters.AddWithValue("@Policy", AvailabilityContainmentV1.PolicyId);
            command.Parameters.Add("@AP", SqlDbType.VarBinary, 512).Value = OrdinalStringKeyCodec.Encode(batch.Cut.ProcessorId.Value);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        Assert.Null(await Reader.ReadDetailAsync(batch.Processor, batch.Results[0].Document.Key, default));
        await using (var tx = c.BeginTransaction(IsolationLevel.Serializable))
        {
            _ = await SqlContainmentArchiveTransaction.PrepareAsync(c, tx, envelopes, false, default);
            await tx.CommitAsync();
        }
        var before = await SqlContainmentPublicationIntegrationTests.Snapshot(c, null);
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() => Reader.ReadDetailAsync(batch.Processor, batch.Results[0].Document.Key, default).AsTask());
        await Assert.ThrowsAsync<ContainmentHistoricalIntegrityException>(() => new SqlServerOperationalMetricProjectionStore(fixture.ConnectionString).ReadCheckpointAsync(batch.Processor, batch.Cut.StreamId, default).AsTask());
        Assert.Equal(before, await SqlContainmentPublicationIntegrationTests.Snapshot(c, null));
    }

    [Fact]
    public async Task MissingHistoricalAuthorityIsDistinctFromCorruptedPublication()
    {
        var batch = await Publish(); var first = batch.Results[0].Document;
        await using var c = fixture.CreateConnection(); await c.OpenAsync();
        await using var command = c.CreateCommand();
        command.CommandText = "DELETE dbo.ProductionReferenceTimePublicationCut WHERE MetricAggregationProcessorRowId=(SELECT MetricAggregationProcessorRowId FROM dbo.MetricAggregationProcessor WHERE ProcessorKey=@AP);";
        command.Parameters.AddWithValue("@AP", batch.Cut.ProcessorId.Value); Assert.True(await command.ExecuteNonQueryAsync() > 0);
        await Assert.ThrowsAsync<MissingContainmentHistoricalAuthorityException>(() => Reader.ReadDetailAsync(batch.Processor, first.Key, default).AsTask());
    }

    [Fact]
    public async Task ReadsUseCallerSerializableTransactionAndDoNotAcquireWriterGateOrMutateArchives()
    {
        var batch = await Publish();
        await using var c = fixture.CreateConnection(); await c.OpenAsync();
        await using (var unstable = c.BeginTransaction(IsolationLevel.ReadCommitted))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => SqlContainmentVerifiedRead.TryReadAsync(c, unstable, batch.Processor, default));
            await unstable.RollbackAsync();
        }
        await using var first = c.BeginTransaction(IsolationLevel.Serializable);
        var before = await SqlContainmentPublicationIntegrationTests.Snapshot(c, first);
        Assert.NotNull(await SqlContainmentVerifiedRead.TryReadAsync(c, first, batch.Processor, default));
        // A second reader must complete while the first still holds its transaction/gate/authority locks.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var detail = await Reader.ReadDetailAsync(batch.Processor, batch.Results[0].Document.Key, timeout.Token);
        Assert.NotNull(detail); Assert.Same(c, first.Connection);
        Assert.Equal(before, await SqlContainmentPublicationIntegrationTests.Snapshot(c, first));
        await first.RollbackAsync();
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task ReportingHttpQueryReturnsContainmentAndFailsClosedOnDamagedEvidence(bool corrupt, bool day)
    {
        var envelopes = await new SqlContainmentHistoricalResultIntegrationTests(fixture).CreateVerifiedReadEvidenceAsync(day);
        await new SqlContainmentPublicationTransaction(fixture.ConnectionString).PublishAsync(envelopes, null, default);
        var batch = ContainmentArchiveBatch.Preflight(envelopes); var first = batch.Results[0].Document;
        if (corrupt)
        {
            await using var c = fixture.CreateConnection(); await c.OpenAsync();
            await using var command = c.CreateCommand(); command.Parameters.AddWithValue("@Name", batch.Processor.Value);
            command.CommandText = "DELETE e FROM dbo.OperationalMetricProjectionEvidence e JOIN dbo.OperationalMetricProjection p ON p.OperationalMetricProjectionRowId=e.OperationalMetricProjectionRowId JOIN dbo.OperationalMetricProjectionProcessor pp ON pp.OperationalMetricProjectionProcessorRowId=p.OperationalMetricProjectionProcessorRowId WHERE pp.ProcessorKey=@Name;";
            await command.ExecuteNonQueryAsync();
        }
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IOperationalMetricReportingQueryProvider>(new SqlServerOperationalMetricReportingQueryProvider(fixture.ConnectionString));
        builder.Services.AddSingleton<IOperationalMetricProjectionQueryReader>(Reader);
        builder.Services.AddSingleton<IMachineShiftOccurrenceRosterStore>(new SqlServerMachineShiftOccurrenceRosterStore(fixture.ConnectionString));
        builder.Services.AddFactoryConnectOperationalMetricReporting();
        await using var app = builder.Build(); app.UseExceptionHandler(); app.MapOperationalMetricReportingEndpoints(); await app.StartAsync();
        HttpResponseMessage response;
        if (day)
        {
            var period = Assert.IsType<OperationalMetricPeriodId.ProductionDay>(first.Key.PeriodId);
            var request = new ProductionDayOperationalMetricQueryRequest([new(first.Key.MachineId.Value, batch.Processor.Value)],
                period.ProductionDayId.BusinessDate, period.ProductionDayId.BusinessDate.AddDays(1), null, null, null, "period-ascending", 100, null);
            response = await app.GetTestClient().PostAsJsonAsync("/api/reporting/v1/operational-metrics/production-days/query", request);
        }
        else
        {
            var shift = Assert.IsType<OperationalMetricPeriodId.Shift>(first.Key.PeriodId);
            var request = new ShiftOperationalMetricQueryRequest([new(first.Key.MachineId.Value, batch.Processor.Value)],
                shift.ShiftOccurrenceId.StartsAtUtc, shift.ShiftOccurrenceId.EndsAtUtc, null, null, null, "period-ascending", 100, null);
            response = await app.GetTestClient().PostAsJsonAsync("/api/reporting/v1/operational-metrics/shifts/query", request);
        }
        if (corrupt) { Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode); return; }
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(5, items.Length);
        var availability = items.Single(i => i.GetProperty("metricKey").GetString() == "Availability");
        Assert.Equal(batch.Processor.Value, availability.GetProperty("processorId").GetString());
        Assert.Equal("unavailable", availability.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, availability.GetProperty("value").ValueKind);
        Assert.Equal("RunningOutsidePlannedProduction", availability.GetProperty("reasonCode").GetString());
    }
}
