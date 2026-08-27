using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMetricInputStoreIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private static readonly MachineId MachineOne = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MachineId MachineTwo = new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMetricInputStoreIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AppendAllocatesOrderedPositionsAndIdenticalReplayPreservesPosition()
    {
        var store = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var first = CreateAppend(MachineOne, "fact-1", 10m, minute: 0);
        var second = CreateAppend(MachineOne, "fact-2", 20m, minute: 1);

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
        var store = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var original = CreateAppend(MachineOne, "fact-conflict", 10m, minute: 10);
        var conflicting = CreateAppend(MachineOne, "fact-conflict", 11m, minute: 10);

        await store.AppendAsync(original, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AppendAsync(conflicting, CancellationToken.None));

        var next = await store.AppendAsync(
            CreateAppend(MachineOne, "fact-after-conflict", 12m, minute: 11),
            CancellationToken.None);

        Assert.Equal(2UL, next.Position.Value);
    }

    [Fact]
    public async Task OrderedReaderHonorsAfterPositionAndMaximumCount()
    {
        var store = new SqlServerMetricInputStore(_fixture.ConnectionString);
        var streamId = MetricInputStreamId.ForMachine(MachineOne);

        for (var index = 0; index < 4; index++)
        {
            await store.AppendAsync(
                CreateAppend(
                    MachineOne,
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
        var store = new SqlServerMetricInputStore(_fixture.ConnectionString);

        var machineOne = await store.AppendAsync(
            CreateAppend(MachineOne, "fact-machine-1", 1m, minute: 30),
            CancellationToken.None);
        var machineTwo = await store.AppendAsync(
            CreateAppend(MachineTwo, "fact-machine-2", 1m, minute: 30),
            CancellationToken.None);

        Assert.Equal(1UL, machineOne.Position.Value);
        Assert.Equal(1UL, machineTwo.Position.Value);
        Assert.NotEqual(machineOne.StreamId, machineTwo.StreamId);
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
            new ProductionDayId(
                siteId,
                new DateOnly(2026, 8, 27)));
    }
}
