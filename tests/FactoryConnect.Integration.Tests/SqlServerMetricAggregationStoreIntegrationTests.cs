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

    public SqlServerMetricAggregationStoreIntegrationTests(SqlServerTestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AggregationPublicationTransactionCreatesFkBackedEmptyReferenceTimeCut()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var input = await new SqlServerMetricInputStore(_fixture.ConnectionString).AppendAsync(CreateAppend(machineId, $"empty-cut-{Guid.NewGuid():N}", 1m, 0), CancellationToken.None);
        var processorId = new MetricAggregationProcessorId($"empty-cut-{Guid.NewGuid():N}");
        var revision = new MetricAggregationCheckpoint(processorId, input.StreamId, input.Position);
        await new SqlServerMetricAggregationStore(_fixture.ConnectionString).CommitAsync(new MetricAggregationCommit(processorId, null, revision, [input]), CancellationToken.None);

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
        var input = await new SqlServerMetricInputStore(_fixture.ConnectionString).AppendAsync(CreateAppend(machineId, $"source-replay-{Guid.NewGuid():N}", 1m, 0), CancellationToken.None);
        var processorId = new MetricAggregationProcessorId($"source-replay-{Guid.NewGuid():N}");
        await new SqlServerMetricAggregationStore(_fixture.ConnectionString).CommitAsync(new MetricAggregationCommit(processorId, null, new MetricAggregationCheckpoint(processorId, input.StreamId, input.Position), [input]), CancellationToken.None);

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
            VALUES (@ProcessorRowId, @First), (@ProcessorRowId, @First + 1);
            INSERT INTO dbo.ProductionReferenceTimeOutcome
                (MetricAggregationProcessorRowId, ProductionReferenceTimeRevision, SourceQuantityEvidenceId, CompanyId, SiteId, MachineId,
                 ShiftOccurrenceSiteId, ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc, ProductionDaySiteId,
                 ProductionBusinessDate, OccurredAtUtc, ProducedQuantity, ProductionStandardAuthorityRevision, ResolutionStatus)
            VALUES
                (@ProcessorRowId, @First, @SourceId, N'company', N'site', @MachineId, N'site', N'schedule', N'shift',
                 '2026-09-26T00:00:00+00:00', '2026-09-26T08:00:00+00:00', N'site', '2026-09-26', '2026-09-26T00:01:00+00:00', 1, 0, 2);
            INSERT INTO dbo.ProductionReferenceTimeOutcome
                (MetricAggregationProcessorRowId, ProductionReferenceTimeRevision, SourceQuantityEvidenceId, CompanyId, SiteId, MachineId,
                 ShiftOccurrenceSiteId, ShiftScheduleAssignmentId, ShiftId, ShiftStartsAtUtc, ShiftEndsAtUtc, ProductionDaySiteId,
                 ProductionBusinessDate, OccurredAtUtc, ProducedQuantity, ProductionStandardAuthorityRevision, ResolutionStatus)
            VALUES
                (@ProcessorRowId, @First + 1, @SourceId, N'company', N'site', @MachineId, N'site', N'schedule', N'shift',
                 '2026-09-26T00:00:00+00:00', '2026-09-26T08:00:00+00:00', N'site', '2026-09-26', '2026-09-26T00:01:00+00:00', 1, 0, 2);
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
        var first = await inputStore.AppendAsync(CreateAppend(machineId, "sql-aggregate-1", 10m, 0), CancellationToken.None);
        var second = await inputStore.AppendAsync(CreateAppend(machineId, "sql-aggregate-2", 20m, 1), CancellationToken.None);
        var processorId = new MetricAggregationProcessorId($"sql-aggregate-{Guid.NewGuid():N}");
        var checkpoint = new MetricAggregationCheckpoint(processorId, first.StreamId, second.Position);
        await aggregationStore.CommitAsync(new MetricAggregationCommit(processorId, null, checkpoint, [first, second]), CancellationToken.None);
        var shift = await aggregationStore.ReadShiftAggregateAsync(processorId, new ShiftMetricAggregateKey(machineId, first.ShiftOccurrenceId, first.Fact.Key), CancellationToken.None);
        var day = await aggregationStore.ReadProductionDayAggregateAsync(processorId, new ProductionDayMetricAggregateKey(machineId, first.ProductionDayId, first.Fact.Key), CancellationToken.None);
        var restored = await aggregationStore.ReadCheckpointAsync(processorId, first.StreamId, CancellationToken.None);
        Assert.NotNull(shift);
        Assert.Equal(30m, shift.Value);
        Assert.Equal(2L, shift.InputCount);
        Assert.Equal(shift, day);
        Assert.Equal(checkpoint, restored);
    }

    [Fact]
    public async Task IdenticalReplayDoesNotInflatePersistedAggregates()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var first = await inputStore.AppendAsync(CreateAppend(machineId, "sql-replay-1", 10m, 10), CancellationToken.None);
        var second = await inputStore.AppendAsync(CreateAppend(machineId, "sql-replay-2", 20m, 11), CancellationToken.None);
        var processorId = new MetricAggregationProcessorId($"sql-replay-{Guid.NewGuid():N}");
        var firstCheckpoint = new MetricAggregationCheckpoint(processorId, first.StreamId, first.Position);
        await aggregationStore.CommitAsync(new MetricAggregationCommit(processorId, null, firstCheckpoint, [first]), CancellationToken.None);
        var secondCheckpoint = new MetricAggregationCheckpoint(processorId, first.StreamId, second.Position);
        await aggregationStore.CommitAsync(new MetricAggregationCommit(processorId, firstCheckpoint, secondCheckpoint, [first, second]), CancellationToken.None);
        var aggregate = await aggregationStore.ReadShiftAggregateAsync(processorId, new ShiftMetricAggregateKey(machineId, first.ShiftOccurrenceId, first.Fact.Key), CancellationToken.None);
        Assert.NotNull(aggregate);
        Assert.Equal(30m, aggregate.Value);
        Assert.Equal(2L, aggregate.InputCount);
    }

    [Fact]
    public async Task StaleCheckpointAndUnseenBehindProgressAreRejectedWithoutMutation()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var first = await inputStore.AppendAsync(CreateAppend(machineId, "sql-progress-1", 10m, 20), CancellationToken.None);
        var second = await inputStore.AppendAsync(CreateAppend(machineId, "sql-progress-2", 20m, 21), CancellationToken.None);
        var third = await inputStore.AppendAsync(CreateAppend(machineId, "sql-progress-3", 30m, 22), CancellationToken.None);
        var processorId = new MetricAggregationProcessorId($"sql-progress-{Guid.NewGuid():N}");
        var acknowledged = new MetricAggregationCheckpoint(processorId, first.StreamId, second.Position);
        await aggregationStore.CommitAsync(new MetricAggregationCommit(processorId, null, acknowledged, []), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await aggregationStore.CommitAsync(new MetricAggregationCommit(processorId, null, new MetricAggregationCheckpoint(processorId, first.StreamId, third.Position), [third]), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await aggregationStore.CommitAsync(new MetricAggregationCommit(processorId, acknowledged, new MetricAggregationCheckpoint(processorId, first.StreamId, third.Position), [first, third]), CancellationToken.None));
        var aggregate = await aggregationStore.ReadShiftAggregateAsync(processorId, new ShiftMetricAggregateKey(machineId, first.ShiftOccurrenceId, first.Fact.Key), CancellationToken.None);
        var restored = await aggregationStore.ReadCheckpointAsync(processorId, first.StreamId, CancellationToken.None);
        Assert.Null(aggregate);
        Assert.Equal(acknowledged, restored);
    }

    [Fact]
    public async Task CommitRejectsPayloadThatDoesNotMatchDurablePositionedFact()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var persisted = await inputStore.AppendAsync(CreateAppend(machineId, "sql-fabricated", 10m, 30), CancellationToken.None);
        var fabricated = new PositionedMetricInputFact(persisted.StreamId, persisted.Position, persisted.Fact with { Value = 999m }, persisted.ShiftOccurrenceId, persisted.ProductionDayId);
        var processorId = new MetricAggregationProcessorId($"sql-fabricated-{Guid.NewGuid():N}");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await aggregationStore.CommitAsync(new MetricAggregationCommit(processorId, null, new MetricAggregationCheckpoint(processorId, persisted.StreamId, persisted.Position), [fabricated]), CancellationToken.None));
        Assert.Null(await aggregationStore.ReadCheckpointAsync(processorId, persisted.StreamId, CancellationToken.None));
    }

    [Fact]
    public async Task RevisionReaderEnumeratesExactCommittedRevisionsIncludingEmptyMembership()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var store = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var first = await inputStore.AppendAsync(CreateAppend(machineId, "sql-revision-1", 10m, 40), CancellationToken.None);
        var second = await inputStore.AppendAsync(CreateAppend(machineId, "sql-revision-2", 20m, 41), CancellationToken.None);
        var processorId = new MetricAggregationProcessorId($"sql-revision-{Guid.NewGuid():N}");
        var firstRevision = new MetricAggregationCheckpoint(processorId, first.StreamId, first.Position);
        var secondRevision = new MetricAggregationCheckpoint(processorId, first.StreamId, second.Position);
        await store.CommitAsync(new MetricAggregationCommit(processorId, null, firstRevision, []), CancellationToken.None);
        await store.CommitAsync(new MetricAggregationCommit(processorId, firstRevision, secondRevision, [second]), CancellationToken.None);
        var firstChange = await store.ReadNextAsync(processorId, first.StreamId, null, CancellationToken.None);
        var secondChange = await store.ReadNextAsync(processorId, first.StreamId, firstRevision, CancellationToken.None);
        var exactFirst = await store.ReadExactAsync(firstRevision, CancellationToken.None);
        var exactSecond = await store.ReadExactAsync(secondRevision, CancellationToken.None);
        Assert.NotNull(firstChange);
        Assert.Equal(firstRevision, firstChange.Revision);
        Assert.Empty(firstChange.ShiftOccurrenceIds);
        Assert.Empty(firstChange.ProductionDayIds);
        Assert.NotNull(exactFirst);
        Assert.Equal(firstChange.Revision, exactFirst.Revision);
        Assert.Equal(firstChange.ShiftOccurrenceIds, exactFirst.ShiftOccurrenceIds);
        Assert.Equal(firstChange.ProductionDayIds, exactFirst.ProductionDayIds);
        Assert.NotNull(secondChange);
        Assert.Equal(secondRevision, secondChange.Revision);
        Assert.Single(secondChange.ShiftOccurrenceIds);
        Assert.Equal(second.ShiftOccurrenceId, secondChange.ShiftOccurrenceIds[0]);
        Assert.Single(secondChange.ProductionDayIds);
        Assert.Equal(second.ProductionDayId, secondChange.ProductionDayIds[0]);
        Assert.NotNull(exactSecond);
        Assert.Equal(secondChange.Revision, exactSecond.Revision);
        Assert.Equal(secondChange.ShiftOccurrenceIds, exactSecond.ShiftOccurrenceIds);
        Assert.Equal(secondChange.ProductionDayIds, exactSecond.ProductionDayIds);
        Assert.Null(await store.ReadNextAsync(processorId, first.StreamId, secondRevision, CancellationToken.None));
    }

    [Fact]
    public async Task RevisionedSnapshotUsesExactLedgerRevisionAndHistoricalContributions()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var store = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var first = await inputStore.AppendAsync(CreateAppend(machineId, "sql-historical-1", 10m, 42), CancellationToken.None);
        var second = await inputStore.AppendAsync(CreateAppend(machineId, "sql-historical-2", 20m, 43), CancellationToken.None);
        var processorId = new MetricAggregationProcessorId($"sql-historical-{Guid.NewGuid():N}");
        var firstRevision = new MetricAggregationCheckpoint(processorId, first.StreamId, first.Position);
        var secondRevision = new MetricAggregationCheckpoint(processorId, first.StreamId, second.Position);
        await store.CommitAsync(new MetricAggregationCommit(processorId, null, firstRevision, [first]), CancellationToken.None);
        await store.CommitAsync(new MetricAggregationCommit(processorId, firstRevision, secondRevision, [second]), CancellationToken.None);
        var request = CreateSnapshotRequest(machineId, first, processorId, "historical-proof");
        var firstSnapshot = await store.ReadAtRevisionAsync(request, firstRevision, CancellationToken.None);
        var secondSnapshot = await store.ReadAtRevisionAsync(request, secondRevision, CancellationToken.None);
        Assert.Equal(firstRevision, firstSnapshot.Revision);
        var firstComponent = Assert.Single(firstSnapshot.Components);
        Assert.Equal(10m, firstComponent.Aggregate.Value);
        Assert.Equal(1L, firstComponent.Aggregate.InputCount);
        Assert.Equal(secondRevision, secondSnapshot.Revision);
        var secondComponent = Assert.Single(secondSnapshot.Components);
        Assert.Equal(30m, secondComponent.Aggregate.Value);
        Assert.Equal(2L, secondComponent.Aggregate.InputCount);
    }

    [Fact]
    public async Task RevisionedSnapshotRejectsPositionWithoutCommittedLedgerRevision()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var store = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var first = await inputStore.AppendAsync(CreateAppend(machineId, "sql-ledger-proof-1", 10m, 44), CancellationToken.None);
        var second = await inputStore.AppendAsync(CreateAppend(machineId, "sql-ledger-proof-2", 20m, 45), CancellationToken.None);
        var third = await inputStore.AppendAsync(CreateAppend(machineId, "sql-ledger-proof-3", 30m, 46), CancellationToken.None);
        var processorId = new MetricAggregationProcessorId($"sql-ledger-proof-{Guid.NewGuid():N}");
        var committedRevision = new MetricAggregationCheckpoint(processorId, first.StreamId, third.Position);
        await store.CommitAsync(new MetricAggregationCommit(processorId, null, committedRevision, [first, second, third]), CancellationToken.None);
        var request = CreateSnapshotRequest(machineId, first, processorId, "ledger-proof");
        var neverCommittedRevision = new MetricAggregationCheckpoint(processorId, first.StreamId, second.Position);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.ReadAtRevisionAsync(request, neverCommittedRevision, CancellationToken.None));
        Assert.Equal("Requested historical aggregation revision is not available.", exception.Message);
    }

    private static OperationalMetricComponentSnapshotRequest CreateSnapshotRequest(MachineId machineId, PositionedMetricInputFact input, MetricAggregationProcessorId processorId, string definitionId) =>
        new(new OperationalMetricEvaluationKey(machineId, new OperationalMetricPeriodId.Shift(input.ShiftOccurrenceId), new OperationalMetricDefinitionId(definitionId, "1"), OperationalMetricEvaluationContextKey.Unpartitioned), processorId,
        [new OperationalMetricOperandDefinition { OperandName = "running", Source = new OperationalMetricOperandSource.Component(input.Fact.Key), RequiredDimension = MetricDimension.Duration, RequiredUnit = "seconds" }]);

    private static DurableMetricInputAppend CreateAppend(MachineId machineId, string factId, decimal value, int minute)
    {
        var siteId = new SiteId("SITE-1");
        var shiftId = new ShiftId("SHIFT-A");
        var scheduleId = new ShiftScheduleAssignmentId("SCHEDULE-A");
        var occurrenceStart = new DateTimeOffset(2026, 8, 27, 6, 0, 0, TimeSpan.Zero);
        var factStart = occurrenceStart.AddMinutes(minute);
        var fact = new DurableMetricInputFact { Id = new MetricInputFactId(factId), Key = "running-duration", Value = value, Unit = "seconds", StartsAtUtc = factStart, EndsAtUtc = factStart.AddMinutes(1), CompanyId = new CompanyId("COMP-1"), SiteId = siteId, ProductionLineId = new ProductionLineId("LINE-1"), MachineId = machineId, ShiftId = shiftId, ShiftScheduleAssignmentId = scheduleId };
        return new DurableMetricInputAppend(MetricInputStreamId.ForMachine(machineId), fact, new ShiftOccurrenceId(siteId, scheduleId, shiftId, occurrenceStart, occurrenceStart.AddHours(8)), new ProductionDayId(siteId, new DateOnly(2026, 8, 27)));
    }
}
