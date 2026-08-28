using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricRevisionDrivenConformanceTests
{
    private static readonly DateTimeOffset StartsAt = new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndsAt = new(2026, 8, 29, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RuntimeProcessesHistoricalRevisionBeforeCurrentAndThenReplaysCurrentAfterRestart()
    {
        var machineId = MachineId.New();
        var siteId = new SiteId("site-a");
        var day = new ProductionDayId(siteId, new DateOnly(2026, 8, 29));
        var shiftId = new ShiftId("shift-a");
        var assignmentId = new ShiftScheduleAssignmentId("schedule-a");
        var occurrence = new ShiftOccurrenceId(
            siteId,
            assignmentId,
            shiftId,
            StartsAt,
            new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero));
        var streamId = MetricInputStreamId.ForMachine(machineId);
        var aggregationProcessorId = new MetricAggregationProcessorId("aggregate-m01");
        var aggregationStore = new InMemoryMetricAggregationStore();

        var revision6 = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            streamId,
            new MetricInputPosition(6));
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(
                aggregationProcessorId,
                null,
                revision6,
                [
                    Input(streamId, 1, machineId, siteId, shiftId, assignmentId, occurrence, day, MetricInputKeys.ActualProductionTime, 300m, MetricInputFactUnits.Seconds),
                    Input(streamId, 2, machineId, siteId, shiftId, assignmentId, occurrence, day, MetricInputKeys.PlannedOperatingTime, 600m, MetricInputFactUnits.Seconds),
                    Input(streamId, 3, machineId, siteId, shiftId, assignmentId, occurrence, day, MetricInputKeys.ProductionReferenceTime, 240m, MetricInputFactUnits.Seconds),
                    Input(streamId, 4, machineId, siteId, shiftId, assignmentId, occurrence, day, MetricInputKeys.ProducedQuantity, 100m, MetricInputFactUnits.Count),
                    Input(streamId, 5, machineId, siteId, shiftId, assignmentId, occurrence, day, MetricInputKeys.GoodQuantity, 90m, MetricInputFactUnits.Count),
                    Input(streamId, 6, machineId, siteId, shiftId, assignmentId, occurrence, day, MetricInputKeys.MachinePowerOnTime, 750m, MetricInputFactUnits.Seconds),
                ]),
            CancellationToken.None);

        var revision7 = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            streamId,
            new MetricInputPosition(7));
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(
                aggregationProcessorId,
                revision6,
                revision7,
                [Input(
                    streamId,
                    7,
                    machineId,
                    siteId,
                    shiftId,
                    assignmentId,
                    occurrence,
                    day,
                    MetricInputKeys.GoodQuantity,
                    5m,
                    MetricInputFactUnits.Count)]),
            CancellationToken.None);

        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var source = new CoherentOperationalMetricEvaluationBatchSource(
            catalog,
            aggregationStore,
            aggregationStore,
            aggregationProcessorId,
            streamId,
            OperationalMetricEvaluationContextKey.Unpartitioned);
        var projectionProcessorId = new OperationalMetricProjectionProcessorId("projection-m01");
        var projectionStore = new InMemoryOperationalMetricProjectionStore();
        var factory = new OperationalMetricProjectionFactory(catalog, projectionProcessorId);
        var runtime = new OperationalMetricProjectionProcessingRuntime(
            projectionProcessorId,
            aggregationProcessorId,
            streamId,
            source,
            factory,
            projectionStore);
        var dayOeeKey = new OperationalMetricEvaluationKey(
            machineId,
            new OperationalMetricPeriodId.ProductionDay(day),
            BuiltInOperationalMetricDefinitions.OeeId,
            OperationalMetricEvaluationContextKey.Unpartitioned);

        Assert.Equal(10, await runtime.RunCycleAsync());
        var revision6Oee = await projectionStore.ReadProjectionAsync(
            projectionProcessorId,
            dayOeeKey,
            CancellationToken.None);
        Assert.NotNull(revision6Oee);
        Assert.Equal(0.36m, revision6Oee.Value);
        Assert.Equal(revision6, revision6Oee.SourceRevision);

        Assert.Equal(10, await runtime.RunCycleAsync());
        var revision7Oee = await projectionStore.ReadProjectionAsync(
            projectionProcessorId,
            dayOeeKey,
            CancellationToken.None);
        Assert.NotNull(revision7Oee);
        Assert.Equal(0.38m, revision7Oee.Value);
        Assert.Equal(revision7, revision7Oee.SourceRevision);

        var restarted = new OperationalMetricProjectionProcessingRuntime(
            projectionProcessorId,
            aggregationProcessorId,
            streamId,
            source,
            factory,
            projectionStore);
        Assert.Equal(0, await restarted.RunCycleAsync());

        var checkpoint = await projectionStore.ReadCheckpointAsync(
            projectionProcessorId,
            streamId,
            CancellationToken.None);
        Assert.NotNull(checkpoint);
        Assert.Equal(revision7, checkpoint.SourceRevision);
        Assert.Equal(10, checkpoint.BatchManifest.ProjectionKeys.Count);
    }

    private static PositionedMetricInputFact Input(
        MetricInputStreamId streamId,
        ulong position,
        MachineId machineId,
        SiteId siteId,
        ShiftId shiftId,
        ShiftScheduleAssignmentId assignmentId,
        ShiftOccurrenceId occurrence,
        ProductionDayId day,
        string key,
        decimal value,
        string unit) => new(
            streamId,
            new MetricInputPosition(position),
            new DurableMetricInputFact
            {
                Id = new MetricInputFactId($"fact-{position}"),
                Key = key,
                Value = value,
                Unit = unit,
                StartsAtUtc = StartsAt,
                EndsAtUtc = EndsAt,
                CompanyId = new CompanyId("company-a"),
                SiteId = siteId,
                MachineId = machineId,
                ShiftId = shiftId,
                ShiftScheduleAssignmentId = assignmentId,
            },
            occurrence,
            day);
}
