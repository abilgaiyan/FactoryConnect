using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerProductionDayShiftOperationalMetricConformanceIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerProductionDayShiftOperationalMetricConformanceIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PublicProductionDayPagingReturnsOnlyRosteredShiftWithCommittedRevision()
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId(
            $"production-day-shift-{Guid.NewGuid():N}");
        var siteId = new SiteId("SITE-1");
        var lineId = new ProductionLineId("LINE-1");
        var productionDayId = new ProductionDayId(siteId, new DateOnly(2026, 9, 7));
        var rosteredOccurrence = new ShiftOccurrenceId(
            siteId,
            new ShiftScheduleAssignmentId("SCHEDULE-A"),
            new ShiftId("SHIFT-A"),
            new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 7, 14, 0, 0, TimeSpan.Zero));
        var offRosterOccurrence = new ShiftOccurrenceId(
            siteId,
            new ShiftScheduleAssignmentId("SCHEDULE-B"),
            new ShiftId("SHIFT-B"),
            new DateTimeOffset(2026, 9, 7, 14, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 7, 22, 0, 0, TimeSpan.Zero));
        var context = OperationalMetricEvaluationContextKey.Unpartitioned;

        var rosteredProjection = CreateCalculated(
            processorId,
            new OperationalMetricEvaluationKey(
                source.MachineId,
                new OperationalMetricPeriodId.Shift(rosteredOccurrence),
                new OperationalMetricDefinitionId("availability", "1"),
                context),
            source.Checkpoint,
            0.8m);
        var offRosterProjection = CreateCalculated(
            processorId,
            new OperationalMetricEvaluationKey(
                source.MachineId,
                new OperationalMetricPeriodId.Shift(offRosterOccurrence),
                new OperationalMetricDefinitionId("availability", "1"),
                context),
            source.Checkpoint,
            0.6m);

        await PublishAsync(
            new OperationalMetricProjectionCommit(
                processorId,
                null,
                new OperationalMetricProjectionCheckpoint(
                    processorId,
                    source.Checkpoint,
                    new OperationalMetricProjectionBatchManifest(
                        [rosteredProjection.Key, offRosterProjection.Key])),
                [rosteredProjection, offRosterProjection]));

        IMachineShiftOccurrenceRosterStore rosterStore =
            new SqlServerMachineShiftOccurrenceRosterStore(_fixture.ConnectionString);
        await rosterStore.CommitAsync(
            new MachineShiftOccurrenceRosterCommit(
                null,
                new MachineShiftOccurrenceRoster(
                    source.MachineId,
                    lineId,
                    productionDayId,
                    new MachineShiftOccurrenceRosterRevision(1),
                    [
                        new MachineShiftOccurrenceOwnership(
                            source.MachineId,
                            lineId,
                            rosteredOccurrence,
                            productionDayId),
                    ])),
            CancellationToken.None);

        IOperationalMetricProjectionQueryReader projectionReader =
            new SqlServerOperationalMetricProjectionQueryReader(_fixture.ConnectionString);
        IOperationalMetricReportReader metricReader =
            new OperationalMetricReportReader(projectionReader);
        IProductionDayShiftOperationalMetricReader productionDayReader =
            new ProductionDayShiftOperationalMetricReader(rosterStore, metricReader);
        IProductionDayShiftOperationalMetricQueryReader pagingReader =
            new ProductionDayShiftOperationalMetricQueryReader(productionDayReader);

        var reportingSource = new OperationalMetricReportingSource(
            source.MachineId,
            processorId);
        var selection = new ProductionDayShiftOperationalMetricQuery(
            [new ProductionDayShiftReportingSource(reportingSource, productionDayId)],
            context);
        var page = await pagingReader.ReadAsync(
            new ProductionDayShiftOperationalMetricPageQuery(
                selection,
                new ReportingPageRequest(10)),
            CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Null(page.ContinuationToken);
        Assert.Equal(source.MachineId, item.Source.MachineId);
        Assert.Equal(processorId, item.Source.ProcessorId);
        Assert.Equal(productionDayId, item.ProductionDayId);
        Assert.Equal(lineId, item.ProductionLineId);
        Assert.Equal(rosteredOccurrence, item.ShiftOccurrenceId);
        Assert.Equal(context, item.ContextKey);
        Assert.NotNull(item.SourceRevision);
        Assert.Equal(source.Checkpoint, item.SourceRevision);

        var metric = Assert.Single(item.Metrics);
        Assert.Equal(new OperationalMetricDefinitionId("availability", "1"), metric.DefinitionId);
        Assert.Equal(OperationalMetricEvaluationStatus.Calculated, metric.Status);
        Assert.Equal(0.8m, metric.Value);

        Assert.DoesNotContain(
            page.Items,
            report => report.ShiftOccurrenceId == offRosterOccurrence);
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

    private async Task<SourceFixture> CreateSourceAsync()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var fact = await inputStore.AppendAsync(
            CreateAppend(machineId, $"production-day-shift-source-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var aggregationProcessorId = new MetricAggregationProcessorId(
            $"production-day-shift-aggregation-{Guid.NewGuid():N}");
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
            EndsAtUtc = start.AddMinutes(1),
            CompanyId = new CompanyId("COMP-1"),
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

    private sealed record SourceFixture(
        MachineId MachineId,
        MetricAggregationCheckpoint Checkpoint);
}
