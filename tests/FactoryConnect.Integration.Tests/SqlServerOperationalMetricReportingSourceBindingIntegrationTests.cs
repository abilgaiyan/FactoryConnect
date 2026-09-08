using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricReportingSourceBindingIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerOperationalMetricReportingSourceBindingIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReadWindowAsyncFailsWhenRequestedMachineDoesNotMatchPersistedProcessorSourceBinding()
    {
        var source = await CreateSourceAsync();
        var processorId = new OperationalMetricProjectionProcessorId(
            $"reporting-source-binding-{Guid.NewGuid():N}");

        await PublishAsync(
            new OperationalMetricProjectionCommit(
                processorId,
                null,
                new OperationalMetricProjectionCheckpoint(
                    processorId,
                    source.Checkpoint,
                    new OperationalMetricProjectionBatchManifest([])),
                []));

        var wrongMachineId = new MachineId(Guid.NewGuid());
        Assert.NotEqual(source.MachineId, wrongMachineId);

        var query = new ShiftOperationalMetricReportQuery(
            new OperationalMetricReportingSourceSelection([
                new OperationalMetricReportingSource(wrongMachineId, processorId),
            ]),
            new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero),
            null,
            null,
            null,
            OperationalMetricReportOrder.PeriodAscending,
            new ReportingPageRequest(200));

        var provider = new SqlServerOperationalMetricReportingQueryProvider(_fixture.ConnectionString);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await provider.ReadWindowAsync(query, null, 200, CancellationToken.None));

        Assert.Contains("source binding", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(processorId.Value, exception.Message, StringComparison.Ordinal);
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

    private async Task<SourceFixture> CreateSourceAsync()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var fact = await inputStore.AppendAsync(
            CreateAppend(machineId, $"reporting-source-binding-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var aggregationProcessorId = new MetricAggregationProcessorId(
            $"reporting-source-binding-aggregation-{Guid.NewGuid():N}");
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

    private sealed record SourceFixture(
        MachineId MachineId,
        MetricAggregationCheckpoint Checkpoint);
}
