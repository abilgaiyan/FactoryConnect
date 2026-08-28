using FactoryConnect.Abstractions;
using FactoryConnect.Core;

namespace FactoryConnect.Core.Tests;

public sealed class InMemoryOperationalMetricComponentSnapshotReaderTests
{
    private static readonly MachineId Machine = new(new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    [Fact]
    public async Task SnapshotBindsAggregateValuesAndCheckpointFromSameCommittedRevision()
    {
        var store = new InMemoryMetricAggregationStore();
        var reader = store;
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var stream = MetricInputStreamId.ForMachine(Machine);
        var occurrence = CreateOccurrence();
        var day = CreateProductionDay();
        var key = new OperationalMetricEvaluationKey(
            Machine,
            new OperationalMetricPeriodId.ProductionDay(day),
            new OperationalMetricDefinitionId("snapshot-proof", "1.0"),
            OperationalMetricEvaluationContextKey.Unpartitioned);
        var request = new OperationalMetricComponentSnapshotRequest(
            key,
            processor,
            [new OperationalMetricOperandDefinition
            {
                OperandName = "RunningDuration",
                Source = new OperationalMetricOperandSource.Component(MetricInputFactKeys.RunningDuration),
                RequiredDimension = MetricDimension.Duration,
                RequiredUnit = "seconds",
            }]);

        var firstInput = CreateInput(stream, 1, "FACT-1", 60m, occurrence, day, 7);
        var firstRevision = new MetricAggregationCheckpoint(
            processor,
            stream,
            new MetricInputPosition(1));
        await store.CommitAsync(
            new MetricAggregationCommit(processor, null, firstRevision, [firstInput]),
            CancellationToken.None);

        var firstSnapshot = await reader.ReadAsync(request, CancellationToken.None);

        var secondInput = CreateInput(stream, 2, "FACT-2", 30m, occurrence, day, 8);
        var secondRevision = new MetricAggregationCheckpoint(
            processor,
            stream,
            new MetricInputPosition(2));
        await store.CommitAsync(
            new MetricAggregationCommit(processor, firstRevision, secondRevision, [secondInput]),
            CancellationToken.None);

        var secondSnapshot = await reader.ReadAsync(request, CancellationToken.None);

        Assert.Equal(firstRevision, firstSnapshot.Revision);
        Assert.Single(firstSnapshot.Components);
        Assert.Equal(60m, firstSnapshot.Components[0].Aggregate.Value);
        Assert.Equal(firstRevision.ProcessorId, firstSnapshot.Components[0].SourceIdentity.ProcessorId);

        Assert.Equal(secondRevision, secondSnapshot.Revision);
        Assert.Single(secondSnapshot.Components);
        Assert.Equal(90m, secondSnapshot.Components[0].Aggregate.Value);
        Assert.Equal(secondRevision.ProcessorId, secondSnapshot.Components[0].SourceIdentity.ProcessorId);

        Assert.Equal(60m, firstSnapshot.Components[0].Aggregate.Value);
        Assert.Equal(firstRevision, firstSnapshot.Revision);
    }

    [Fact]
    public async Task SnapshotRejectsProcessorCheckpointOwnedByAnotherMachine()
    {
        var store = new InMemoryMetricAggregationStore();
        var reader = store;
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var otherMachine = MachineId.New();
        var otherStream = MetricInputStreamId.ForMachine(otherMachine);
        var revision = new MetricAggregationCheckpoint(
            processor,
            otherStream,
            new MetricInputPosition(1));
        await store.CommitAsync(
            new MetricAggregationCommit(processor, null, revision, []),
            CancellationToken.None);

        var day = CreateProductionDay();
        var key = new OperationalMetricEvaluationKey(
            Machine,
            new OperationalMetricPeriodId.ProductionDay(day),
            new OperationalMetricDefinitionId("snapshot-proof", "1.0"),
            OperationalMetricEvaluationContextKey.Unpartitioned);
        var request = new OperationalMetricComponentSnapshotRequest(
            key,
            processor,
            []);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await reader.ReadAsync(request, CancellationToken.None));
    }

    private static ShiftOccurrenceId CreateOccurrence() =>
        new(
            new SiteId("SITE-1"),
            new ShiftScheduleAssignmentId("SCHEDULE-A"),
            new ShiftId("SHIFT-A"),
            new DateTimeOffset(2026, 8, 27, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 27, 14, 0, 0, TimeSpan.Zero));

    private static ProductionDayId CreateProductionDay() =>
        new(new SiteId("SITE-1"), new DateOnly(2026, 8, 27));

    private static PositionedMetricInputFact CreateInput(
        MetricInputStreamId stream,
        ulong position,
        string factId,
        decimal value,
        ShiftOccurrenceId occurrence,
        ProductionDayId productionDay,
        int hour)
    {
        var startsAt = new DateTimeOffset(2026, 8, 27, hour, 0, 0, TimeSpan.Zero);
        var fact = new DurableMetricInputFact
        {
            Id = new MetricInputFactId(factId),
            Key = MetricInputFactKeys.RunningDuration,
            Value = value,
            Unit = "seconds",
            StartsAtUtc = startsAt,
            EndsAtUtc = startsAt.AddMinutes(1),
            CompanyId = new CompanyId("COMP-1"),
            SiteId = new SiteId("SITE-1"),
            ProductionLineId = new ProductionLineId("LINE-1"),
            MachineId = Machine,
            ShiftId = occurrence.ShiftId,
            ShiftScheduleAssignmentId = occurrence.ShiftScheduleAssignmentId,
        };

        return new PositionedMetricInputFact(
            stream,
            new MetricInputPosition(position),
            fact,
            occurrence,
            productionDay);
    }
}
