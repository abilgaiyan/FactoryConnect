using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricReaderConformanceIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerOperationalMetricReaderConformanceIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UnknownProcessorIsConsistentlyAbsentAcrossReaders()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var period = CreateShiftPeriod();
        var context = OperationalMetricEvaluationContextKey.Unpartitioned;
        var key = CreateKey(source.MachineId, period, "availability", "1", context);

        var projectionReader = new SqlServerOperationalMetricProjectionQueryReader(_fixture.ConnectionString);
        var reportReader = new OperationalMetricReportReader(projectionReader);
        var provider = new SqlServerOperationalMetricReportingQueryProvider(_fixture.ConnectionString);
        var queryReader = new OperationalMetricReportingQueryReader(provider);
        var query = CreateShiftQuery(source.MachineId, processorId, period);

        var summaries = await projectionReader.ReadPeriodSummariesAsync(
            processorId,
            source.MachineId,
            period,
            context,
            CancellationToken.None);
        var detail = await projectionReader.ReadDetailAsync(
            processorId,
            key,
            CancellationToken.None);
        var report = await reportReader.ReadShiftAsync(
            processorId,
            source.MachineId,
            period.ShiftOccurrenceId,
            context,
            CancellationToken.None);
        var window = await provider.ReadWindowAsync(
            query,
            null,
            200,
            CancellationToken.None);
        var page = await queryReader.ReadAsync(query, CancellationToken.None);

        Assert.Empty(summaries);
        Assert.Null(detail);
        Assert.Null(report);
        Assert.Empty(window);
        Assert.Empty(page.Items);
        Assert.Null(page.ContinuationToken);
    }

    [Fact]
    public async Task MissingCheckpointWithEmptyManifestIsConsistentlyAbsentAcrossReaders()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var period = CreateShiftPeriod();
        var context = OperationalMetricEvaluationContextKey.Unpartitioned;
        var key = CreateKey(source.MachineId, period, "availability", "1", context);

        await PublishAsync(CreateInitialCommit(processorId, source.Checkpoint, []));
        var header = await ReadCheckpointHeaderAsync(processorId);
        await DeleteCheckpointAsync(header.ProjectionProcessorRowId);

        var projectionReader = new SqlServerOperationalMetricProjectionQueryReader(_fixture.ConnectionString);
        var reportReader = new OperationalMetricReportReader(projectionReader);
        var provider = new SqlServerOperationalMetricReportingQueryProvider(_fixture.ConnectionString);
        var queryReader = new OperationalMetricReportingQueryReader(provider);
        var query = CreateShiftQuery(source.MachineId, processorId, period);

        var summaries = await projectionReader.ReadPeriodSummariesAsync(
            processorId,
            source.MachineId,
            period,
            context,
            CancellationToken.None);
        var detail = await projectionReader.ReadDetailAsync(
            processorId,
            key,
            CancellationToken.None);
        var report = await reportReader.ReadShiftAsync(
            processorId,
            source.MachineId,
            period.ShiftOccurrenceId,
            context,
            CancellationToken.None);
        var window = await provider.ReadWindowAsync(
            query,
            null,
            200,
            CancellationToken.None);
        var page = await queryReader.ReadAsync(query, CancellationToken.None);

        Assert.Empty(summaries);
        Assert.Null(detail);
        Assert.Null(report);
        Assert.Empty(window);
        Assert.Empty(page.Items);
        Assert.Null(page.ContinuationToken);
    }

    [Fact]
    public async Task CurrentPublicationIsConsistentAcrossSummaryDetailWindowAndPublicReaders()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var period = CreateShiftPeriod();
        var context = OperationalMetricEvaluationContextKey.Unpartitioned;
        var key = CreateKey(source.MachineId, period, "availability", "1", context);
        var projection = CreateCalculated(
            processorId,
            key,
            source.Checkpoint,
            0.625m);
        await PublishAsync(CreateInitialCommit(
            processorId,
            source.Checkpoint,
            [projection]));

        var projectionReader = new SqlServerOperationalMetricProjectionQueryReader(_fixture.ConnectionString);
        var reportReader = new OperationalMetricReportReader(projectionReader);
        var provider = new SqlServerOperationalMetricReportingQueryProvider(_fixture.ConnectionString);
        var queryReader = new OperationalMetricReportingQueryReader(provider);
        var query = CreateShiftQuery(source.MachineId, processorId, period);

        var summary = Assert.Single(await projectionReader.ReadPeriodSummariesAsync(
            processorId,
            source.MachineId,
            period,
            context,
            CancellationToken.None));
        var detail = Assert.IsType<OperationalMetricProjection>(await projectionReader.ReadDetailAsync(
            processorId,
            key,
            CancellationToken.None));
        var report = Assert.IsType<ShiftOperationalMetricReport>(await reportReader.ReadShiftAsync(
            processorId,
            source.MachineId,
            period.ShiftOccurrenceId,
            context,
            CancellationToken.None));
        var reportDetail = Assert.IsType<OperationalMetricReportDetail>(await reportReader.ReadMetricDetailAsync(
            processorId,
            source.MachineId,
            period,
            context,
            key.DefinitionId,
            CancellationToken.None));
        var windowSummary = Assert.Single(await provider.ReadWindowAsync(
            query,
            null,
            200,
            CancellationToken.None));
        var pageSummary = Assert.Single((await queryReader.ReadAsync(
            query,
            CancellationToken.None)).Items);

        Assert.Equal(key, summary.Key);
        Assert.Equal(key, detail.Key);
        Assert.Equal(key, windowSummary.Key);
        Assert.Equal(key, pageSummary.Key);
        Assert.Equal(source.Checkpoint, summary.SourceRevision);
        Assert.Equal(source.Checkpoint, detail.SourceRevision);
        Assert.Equal(source.Checkpoint, windowSummary.SourceRevision);
        Assert.Equal(source.Checkpoint, pageSummary.SourceRevision);
        Assert.Equal(source.Checkpoint, report.SourceRevision);
        Assert.Equal(source.Checkpoint, reportDetail.SourceRevision);
        Assert.Equal(summary.Status, detail.Status);
        Assert.Equal(summary.Status, windowSummary.Status);
        Assert.Equal(summary.Status, pageSummary.Status);
        Assert.Equal(summary.Value, detail.Value);
        Assert.Equal(summary.Value, windowSummary.Value);
        Assert.Equal(summary.Value, pageSummary.Value);
        var reportItem = Assert.Single(report.Metrics);
        Assert.Equal(key.DefinitionId, reportItem.DefinitionId);
        Assert.Equal(summary.Status, reportItem.Status);
        Assert.Equal(summary.Value, reportItem.Value);
        Assert.Equal(summary.Status, reportDetail.Status);
        Assert.Equal(summary.Value, reportDetail.Value);
    }

    [Fact]
    public async Task PublicQueryReaderPropagatesPreCancelledRead()
    {
        var source = await CreateSourceAsync();
        var processorId = NewProcessorId();
        var period = CreateShiftPeriod();
        var reader = new OperationalMetricReportingQueryReader(
            new SqlServerOperationalMetricReportingQueryProvider(_fixture.ConnectionString));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await reader.ReadAsync(
                CreateShiftQuery(source.MachineId, processorId, period),
                cancellation.Token));
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
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
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
        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        var header = await transaction.ReadCheckpointHeaderAsync(processorId, CancellationToken.None);
        return Assert.IsType<SqlServerOperationalMetricProjectionCheckpointHeader>(header);
    }

    private async Task<SourceFixture> CreateSourceAsync()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var fact = await inputStore.AppendAsync(
            CreateAppend(machineId, $"reader-conformance-source-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var aggregationProcessorId = new MetricAggregationProcessorId(
            $"reader-conformance-aggregation-{Guid.NewGuid():N}");
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
        new(machineId, periodId, new OperationalMetricDefinitionId(metricKey, version), contextKey);

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
        new($"reader-conformance-{Guid.NewGuid():N}");

    private sealed record SourceFixture(
        MachineId MachineId,
        MetricAggregationCheckpoint Checkpoint);
}
