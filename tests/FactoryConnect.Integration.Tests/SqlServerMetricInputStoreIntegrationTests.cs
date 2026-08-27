using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMetricInputStoreIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMetricInputStoreIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AppendAllocatesOrderedPositionsAndIdenticalReplayPreservesPosition()
    {
        var machineId = NewMachineId();
        var store = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var first = CreateAppend(machineId, "fact-1", 10m, minute: 0);
        var second = CreateAppend(machineId, "fact-2", 20m, minute: 1);

        var positionedFirst = await store.AppendAsync(first, CancellationToken.None);
        var replayedFirst = await store.AppendAsync(first, CancellationToken.None);
        var positionedSecond = await store.AppendAsync(second, CancellationToken.None);

        Assert.Equal(1UL, positionedFirst.Position.Value);
        Assert.Equal(positionedFirst, replayedFirst);
        Assert.Equal(2UL, positionedSecond.Position.Value);
    }

    [Fact]
    public async Task ConflictingReplayIsRejectedWithoutAllocatingAnotherPosition()
    {
        var machineId = NewMachineId();
        var store = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var original = CreateAppend(machineId, "fact-conflict", 10m, minute: 10);
        var conflicting = CreateAppend(machineId, "fact-conflict", 11m, minute: 10);

        await store.AppendAsync(original, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AppendAsync(conflicting, CancellationToken.None));

        var next = await store.AppendAsync(
            CreateAppend(machineId, "fact-after-conflict", 12m, minute: 11),
            CancellationToken.None);

        Assert.Equal(2UL, next.Position.Value);
    }

    [Fact]
    public async Task OrderedReaderHonorsAfterPositionAndMaximumCount()
    {
        var machineId = NewMachineId();
        var store = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var streamId = MetricInputStreamId.ForMachine(machineId);

        for (var index = 0; index < 4; index++)
        {
            await store.AppendAsync(
                CreateAppend(
                    machineId,
                    $"fact-read-{index}",
                    index + 1m,
                    minute: 20 + index),
                CancellationToken.None);
        }

        var firstBatch = await store.ReadAsync(
            new MetricInputReadRequest(
                streamId,
                afterPosition: null,
                maxCount: 2),
            CancellationToken.None);

        Assert.Equal(2, firstBatch.Facts.Count);
        Assert.Equal(1UL, firstBatch.Facts[0].Position.Value);
        Assert.Equal(2UL, firstBatch.Facts[1].Position.Value);
        Assert.Equal(2UL, firstBatch.ThroughPosition!.Value);

        var secondBatch = await store.ReadAsync(
            new MetricInputReadRequest(
                streamId,
                firstBatch.ThroughPosition,
                maxCount: 2),
            CancellationToken.None);

        Assert.Equal(2, secondBatch.Facts.Count);
        Assert.Equal(3UL, secondBatch.Facts[0].Position.Value);
        Assert.Equal(4UL, secondBatch.Facts[1].Position.Value);
        Assert.Equal(4UL, secondBatch.ThroughPosition!.Value);
    }

    [Fact]
    public async Task MachineScopedStreamsAllocateProgressIndependently()
    {
        var machineOneId = NewMachineId();
        var machineTwoId = NewMachineId();
        var store = new SqlServerMetricInputStore(_fixture.ConnectionString);

        var machineOne = await store.AppendAsync(
            CreateAppend(machineOneId, "fact-machine-1", 1m, minute: 30),
            CancellationToken.None);
        var machineTwo = await store.AppendAsync(
            CreateAppend(machineTwoId, "fact-machine-2", 1m, minute: 30),
            CancellationToken.None);

        Assert.Equal(1UL, machineOne.Position.Value);
        Assert.Equal(1UL, machineTwo.Position.Value);
        Assert.NotEqual(machineOne.StreamId, machineTwo.StreamId);
    }

    [Fact]
    public async Task ConcurrentDifferentFactsReceiveDistinctConsecutivePositions()
    {
        var machineId = NewMachineId();
        var store = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var first = CreateAppend(machineId, "fact-concurrent-1", 1m, minute: 40);
        var second = CreateAppend(machineId, "fact-concurrent-2", 2m, minute: 41);

        var results = await Task.WhenAll(
            store.AppendAsync(first, CancellationToken.None).AsTask(),
            store.AppendAsync(second, CancellationToken.None).AsTask());

        var positions = results
            .Select(static result => result.Position.Value)
            .OrderBy(static position => position)
            .ToArray();

        Assert.Collection(
            positions,
            static position => Assert.Equal(1UL, position),
            static position => Assert.Equal(2UL, position));

        var batch = await store.ReadAsync(
            new MetricInputReadRequest(
                MetricInputStreamId.ForMachine(machineId),
                afterPosition: null,
                maxCount: 10),
            CancellationToken.None);

        Assert.Equal(2, batch.Facts.Count);
        Assert.Equal(2, batch.Facts.Select(static fact => fact.Fact.Id).Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentIdenticalReplayProducesOneFactAndOneStablePosition()
    {
        var machineId = NewMachineId();
        var store = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var append = CreateAppend(machineId, "fact-concurrent-replay", 3m, minute: 50);

        var results = await Task.WhenAll(
            store.AppendAsync(append, CancellationToken.None).AsTask(),
            store.AppendAsync(append, CancellationToken.None).AsTask());

        Assert.Equal(results[0], results[1]);
        Assert.Equal(1UL, results[0].Position.Value);

        var batch = await store.ReadAsync(
            new MetricInputReadRequest(
                MetricInputStreamId.ForMachine(machineId),
                afterPosition: null,
                maxCount: 10),
            CancellationToken.None);

        var persisted = Assert.Single(batch.Facts);
        Assert.Equal(results[0], persisted);
    }

    [Fact]
    public async Task SeparateMachineStreamsAllocateIndependentlyUnderConcurrency()
    {
        var firstMachineId = NewMachineId();
        var secondMachineId = NewMachineId();
        var store = new SqlServerMetricInputStore(_fixture.ConnectionString);

        var results = await Task.WhenAll(
            store.AppendAsync(
                CreateAppend(firstMachineId, "fact-concurrent-machine-1", 1m, minute: 55),
                CancellationToken.None).AsTask(),
            store.AppendAsync(
                CreateAppend(secondMachineId, "fact-concurrent-machine-2", 1m, minute: 55),
                CancellationToken.None).AsTask());

        Assert.All(results, static result => Assert.Equal(1UL, result.Position.Value));
        Assert.NotEqual(results[0].StreamId, results[1].StreamId);
    }

    private static MachineId NewMachineId() => new(Guid.NewGuid());

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
            new ProductionDayId(
                siteId,
                new DateOnly(2026, 8, 27)));
    }
}
