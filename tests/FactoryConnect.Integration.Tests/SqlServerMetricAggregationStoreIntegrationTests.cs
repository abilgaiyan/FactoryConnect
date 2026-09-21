using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
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
        var processorId = new MetricAggregationProcessorId($"sql-aggregate-{Guid.NewGuid():N}");
        var checkpoint = new MetricAggregationCheckpoint(
            processorId,
            first.StreamId,
            second.Position);

        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(
                processorId,
                expectedCheckpoint: null,
                checkpoint,
                [first, second]),
            CancellationToken.None);

        var shift = await aggregationStore.ReadShiftAggregateAsync(
            processorId,
            new ShiftMetricAggregateKey(
                machineId,
                first.ShiftOccurrenceId,
                first.Fact.Key),
            CancellationToken.None);
        var day = await aggregationStore.ReadProductionDayAggregateAsync(
            processorId,
            new ProductionDayMetricAggregateKey(
                machineId,
                first.ProductionDayId,
                first.Fact.Key),
            CancellationToken.None);
        var restored = await aggregationStore.ReadCheckpointAsync(
            processorId,
            first.StreamId,
            CancellationToken.None);

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
        var first = await inputStore.AppendAsync(
            CreateAppend(machineId, "sql-replay-1", 10m, minute: 10),
            CancellationToken.None);
        var second = await inputStore.AppendAsync(
            CreateAppend(machineId, "sql-replay-2", 20m, minute: 11),
            CancellationToken.None);
        var processorId = new MetricAggregationProcessorId($"sql-replay-{Guid.NewGuid():N}");
        var firstCheckpoint = new MetricAggregationCheckpoint(
            processorId,
            first.StreamId,
            first.Position);

        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(
                processorId,
                expectedCheckpoint: null,
                firstCheckpoint,
                [first]),
            CancellationToken.None);

        var secondCheckpoint = new MetricAggregationCheckpoint(
            processorId,
            first.StreamId,
            second.Position);
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(
                processorId,
                firstCheckpoint,
                secondCheckpoint,
                [first, second]),
            CancellationToken.None);

        var aggregate = await aggregationStore.ReadShiftAggregateAsync(
            processorId,
            new ShiftMetricAggregateKey(
                machineId,
                first.ShiftOccurrenceId,
                first.Fact.Key),
            CancellationToken.None);

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
        var first = await inputStore.AppendAsync(
            CreateAppend(machineId, "sql-progress-1", 10m, minute: 20),
            CancellationToken.None);
        var second = await inputStore.AppendAsync(
            CreateAppend(machineId, "sql-progress-2", 20m, minute: 21),
            CancellationToken.None);
        var third = await inputStore.AppendAsync(
            CreateAppend(machineId, "sql-progress-3", 30m, minute: 22),
            CancellationToken.None);
        var processorId = new MetricAggregationProcessorId($"sql-progress-{Guid.NewGuid():N}");
        var acknowledged = new MetricAggregationCheckpoint(
            processorId,
            first.StreamId,
            second.Position);

        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(
                processorId,
                expectedCheckpoint: null,
                acknowledged,
                []),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await aggregationStore.CommitAsync(
                new MetricAggregationCommit(
                    processorId,
                    expectedCheckpoint: null,
                    new MetricAggregationCheckpoint(
                        processorId,
                        first.StreamId,
                        third.Position),
                    [third]),
                CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await aggregationStore.CommitAsync(
                new MetricAggregationCommit(
                    processorId,
                    acknowledged,
                    new MetricAggregationCheckpoint(
                        processorId,
                        first.StreamId,
                        third.Position),
                    [first, third]),
                CancellationToken.None));

        var aggregate = await aggregationStore.ReadShiftAggregateAsync(
            processorId,
            new ShiftMetricAggregateKey(
                machineId,
                first.ShiftOccurrenceId,
                first.Fact.Key),
            CancellationToken.None);
        var restored = await aggregationStore.ReadCheckpointAsync(
            processorId,
            first.StreamId,
            CancellationToken.None);

        Assert.Null(aggregate);
        Assert.Equal(acknowledged, restored);
    }

    [Fact]
    public async Task CommitRejectsPayloadThatDoesNotMatchDurablePositionedFact()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var aggregationStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var persisted = await inputStore.AppendAsync(
            CreateAppend(machineId, "sql-fabricated", 10m, minute: 30),
            CancellationToken.None);
        var conflictingFact = persisted.Fact with { Value = 999m };
        var fabricated = new PositionedMetricInputFact(
            persisted.StreamId,
            persisted.Position,
            conflictingFact,
            persisted.ShiftOccurrenceId,
            persisted.ProductionDayId);
        var processorId = new MetricAggregationProcessorId($"sql-fabricated-{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await aggregationStore.CommitAsync(
                new MetricAggregationCommit(
                    processorId,
                    expectedCheckpoint: null,
                    new MetricAggregationCheckpoint(
                        processorId,
                        persisted.StreamId,
                        persisted.Position),
                    [fabricated]),
                CancellationToken.None));

        var restored = await aggregationStore.ReadCheckpointAsync(
            processorId,
            persisted.StreamId,
            CancellationToken.None);
        Assert.Null(restored);
    }

    [Fact]
    public async Task RevisionReaderEnumeratesExactCommittedRevisionsIncludingEmptyMembership()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var store = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var first = await inputStore.AppendAsync(
            CreateAppend(machineId, "sql-revision-1", 10m, minute: 40),
            CancellationToken.None);
        var second = await inputStore.AppendAsync(
            CreateAppend(machineId, "sql-revision-2", 20m, minute: 41),
            CancellationToken.None);
        var processorId = new MetricAggregationProcessorId($"sql-revision-{Guid.NewGuid():N}");
        var firstRevision = new MetricAggregationCheckpoint(
            processorId,
            first.StreamId,
            first.Position);
        var secondRevision = new MetricAggregationCheckpoint(
            processorId,
            first.StreamId,
            second.Position);

        await store.CommitAsync(
            new MetricAggregationCommit(
                processorId,
                expectedCheckpoint: null,
                firstRevision,
                []),
            CancellationToken.None);
        await store.CommitAsync(
            new MetricAggregationCommit(
                processorId,
                firstRevision,
                secondRevision,
                [second]),
            CancellationToken.None);

        var firstChange = await store.ReadNextAsync(
            processorId,
            first.StreamId,
            afterRevision: null,
            CancellationToken.None);
        var secondChange = await store.ReadNextAsync(
            processorId,
            first.StreamId,
            firstRevision,
            CancellationToken.None);
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

        Assert.Null(await store.ReadNextAsync(
            processorId,
            first.StreamId,
            secondRevision,
            CancellationToken.None));
    }

    [Fact]
    public async Task RevisionedSnapshotUsesExactLedgerRevisionAndHistoricalContributions()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var store = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var first = await inputStore.AppendAsync(
            CreateAppend(machineId, "sql-historical-1", 10m, minute: 42),
            CancellationToken.None);
        var second = await inputStore.AppendAsync(
            CreateAppend(machineId, "sql-historical-2", 20m, minute: 43),
            CancellationToken.None);
        var processorId = new MetricAggregationProcessorId($"sql-historical-{Guid.NewGuid():N}");
        var firstRevision = new MetricAggregationCheckpoint(
            processorId,
            first.StreamId,
            first.Position);
        var secondRevision = new MetricAggregationCheckpoint(
            processorId,
            first.StreamId,
            second.Position);

        await store.CommitAsync(
            new MetricAggregationCommit(
                processorId,
                expectedCheckpoint: null,
                firstRevision,
                [first]),
            CancellationToken.None);
        await store.CommitAsync(
            new MetricAggregationCommit(
                processorId,
                firstRevision,
                secondRevision,
                [second]),
            CancellationToken.None);

        var request = new OperationalMetricComponentSnapshotRequest(
            new OperationalMetricEvaluationKey(
                machineId,
                new OperationalMetricPeriodId.Shift(first.ShiftOccurrenceId),
                new OperationalMetricDefinitionId("historical-proof", "1"),
                OperationalMetricEvaluationContextKey.Unpartitioned),
            processorId,
            [
                new OperationalMetricOperandDefinition
                {
                    OperandName = "running",
                    Source = new OperationalMetricOperandSource.Component(first.Fact.Key),
                    RequiredDimension = MetricDimension.Duration,
                    RequiredUnit = "seconds",
                },
            ]);

        var firstSnapshot = await store.ReadAtRevisionAsync(
            request,
            firstRevision,
            CancellationToken.None);
        var secondSnapshot = await store.ReadAtRevisionAsync(
            request,
            secondRevision,
            CancellationToken.None);

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
        var first = await inputStore.AppendAsync(
            CreateAppend(machineId, "sql-ledger-proof-1", 10m, minute: 44),
            CancellationToken.None);
        var second = await inputStore.AppendAsync(
            CreateAppend(machineId, "sql-ledger-proof-2", 20m, minute: 45),
            CancellationToken.None);
        var third = await inputStore.AppendAsync(
            CreateAppend(machineId, "sql-ledger-proof-3", 30m, minute: 46),
            CancellationToken.None);
        var processorId = new MetricAggregationProcessorId($"sql-ledger-proof-{Guid.NewGuid():N}");
        var committedRevision = new MetricAggregationCheckpoint(
            processorId,
            first.StreamId,
            third.Position);

        await store.CommitAsync(
            new MetricAggregationCommit(
                processorId,
                expectedCheckpoint: null,
                committedRevision,
                [first, second, third]),
            CancellationToken.None);

        var request = new OperationalMetricComponentSnapshotRequest(
            new OperationalMetricEvaluationKey(
                machineId,
                new OperationalMetricPeriodId.Shift(first.ShiftOccurrenceId),
                new OperationalMetricDefinitionId("ledger-proof", "1"),
                OperationalMetricEvaluationContextKey.Unpartitioned),
            processorId,
            [
                new OperationalMetricOperandDefinition
                {
                    OperandName = "running",
                    Source = new OperationalMetricOperandSource.Component(first.Fact.Key),
                    RequiredDimension = MetricDimension.Duration,
                    RequiredUnit = "seconds",
                },
            ]);
        var neverCommittedRevision = new MetricAggregationCheckpoint(
            processorId,
            first.StreamId,
            second.Position);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.ReadAtRevisionAsync(
                request,
                neverCommittedRevision,
                CancellationToken.None));

        Assert.Equal(
            "Requested historical aggregation revision is not available.",
            exception.Message);
    }

    private static DurableMetricInputAppend CreateAppend(
        MachineId machineId,
        string factId,
        decimal value,
        int minute)
    {
        var siteId = new SiteId("SITE-1");
        var shiftId = new ShiftId("SHIFT-A");
        var scheduleId = new ShiftScheduleAssignmentId("SCHEDULE-A");
        var occurrenceStart =
            new DateTimeOffset(2026, 8, 27, 6, 0, 0, TimeSpan.Zero);
        var factStart = occurrenceStart.AddMinutes(minute);
        var fact = new DurableMetricInputFact
        {
            Id = new MetricInputFactId(factId),
            Key = "running-duration",
            Value = value,
            Unit = "seconds",
            StartsAtUtc = factStart,
            EndsAtUtc = factStart.AddMinutes(1),
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
            new ShiftOccurrenceId(
                siteId,
                scheduleId,
                shiftId,
                occurrenceStart,
                occurrenceStart.AddHours(8)),
            new ProductionDayId(siteId, new DateOnly(2026, 8, 27)));
    }
}
