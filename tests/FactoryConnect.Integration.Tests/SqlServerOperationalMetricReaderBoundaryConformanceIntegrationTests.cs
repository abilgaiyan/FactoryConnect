using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricReaderBoundaryConformanceIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private static readonly TimeSpan InFlightObservationDelay = TimeSpan.FromMilliseconds(250);

    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerOperationalMetricReaderBoundaryConformanceIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MissingCheckpointWithNonEmptyPublicationIsCorruptionAcrossPublicReaders()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var period = CreateShiftPeriod();
        var context = OperationalMetricEvaluationContextKey.Unpartitioned;
        var key = CreateKey(source.MachineId, period, "availability", "1", context);
        var projection = CreateCalculated(processorId, key, source.Checkpoint, 0.625m);

        await PublishAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [projection]));
        var header = await ReadCheckpointHeaderAsync(processorId);
        await DeleteCheckpointAsync(header.ProjectionProcessorRowId);

        var projectionReader = new SqlServerOperationalMetricProjectionQueryReader(
            _fixture.ConnectionString);
        var provider = new SqlServerOperationalMetricReportingQueryProvider(
            _fixture.ConnectionString);
        var queryReader = new OperationalMetricReportingQueryReader(provider);
        var query = CreateShiftQuery(source.MachineId, processorId, period);

        var summaryException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await projectionReader.ReadPeriodSummariesAsync(
                processorId,
                source.MachineId,
                period,
                context,
                CancellationToken.None));
        var providerException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await provider.ReadWindowAsync(
                query,
                null,
                200,
                CancellationToken.None));
        var publicException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await queryReader.ReadAsync(query, CancellationToken.None));

        Assert.Contains("corrupt", summaryException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("corrupt", providerException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("corrupt", publicException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublicSqlReaderDiscardsCheckpointChangedAttemptAndReturnsStableRetry()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var period = CreateShiftPeriod();
        var context = OperationalMetricEvaluationContextKey.Unpartitioned;
        var key = CreateKey(source.MachineId, period, "availability", "1", context);
        var initialProjection = CreateCalculated(processorId, key, source.Checkpoint, 0.625m);

        await PublishAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [initialProjection]));
        var header = await ReadCheckpointHeaderAsync(processorId);
        var advancedCheckpoint = await AdvanceSourceAsync(source);

        await using var blockerConnection = _fixture.CreateConnection();
        await blockerConnection.OpenAsync();
        await using var blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            CancellationToken.None);
        await AcquireManifestTableLockAsync(blockerConnection, blockerTransaction);

        var reader = new OperationalMetricReportingQueryReader(
            new SqlServerOperationalMetricReportingQueryProvider(_fixture.ConnectionString));
        var readTask = reader.ReadAsync(
            CreateShiftQuery(source.MachineId, processorId, period),
            CancellationToken.None).AsTask();

        await Task.Delay(InFlightObservationDelay);
        Assert.False(readTask.IsCompleted);

        await RewritePublishedRevisionAsync(
            blockerConnection,
            blockerTransaction,
            header.ProjectionProcessorRowId,
            advancedCheckpoint.Position,
            "0.875");
        await blockerTransaction.CommitAsync();

        var page = await readTask;
        var summary = Assert.Single(page.Items);

        Assert.Equal(key, summary.Key);
        Assert.Equal(advancedCheckpoint, summary.SourceRevision);
        Assert.Equal(0.875m, summary.Value);
    }

    [Fact]
    public async Task PublicSqlReaderPropagatesInFlightCancellation()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var period = CreateShiftPeriod();
        var context = OperationalMetricEvaluationContextKey.Unpartitioned;
        var key = CreateKey(source.MachineId, period, "availability", "1", context);
        var projection = CreateCalculated(processorId, key, source.Checkpoint, 0.625m);

        await PublishAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [projection]));

        await using var blockerConnection = _fixture.CreateConnection();
        await blockerConnection.OpenAsync();
        await using var blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            CancellationToken.None);
        await AcquireManifestTableLockAsync(blockerConnection, blockerTransaction);

        var reader = new OperationalMetricReportingQueryReader(
            new SqlServerOperationalMetricReportingQueryProvider(_fixture.ConnectionString));
        using var cancellation = new CancellationTokenSource();
        var readTask = reader.ReadAsync(
            CreateShiftQuery(source.MachineId, processorId, period),
            cancellation.Token).AsTask();

        await Task.Delay(InFlightObservationDelay);
        Assert.False(readTask.IsCompleted);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await readTask);
        await blockerTransaction.RollbackAsync(CancellationToken.None);
    }

    private static async Task AcquireManifestTableLockAsync(
        SqlConnection connection,
        SqlTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM dbo.OperationalMetricProjectionManifest WITH (TABLOCKX, HOLDLOCK);
            """;
        _ = await command.ExecuteScalarAsync(CancellationToken.None);
    }

    private static async Task RewritePublishedRevisionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long processorRowId,
        MetricInputPosition position,
        string metricValue)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE dbo.OperationalMetricProjectionCheckpoint
            SET Position = @Position
            WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId;

            UPDATE dbo.OperationalMetricProjection
            SET SourceRevisionPosition = @Position,
                MetricValue = @MetricValue
            WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId;
            """;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", position.Value));
        command.Parameters.Add("@MetricValue", SqlDbType.NVarChar, 128).Value = metricValue;
        Assert.Equal(2, await command.ExecuteNonQueryAsync(CancellationToken.None));
    }

    private async Task DeleteCheckpointAsync(long processorRowId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM dbo.OperationalMetricProjectionCheckpoint
            WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId;
            """;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task PublishAsync(OperationalMetricProjectionCommit commit)
    {
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);
        await transaction.ExecuteAsync(
            commit,
            async (context, cancellationToken) =>
                await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(
                    context,
                    commit,
                    cancellationToken),
            CancellationToken.None);
    }

    private async Task<SqlServerOperationalMetricProjectionCheckpointHeader> ReadCheckpointHeaderAsync(
        OperationalMetricProjectionProcessorId processorId)
    {
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(
            _fixture.ConnectionString);
        var header = await transaction.ReadCheckpointHeaderAsync(
            processorId,
            CancellationToken.None);
        return Assert.IsType<SqlServerOperationalMetricProjectionCheckpointHeader>(header);
    }

    private async Task<SourceFixture> CreateSourceAsync()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var fact = await inputStore.AppendAsync(
            CreateAppend(machineId, $"reader-boundary-source-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var aggregationProcessorId = new MetricAggregationProcessorId(
            $"reader-boundary-aggregation-{Guid.NewGuid():N}");
        var checkpoint = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            fact.StreamId,
            fact.Position);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(aggregationProcessorId, null, checkpoint, []),
            CancellationToken.None);
        return new SourceFixture(machineId, checkpoint);
    }

    private async Task<MetricAggregationCheckpoint> AdvanceSourceAsync(SourceFixture source)
    {
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var fact = await inputStore.AppendAsync(
            CreateAppend(source.MachineId, $"reader-boundary-source-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var checkpoint = new MetricAggregationCheckpoint(
            source.Checkpoint.ProcessorId,
            source.Checkpoint.StreamId,
            fact.Position);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(
                source.Checkpoint.ProcessorId,
                source.Checkpoint,
                checkpoint,
                []),
            CancellationToken.None);
        return checkpoint;
    }

    private static DurableMetricInputAppend CreateAppend(MachineId machineId, string factId)
    {
        var siteId = new SiteId("SITE-1");
        var shiftId = new ShiftId("SHIFT-A");
        var scheduleId = new ShiftScheduleAssignmentId("SCHEDULE-A");
        var start = new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero);
        var fact = new DurableMetricInputFact
        {
            Id = new MetricInputFactId(factId),
            Key = "running-duration",
            Value = 1m,
            Unit = "seconds",
            StartsAtUtc = start,
            EndsAtUtc = start.AddSeconds(1),
            CompanyId = new CompanyId("COMPANY-1"),
            SiteId = siteId,
            ProductionLineId = new ProductionLineId("LINE-1"),
            MachineId = machineId,
            ShiftId = shiftId,
            ShiftScheduleAssignmentId = scheduleId,
        };

        return new DurableMetricInputAppend(
            MetricInputStreamId.ForMachine(machineId),
            fact,
            new ShiftOccurrenceId(siteId, scheduleId, shiftId, start, start.AddHours(8)),
            new ProductionDayId(siteId, DateOnly.FromDateTime(start.UtcDateTime)));
    }

    private static OperationalMetricPeriodId.Shift CreateShiftPeriod() =>
        new(
            new ShiftOccurrenceId(
                new SiteId("SITE-1"),
                new ShiftScheduleAssignmentId("SCHEDULE-6"),
                new ShiftId("SHIFT-6"),
                new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 7, 14, 0, 0, TimeSpan.Zero)));

    private static ShiftOperationalMetricReportQuery CreateShiftQuery(
        MachineId machineId,
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricPeriodId.Shift period) =>
        new(
            new OperationalMetricReportingSourceSelection([
                new OperationalMetricReportingSource(machineId, processorId),
            ]),
            period.ShiftOccurrenceId.StartsAtUtc,
            period.ShiftOccurrenceId.EndsAtUtc,
            null,
            null,
            null,
            OperationalMetricReportOrder.PeriodAscending,
            new ReportingPageRequest(200));

    private static OperationalMetricEvaluationKey CreateKey(
        MachineId machineId,
        OperationalMetricPeriodId periodId,
        string metricKey,
        string version,
        OperationalMetricEvaluationContextKey contextKey) =>
        new(
            machineId,
            periodId,
            new OperationalMetricDefinitionId(metricKey, version),
            contextKey);

    private static OperationalMetricProjection CreateCalculated(
        OperationalMetricProjectionProcessorId processorId,
        OperationalMetricEvaluationKey key,
        MetricAggregationCheckpoint revision,
        decimal value) =>
        new(
            processorId,
            key,
            OperationalMetricEvaluationStatus.Calculated,
            value,
            "ratio",
            null,
            null,
            revision);

    private static OperationalMetricProjectionCommit CreateInitialCommit(
        OperationalMetricProjectionProcessorId processorId,
        MetricAggregationCheckpoint revision,
        IReadOnlyList<OperationalMetricProjection> projections) =>
        new(
            processorId,
            null,
            new OperationalMetricProjectionCheckpoint(
                processorId,
                revision,
                new OperationalMetricProjectionBatchManifest(
                    projections.Select(static projection => projection.Key))),
            projections);

    private static OperationalMetricProjectionProcessorId NewProcessorId() =>
        new($"reader-boundary-{Guid.NewGuid():N}");

    private sealed record SourceFixture(
        MachineId MachineId,
        MetricAggregationCheckpoint Checkpoint);
}
