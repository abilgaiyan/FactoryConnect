using System.Data;
using System.Globalization;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMetricAggregationParityIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMetricAggregationParityIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EmptyWindowAdvancesOnlyCheckpoint()
    {
        var machineId = NewMachineId();
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var store = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var positioned = await inputStore.AppendAsync(
            CreateAppend(machineId, "empty-window", 10m),
            CancellationToken.None);
        var processorId = NewProcessorId("empty");
        var checkpoint = new MetricAggregationCheckpoint(
            processorId,
            positioned.StreamId,
            positioned.Position);

        await store.CommitAsync(
            new MetricAggregationCommit(
                processorId,
                expectedCheckpoint: null,
                checkpoint,
                []),
            CancellationToken.None);

        var aggregate = await store.ReadShiftAggregateAsync(
            processorId,
            ShiftKey(positioned),
            CancellationToken.None);
        var restored = await store.ReadCheckpointAsync(
            processorId,
            positioned.StreamId,
            CancellationToken.None);

        Assert.Null(aggregate);
        Assert.Equal(checkpoint, restored);
    }

    [Fact]
    public async Task IncompatibleUnitRollsBackBothProjectionsContributionAndCheckpoint()
    {
        var machineId = NewMachineId();
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var store = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var first = await inputStore.AppendAsync(
            CreateAppend(machineId, "unit-1", 10m, unit: "seconds"),
            CancellationToken.None);
        var second = await inputStore.AppendAsync(
            CreateAppend(machineId, "unit-2", 20m, unit: "minutes", minute: 1),
            CancellationToken.None);
        var processorId = NewProcessorId("unit");
        var firstCheckpoint = new MetricAggregationCheckpoint(
            processorId,
            first.StreamId,
            first.Position);

        await store.CommitAsync(
            new MetricAggregationCommit(
                processorId,
                expectedCheckpoint: null,
                firstCheckpoint,
                [first]),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitAsync(
                new MetricAggregationCommit(
                    processorId,
                    firstCheckpoint,
                    new MetricAggregationCheckpoint(
                        processorId,
                        first.StreamId,
                        second.Position),
                    [second]),
                CancellationToken.None));

        var shift = await store.ReadShiftAggregateAsync(
            processorId,
            ShiftKey(first),
            CancellationToken.None);
        var day = await store.ReadProductionDayAggregateAsync(
            processorId,
            DayKey(first),
            CancellationToken.None);
        var restored = await store.ReadCheckpointAsync(
            processorId,
            first.StreamId,
            CancellationToken.None);
        var contributionCount = await CountContributionAsync(processorId, second.Fact.Id);

        Assert.NotNull(shift);
        Assert.Equal(10m, shift.Value);
        Assert.Equal(shift, day);
        Assert.Equal(firstCheckpoint, restored);
        Assert.Equal(0L, contributionCount);
    }

    [Fact]
    public async Task PersistedDecimalOverflowRollsBackEverything()
    {
        var machineId = NewMachineId();
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var store = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var first = await inputStore.AppendAsync(
            CreateAppend(machineId, "overflow-1", decimal.MaxValue),
            CancellationToken.None);
        var second = await inputStore.AppendAsync(
            CreateAppend(machineId, "overflow-2", 1m, minute: 1),
            CancellationToken.None);
        var processorId = NewProcessorId("overflow");
        var firstCheckpoint = new MetricAggregationCheckpoint(
            processorId,
            first.StreamId,
            first.Position);

        await store.CommitAsync(
            new MetricAggregationCommit(
                processorId,
                expectedCheckpoint: null,
                firstCheckpoint,
                [first]),
            CancellationToken.None);

        await Assert.ThrowsAsync<OverflowException>(async () =>
            await store.CommitAsync(
                new MetricAggregationCommit(
                    processorId,
                    firstCheckpoint,
                    new MetricAggregationCheckpoint(
                        processorId,
                        first.StreamId,
                        second.Position),
                    [second]),
                CancellationToken.None));

        var shift = await store.ReadShiftAggregateAsync(
            processorId,
            ShiftKey(first),
            CancellationToken.None);
        var day = await store.ReadProductionDayAggregateAsync(
            processorId,
            DayKey(first),
            CancellationToken.None);
        var restored = await store.ReadCheckpointAsync(
            processorId,
            first.StreamId,
            CancellationToken.None);
        var contributionCount = await CountContributionAsync(processorId, second.Fact.Id);

        Assert.NotNull(shift);
        Assert.Equal(decimal.MaxValue, shift.Value);
        Assert.Equal(shift, day);
        Assert.Equal(firstCheckpoint, restored);
        Assert.Equal(0L, contributionCount);
    }

    [Fact]
    public async Task ConcurrentCommitsWithSameExpectedCheckpointHaveExactlyOneWinner()
    {
        var machineId = NewMachineId();
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var store = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var first = await inputStore.AppendAsync(
            CreateAppend(machineId, "cas-1", 10m),
            CancellationToken.None);
        var second = await inputStore.AppendAsync(
            CreateAppend(machineId, "cas-2", 20m, minute: 1),
            CancellationToken.None);
        var third = await inputStore.AppendAsync(
            CreateAppend(machineId, "cas-3", 30m, minute: 2),
            CancellationToken.None);
        var processorId = NewProcessorId("cas");
        var firstCheckpoint = new MetricAggregationCheckpoint(
            processorId,
            first.StreamId,
            first.Position);

        await store.CommitAsync(
            new MetricAggregationCommit(
                processorId,
                expectedCheckpoint: null,
                firstCheckpoint,
                [first]),
            CancellationToken.None);

        var secondCommit = new MetricAggregationCommit(
            processorId,
            firstCheckpoint,
            new MetricAggregationCheckpoint(processorId, first.StreamId, second.Position),
            [second]);
        var thirdCommit = new MetricAggregationCommit(
            processorId,
            firstCheckpoint,
            new MetricAggregationCheckpoint(processorId, first.StreamId, third.Position),
            [third]);

        var outcomes = await Task.WhenAll(
            TryCommitAsync(store, secondCommit),
            TryCommitAsync(store, thirdCommit));

        Assert.Equal(1, outcomes.Count(static succeeded => succeeded));

        var aggregate = await store.ReadShiftAggregateAsync(
            processorId,
            ShiftKey(first),
            CancellationToken.None);
        var restored = await store.ReadCheckpointAsync(
            processorId,
            first.StreamId,
            CancellationToken.None);

        Assert.NotNull(aggregate);
        Assert.NotNull(restored);
        Assert.True(
            (restored.Position == second.Position && aggregate.Value == 30m) ||
            (restored.Position == third.Position && aggregate.Value == 40m));
    }

    [Fact]
    public async Task ProcessorsConsumeSameStreamIndependentlyAndCannotSwitchStreams()
    {
        var machineId = NewMachineId();
        var otherMachineId = NewMachineId();
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var store = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var first = await inputStore.AppendAsync(
            CreateAppend(machineId, "processor-1", 10m),
            CancellationToken.None);
        var second = await inputStore.AppendAsync(
            CreateAppend(machineId, "processor-2", 20m, minute: 1),
            CancellationToken.None);
        var other = await inputStore.AppendAsync(
            CreateAppend(otherMachineId, "processor-other", 99m),
            CancellationToken.None);
        var processorOne = NewProcessorId("processor-one");
        var processorTwo = NewProcessorId("processor-two");

        foreach (var processorId in new[] { processorOne, processorTwo })
        {
            await store.CommitAsync(
                new MetricAggregationCommit(
                    processorId,
                    expectedCheckpoint: null,
                    new MetricAggregationCheckpoint(processorId, first.StreamId, second.Position),
                    [first, second]),
                CancellationToken.None);
        }

        var one = await store.ReadShiftAggregateAsync(
            processorOne,
            ShiftKey(first),
            CancellationToken.None);
        var two = await store.ReadShiftAggregateAsync(
            processorTwo,
            ShiftKey(first),
            CancellationToken.None);

        Assert.Equal(one, two);
        Assert.Equal(30m, one!.Value);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitAsync(
                new MetricAggregationCommit(
                    processorOne,
                    expectedCheckpoint: null,
                    new MetricAggregationCheckpoint(processorOne, other.StreamId, other.Position),
                    [other]),
                CancellationToken.None));
    }

    [Fact]
    public async Task ReusingDurablePositionForDifferentFactIsRejected()
    {
        var machineId = NewMachineId();
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var store = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var first = await inputStore.AppendAsync(
            CreateAppend(machineId, "position-1", 10m),
            CancellationToken.None);
        var second = await inputStore.AppendAsync(
            CreateAppend(machineId, "position-2", 20m, minute: 1),
            CancellationToken.None);
        var fabricated = new PositionedMetricInputFact(
            second.StreamId,
            first.Position,
            second.Fact,
            second.ShiftOccurrenceId,
            second.ProductionDayId);
        var processorId = NewProcessorId("position");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitAsync(
                new MetricAggregationCommit(
                    processorId,
                    expectedCheckpoint: null,
                    new MetricAggregationCheckpoint(processorId, first.StreamId, second.Position),
                    [first, fabricated]),
                CancellationToken.None));

        var restored = await store.ReadCheckpointAsync(
            processorId,
            first.StreamId,
            CancellationToken.None);
        Assert.Null(restored);
    }

    [Fact]
    public async Task OneCommitUpdatesMultipleShiftsProductionDaysAndMetricKeys()
    {
        var machineId = NewMachineId();
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var store = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var dayOne = new DateOnly(2026, 8, 27);
        var dayTwo = dayOne.AddDays(1);
        var shiftOneStart = new DateTimeOffset(2026, 8, 27, 6, 0, 0, TimeSpan.Zero);
        var shiftTwoStart = new DateTimeOffset(2026, 8, 27, 14, 0, 0, TimeSpan.Zero);
        var shiftThreeStart = new DateTimeOffset(2026, 8, 28, 14, 0, 0, TimeSpan.Zero);
        var first = await inputStore.AppendAsync(
            CreateAppend(machineId, "multi-1", 10m, "seconds", 0, "running", "SHIFT-A", "SCHEDULE-A", shiftOneStart, dayOne),
            CancellationToken.None);
        var second = await inputStore.AppendAsync(
            CreateAppend(machineId, "multi-2", 20m, "seconds", 1, "running", "SHIFT-B", "SCHEDULE-B", shiftTwoStart, dayOne),
            CancellationToken.None);
        var third = await inputStore.AppendAsync(
            CreateAppend(machineId, "multi-3", 3m, "parts", 2, "good-parts", "SHIFT-B", "SCHEDULE-B", shiftThreeStart, dayTwo),
            CancellationToken.None);
        var processorId = NewProcessorId("multi");

        await store.CommitAsync(
            new MetricAggregationCommit(
                processorId,
                expectedCheckpoint: null,
                new MetricAggregationCheckpoint(processorId, first.StreamId, third.Position),
                [first, second, third]),
            CancellationToken.None);

        var firstShift = await store.ReadShiftAggregateAsync(
            processorId,
            ShiftKey(first),
            CancellationToken.None);
        var secondShift = await store.ReadShiftAggregateAsync(
            processorId,
            ShiftKey(second),
            CancellationToken.None);
        var dayOneRunning = await store.ReadProductionDayAggregateAsync(
            processorId,
            DayKey(first),
            CancellationToken.None);
        var dayTwoGoodParts = await store.ReadProductionDayAggregateAsync(
            processorId,
            DayKey(third),
            CancellationToken.None);

        Assert.Equal(10m, firstShift!.Value);
        Assert.Equal(20m, secondShift!.Value);
        Assert.Equal(30m, dayOneRunning!.Value);
        Assert.Equal(3m, dayTwoGoodParts!.Value);
    }

    [Fact]
    public async Task RuntimeRestartRestoresSqlProgressWithoutInflatingAggregate()
    {
        var machineId = NewMachineId();
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var store = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var first = await inputStore.AppendAsync(CreateAppend(machineId, "runtime-1", 10m), CancellationToken.None);
        await inputStore.AppendAsync(CreateAppend(machineId, "runtime-2", 20m, minute: 1), CancellationToken.None);
        var third = await inputStore.AppendAsync(CreateAppend(machineId, "runtime-3", 30m, minute: 2), CancellationToken.None);
        var processorId = NewProcessorId("runtime");

        var firstRuntime = new MetricAggregationProcessingRuntime(
            processorId,
            inputStore,
            store,
            first.StreamId,
            batchSize: 2);
        Assert.Equal(2, await firstRuntime.RunCycleAsync(CancellationToken.None));

        var restartedRuntime = new MetricAggregationProcessingRuntime(
            processorId,
            inputStore,
            store,
            first.StreamId,
            batchSize: 2);
        Assert.Equal(1, await restartedRuntime.RunCycleAsync(CancellationToken.None));
        Assert.Equal(0, await restartedRuntime.RunCycleAsync(CancellationToken.None));

        var aggregate = await store.ReadShiftAggregateAsync(
            processorId,
            ShiftKey(first),
            CancellationToken.None);
        var checkpoint = await store.ReadCheckpointAsync(
            processorId,
            first.StreamId,
            CancellationToken.None);

        Assert.NotNull(aggregate);
        Assert.Equal(60m, aggregate.Value);
        Assert.Equal(3L, aggregate.InputCount);
        Assert.Equal(third.Position, checkpoint!.Position);
    }

    [Fact]
    public async Task SqlAndInMemoryStoresProduceEquivalentObservableResults()
    {
        var machineId = NewMachineId();
        var inputStore = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var sqlStore = new SqlServerMetricAggregationStore(_fixture.ConnectionString);
        var memoryStore = new InMemoryMetricAggregationStore();
        var first = await inputStore.AppendAsync(CreateAppend(machineId, "parity-1", 10m), CancellationToken.None);
        var second = await inputStore.AppendAsync(CreateAppend(machineId, "parity-2", 20m, minute: 1), CancellationToken.None);
        var third = await inputStore.AppendAsync(CreateAppend(machineId, "parity-3", 30m, minute: 2), CancellationToken.None);
        var processorId = NewProcessorId("parity");
        var firstCheckpoint = new MetricAggregationCheckpoint(processorId, first.StreamId, first.Position);
        var finalCheckpoint = new MetricAggregationCheckpoint(processorId, first.StreamId, third.Position);
        var commits = new[]
        {
            new MetricAggregationCommit(processorId, null, firstCheckpoint, [first]),
            new MetricAggregationCommit(processorId, firstCheckpoint, finalCheckpoint, [first, second, third]),
        };

        foreach (var commit in commits)
        {
            await sqlStore.CommitAsync(commit, CancellationToken.None);
            await memoryStore.CommitAsync(commit, CancellationToken.None);
        }

        var shiftKey = ShiftKey(first);
        var dayKey = DayKey(first);
        var sqlShift = await sqlStore.ReadShiftAggregateAsync(processorId, shiftKey, CancellationToken.None);
        var memoryShift = await memoryStore.ReadShiftAggregateAsync(processorId, shiftKey, CancellationToken.None);
        var sqlDay = await sqlStore.ReadProductionDayAggregateAsync(processorId, dayKey, CancellationToken.None);
        var memoryDay = await memoryStore.ReadProductionDayAggregateAsync(processorId, dayKey, CancellationToken.None);
        var sqlCheckpoint = await sqlStore.ReadCheckpointAsync(processorId, first.StreamId, CancellationToken.None);
        var memoryCheckpoint = await memoryStore.ReadCheckpointAsync(processorId, first.StreamId, CancellationToken.None);

        Assert.Equal(memoryShift, sqlShift);
        Assert.Equal(memoryDay, sqlDay);
        Assert.Equal(memoryCheckpoint, sqlCheckpoint);
    }

    private static async Task<bool> TryCommitAsync(
        SqlServerMetricAggregationStore store,
        MetricAggregationCommit commit)
    {
        try
        {
            await store.CommitAsync(commit, CancellationToken.None);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<long> CountContributionAsync(
        MetricAggregationProcessorId processorId,
        MetricInputFactId factId)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT_BIG(1) FROM dbo.MetricAggregationContribution c " +
            "JOIN dbo.MetricAggregationProcessor p ON p.MetricAggregationProcessorRowId = c.MetricAggregationProcessorRowId " +
            "JOIN dbo.MetricInputFact f ON f.MetricInputFactRowId = c.MetricInputFactRowId " +
            "WHERE p.ProcessorKey = @ProcessorKey AND f.FactId = @FactId;";
        command.Parameters.Add("@ProcessorKey", SqlDbType.NVarChar, 256).Value = processorId.Value;
        command.Parameters.Add("@FactId", SqlDbType.NVarChar, 256).Value = factId.Value;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);
    }

    private static ShiftMetricAggregateKey ShiftKey(PositionedMetricInputFact input) =>
        new(input.Fact.MachineId, input.ShiftOccurrenceId, input.Fact.Key);

    private static ProductionDayMetricAggregateKey DayKey(PositionedMetricInputFact input) =>
        new(input.Fact.MachineId, input.ProductionDayId, input.Fact.Key);

    private static MetricAggregationProcessorId NewProcessorId(string prefix) =>
        new($"sql-{prefix}-{Guid.NewGuid():N}");

    private static MachineId NewMachineId() => new(Guid.NewGuid());

    private static DurableMetricInputAppend CreateAppend(
        MachineId machineId,
        string factId,
        decimal value,
        string unit = "seconds",
        int minute = 0,
        string key = "running-duration",
        string shift = "SHIFT-A",
        string schedule = "SCHEDULE-A",
        DateTimeOffset? occurrenceStart = null,
        DateOnly? productionDay = null)
    {
        var siteId = new SiteId("SITE-1");
        var shiftId = new ShiftId(shift);
        var scheduleId = new ShiftScheduleAssignmentId(schedule);
        var resolvedOccurrenceStart = occurrenceStart ??
            new DateTimeOffset(2026, 8, 27, 6, 0, 0, TimeSpan.Zero);
        var factStart = resolvedOccurrenceStart.AddMinutes(minute);
        var fact = new DurableMetricInputFact
        {
            Id = new MetricInputFactId(factId),
            Key = key,
            Value = value,
            Unit = unit,
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
                resolvedOccurrenceStart,
                resolvedOccurrenceStart.AddHours(8)),
            new ProductionDayId(
                siteId,
                productionDay ?? new DateOnly(2026, 8, 27)));
    }
}
