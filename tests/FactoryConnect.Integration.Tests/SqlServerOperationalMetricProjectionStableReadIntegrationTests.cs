using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerOperationalMetricProjectionStableReadIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerOperationalMetricProjectionStableReadIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ExecuteAsyncDiscardsChangedCheckpointAttemptAndReturnsStableRetry()
    {
        var processorRowId = await CreateEmptyPublicationAsync();
        var attempts = 0;

        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();

        var result = await SqlServerOperationalMetricProjectionStableRead.ExecuteAsync(
            connection,
            processorRowId,
            async (_, cancellationToken) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    await AdvanceCheckpointPositionAsync(processorRowId, cancellationToken);
                }

                return attempts;
            },
            CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task ExecuteAsyncFailsAfterFrozenRetryBudgetWhenEveryAttemptIsUnstable()
    {
        var processorRowId = await CreateEmptyPublicationAsync();
        var attempts = 0;

        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await SqlServerOperationalMetricProjectionStableRead.ExecuteAsync(
                connection,
                processorRowId,
                async (_, cancellationToken) =>
                {
                    attempts++;
                    await AdvanceCheckpointPositionAsync(processorRowId, cancellationToken);
                    return attempts;
                },
                CancellationToken.None));

        Assert.Equal(SqlServerOperationalMetricProjectionStableRead.MaximumAttempts, attempts);
        Assert.Contains("retry budget", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<long> CreateEmptyPublicationAsync()
    {
        var sourceRevision = await CreateSourceRevisionAsync();
        var processorId = new OperationalMetricProjectionProcessorId(
            $"stable-read-{Guid.NewGuid():N}");
        var commit = new OperationalMetricProjectionCommit(
            processorId,
            null,
            new OperationalMetricProjectionCheckpoint(
                processorId,
                sourceRevision,
                OperationalMetricProjectionBatchManifest.Empty),
            []);

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

        var header = await transaction.ReadCheckpointHeaderAsync(
            processorId,
            CancellationToken.None);
        return Assert.IsType<SqlServerOperationalMetricProjectionCheckpointHeader>(header)
            .ProjectionProcessorRowId;
    }

    private async Task<MetricAggregationCheckpoint> CreateSourceRevisionAsync()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var fact = await inputStore.AppendAsync(
            CreateAppend(machineId, $"stable-read-source-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var aggregationProcessorId = new MetricAggregationProcessorId(
            $"stable-read-aggregation-{Guid.NewGuid():N}");
        var checkpoint = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            fact.StreamId,
            fact.Position);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(aggregationProcessorId, null, checkpoint, []),
            CancellationToken.None);
        return checkpoint;
    }

    private async Task AdvanceCheckpointPositionAsync(
        long processorRowId,
        CancellationToken cancellationToken)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.OperationalMetricProjectionCheckpoint
            SET Position = Position + 1
            WHERE OperationalMetricProjectionProcessorRowId = @ProcessorRowId;
            """;
        command.Parameters.Add("@ProcessorRowId", SqlDbType.BigInt).Value = processorRowId;
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
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
}
