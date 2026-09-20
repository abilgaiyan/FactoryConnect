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
        Assert.Equal(firstChange, exactFirst);

        Assert.NotNull(secondChange);
        Assert.Equal(secondRevision, secondChange.Revision);
        Assert.Single(secondChange.ShiftOccurrenceIds);
        Assert.Equal(second.ShiftOccurrenceId, secondChange.ShiftOccurrenceIds[0]);
        Assert.Single(secondChange.ProductionDayIds);
        Assert.Equal(second.ProductionDayId, secondChange.ProductionDayIds[0]);
        Assert.Equal(secondChange, exactSecond);

        Assert.Null(await store.ReadNextAsync(
            processorId,
            first.StreamId,
            secondRevision,
            CancellationToken.None));
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
