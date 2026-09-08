using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricProjectionRevisionCoherenceIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerOperationalMetricProjectionRevisionCoherenceIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReadPeriodSummariesAsyncFailsWhenManifestedProjectionRevisionDiffersFromStableCheckpoint()
    {
        var published = await CreatePublishedProjectionAsync();
        await CorruptPublishedRevisionAsync(published.ProcessorRowId);

        var reader = new SqlServerOperationalMetricProjectionSummaryReader(_fixture.ConnectionString);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await reader.ReadPeriodSummariesAsync(
                published.ProcessorId,
                published.MachineId,
                published.Period,
                OperationalMetricEvaluationContextKey.Unpartitioned,
                CancellationToken.None));

        Assert.Contains("source revision", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("checkpoint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadDetailAsyncFailsWhenManifestedProjectionRevisionDiffersFromStableCheckpoint()
    {
        var published = await CreatePublishedProjectionAsync();
        await CorruptPublishedRevisionAsync(published.ProcessorRowId);

        var reader = new SqlServerOperationalMetricProjectionQueryReader(_fixture.ConnectionString);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await reader.ReadDetailAsync(
                published.ProcessorId,
                published.Key,
                CancellationToken.None));

        Assert.Contains("source revision", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("checkpoint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<PublishedFixture> CreatePublishedProjectionAsync()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var fact = await inputStore.AppendAsync(
            CreateAppend(machineId, $"revision-coherence-source-{Guid.NewGuid():N}"),
            CancellationToken.None);

        var aggregationProcessorId = new MetricAggregationProcessorId(
            $"revision-coherence-aggregation-{Guid.NewGuid():N}");
        var sourceRevision = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            fact.StreamId,
            fact.Position);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(aggregationProcessorId, null, sourceRevision, []),
            CancellationToken.None);

        var processorId = new OperationalMetricProjectionProcessorId(
            $"revision-coherence-{Guid.NewGuid():N}");
        var period = CreateShiftPeriod();
        var key = new OperationalMetricEvaluationKey(
            machineId,
            period,
            new OperationalMetricDefinitionId("availability", "1"),
            OperationalMetricEvaluationContextKey.Unpartitioned);
        var projection = new OperationalMetricProjection(
            processorId,
            key,
            OperationalMetricEvaluationStatus.Calculated,
            0.75m,
            "ratio",
            null,
            null,
            sourceRevision);
        var commit = new OperationalMetricProjectionCommit(
            processorId,
            null,
            new OperationalMetricProjectionCheckpoint(
                processorId,
                sourceRevision,
                new OperationalMetricProjectionBatchManifest([key])),
            [projection]);

        var transaction = new SqlServerOperationalMetricProjectionCommitTransaction(_fixture.ConnectionString);
        await transaction.ExecuteAsync(
            commit,
            async (context, cancellationToken) =>
                await SqlServerOperationalMetricProjectionPublication.ExecuteAsync(
                    context,
                    commit,
                    cancellationToken),
            CancellationToken.None);

        var header = Assert.IsType<SqlServerOperationalMetricProjectionCheckpointHeader>(
            await transaction.ReadCheckpointHeaderAsync(processorId, CancellationToken.None));

        return new PublishedFixture(
            processorId,
            header.ProjectionProcessorRowId,
            machineId,
            period,
            key);
    }

    private async Task CorruptPublishedRevisionAsync(long processorRowId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE p
            SET SourceRevisionPosition = SourceRevisionPosition + 1
            FROM dbo.OperationalMetricProjection AS p
            INNER JOIN dbo.OperationalMetricProjectionManifest AS m
                ON m.OperationalMetricProjectionProcessorRowId = p.OperationalMetricProjectionProcessorRowId
                AND m.OperationalMetricProjectionRowId = p.OperationalMetricProjectionRowId
            WHERE p.OperationalMetricProjectionProcessorRowId = @ProcessorRowId;
            """;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
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
                new ShiftScheduleAssignmentId("SCHEDULE-A"),
                new ShiftId("SHIFT-A"),
                new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 7, 14, 0, 0, TimeSpan.Zero)));

    private sealed record PublishedFixture(
        OperationalMetricProjectionProcessorId ProcessorId,
        long ProcessorRowId,
        MachineId MachineId,
        OperationalMetricPeriodId.Shift Period,
        OperationalMetricEvaluationKey Key);
}
