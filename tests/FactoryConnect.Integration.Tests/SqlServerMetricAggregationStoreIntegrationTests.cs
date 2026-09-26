using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMetricAggregationStoreIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMetricAggregationStoreIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AggregationPublicationTransactionCreatesFkBackedEmptyReferenceTimeCut()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var input = await new SqlServerMetricInputStore(_fixture.ConnectionString).AppendAsync(
            CreateAppend(machineId, $"empty-cut-{Guid.NewGuid():N}", 1m, minute: 0),
            CancellationToken.None);
        var processorId = new MetricAggregationProcessorId($"empty-cut-{Guid.NewGuid():N}");
        var revision = new MetricAggregationCheckpoint(processorId, input.StreamId, input.Position);

        await new SqlServerMetricAggregationStore(_fixture.ConnectionString).CommitAsync(
            new MetricAggregationCommit(processorId, null, revision, [input]),
            CancellationToken.None);

        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM dbo.ProductionReferenceTimePublicationCut c
            JOIN dbo.MetricAggregationRevision a
              ON a.MetricAggregationProcessorRowId = c.MetricAggregationProcessorRowId
             AND a.Position = c.MetricAggregationPosition
            JOIN dbo.ProductionReferenceTimeRevision r
              ON r.MetricAggregationProcessorRowId = c.MetricAggregationProcessorRowId
             AND r.ProductionReferenceTimeRevision = c.ProductionReferenceTimeRevision
            JOIN dbo.MetricAggregationProcessor p
              ON p.MetricAggregationProcessorRowId = c.MetricAggregationProcessorRowId
            WHERE p.ProcessorKey = @ProcessorId AND c.MetricAggregationPosition = @Position
              AND r.ProductionReferenceTimeRevision = 0;
            """;
        command.Parameters.AddWithValue("@ProcessorId", processorId.Value);
        command.Parameters.AddWithValue("@Position", (decimal)revision.Position.Value);

        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task OutcomeSourceCannotBeRepublishedAtALaterReferenceTimeRevision()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var input = await new SqlServerMetricInputStore(_fixture.ConnectionString).AppendAsync(
            CreateAppend(machineId, $"source-replay-{Guid.NewGuid():N}", 1m, minute: 0),
            CancellationToken.None);
        var processorId = new MetricAggregationProcessorId($"source-replay-{Guid.NewGuid():N}");
        await new SqlServerMetricAggregationStore(_fixture.ConnectionString).CommitAsync(
            new MetricAggregationCommit(
                processorId,
                null,
                new MetricAggregationCheckpoint(processorId, input.StreamId, input.Position),
                [input]),
            CancellationToken.None);

        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @ProcessorRowId bigint = (
                SELECT MetricAggregationProcessorRowId
                FROM dbo.MetricAggregationProcessor WHERE ProcessorKey = @ProcessorId
            );

            DECLARE @First decimal(20,0);
            SELECT @First = MAX(ProductionReferenceTimeRevision) + 1
            FROM dbo.ProductionReferenceTimeRevision WITH (UPDLOCK, HOLDLOCK)
            WHERE MetricAggregationProcessorRowId = @ProcessorRowId;

            INSERT INTO dbo.ProductionReferenceTimeRevision
                (MetricAggregationProcessorRowId, ProductionReferenceTimeRevision)
            VALUES
                (@ProcessorRowId, @First),
                (@ProcessorRowId, @First + 1);

            INSERT INTO dbo.ProductionReferenceTimeOutcome
                (MetricAggregationProcessorRowId, ProductionReferenceTimeRevision,
                 SourceQuantityEvidenceId, CompanyId, SiteId, MachineId,
                 ShiftOccurrenceSiteId, ShiftScheduleAssignmentId, ShiftId,
                 ShiftStartsAtUtc, ShiftEndsAtUtc, ProductionDaySiteId,
                 ProductionBusinessDate, OccurredAtUtc, ProducedQuantity,
                 ProductionStandardAuthorityRevision, ResolutionStatus)
            VALUES
                (@ProcessorRowId, @First, @SourceId, N'company', N'site', @MachineId,
                 N'site', N'schedule', N'shift',
                 '2026-09-26T00:00:00+00:00', '2026-09-26T08:00:00+00:00', N'site',
                 '2026-09-26', '2026-09-26T00:01:00+00:00', 1, 0, 2);

            INSERT INTO dbo.ProductionReferenceTimeOutcome
                (MetricAggregationProcessorRowId, ProductionReferenceTimeRevision,
                 SourceQuantityEvidenceId, CompanyId, SiteId, MachineId,
                 ShiftOccurrenceSiteId, ShiftScheduleAssignmentId, ShiftId,
                 ShiftStartsAtUtc, ShiftEndsAtUtc, ProductionDaySiteId,
                 ProductionBusinessDate, OccurredAtUtc, ProducedQuantity,
                 ProductionStandardAuthorityRevision, ResolutionStatus)
            VALUES
                (@ProcessorRowId, @First + 1, @SourceId, N'company', N'site', @MachineId,
                 N'site', N'schedule', N'shift',
                 '2026-09-26T00:00:00+00:00', '2026-09-26T08:00:00+00:00', N'site',
                 '2026-09-26', '2026-09-26T00:01:00+00:00', 1, 0, 2);
            """;
        command.Parameters.AddWithValue("@ProcessorId", processorId.Value);
        command.Parameters.AddWithValue("@SourceId", $"source-{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("@MachineId", machineId.Value);

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(2627, exception.Number);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task CommitAtomicallyPersistsBothProjectionsAndCheckpoint()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var first = await inputStore.AppendAsync(
            CreateAppend(machineId, "sql-aggregate-1", 10m, minute: 0),
            CancellationToken.None);
        var second = await inputStore.AppendAsync(
            CreateAppend(machineId, "sql-aggregate-2", 20m, minute: 1),
            CancellationToken.None);
