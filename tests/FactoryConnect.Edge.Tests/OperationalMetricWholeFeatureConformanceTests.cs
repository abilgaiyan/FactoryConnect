using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Edge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class OperationalMetricWholeFeatureConformanceTests
{
    private static readonly DateTimeOffset DayStartsAt =
        new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ComposedMultiMachineRuntimeProducesReportsAndRestartResumesWithoutDuplicateOutput()
    {
        var machineA = MachineId.New();
        var machineB = MachineId.New();
        var siteId = new SiteId("site-a");
        var day = new ProductionDayId(siteId, new DateOnly(2026, 8, 29));
        var aggregationStore = new InMemoryMetricAggregationStore();
        var projectionStore = new InMemoryOperationalMetricProjectionStore();

        await CommitMetricInputsAsync(
            aggregationStore,
            machineA,
            siteId,
            day,
            actualProductionTime: 50m,
            plannedOperatingTime: 100m,
            productionReferenceTime: 40m,
            producedQuantity: 10m,
            goodQuantity: 9m,
            machinePowerOnTime: 120m);
        await CommitMetricInputsAsync(
            aggregationStore,
            machineB,
            siteId,
            day,
            actualProductionTime: 80m,
            plannedOperatingTime: 100m,
            productionReferenceTime: 64m,
            producedQuantity: 10m,
            goodQuantity: 10m,
            machinePowerOnTime: 100m);

        using (var firstProvider = Compose(
            [machineA, machineB],
            aggregationStore,
            projectionStore))
        {
            var runtimes = firstProvider.GetRequiredService<
                OperationalMetricProjectionProcessingRuntimeSet>();
            var reports = firstProvider.GetRequiredService<
                IOperationalMetricReportReader>();

            Assert.True(await runtimes.RunCycleAsync());

            var reportA = await reports.ReadProductionDayAsync(
                ProjectionProcessorId(machineA),
                machineA,
                day,
                OperationalMetricEvaluationContextKey.Unpartitioned,
                CancellationToken.None);
            var reportB = await reports.ReadProductionDayAsync(
                ProjectionProcessorId(machineB),
                machineB,
                day,
                OperationalMetricEvaluationContextKey.Unpartitioned,
                CancellationToken.None);

            Assert.NotNull(reportA);
            Assert.NotNull(reportB);
            Assert.Equal(5, reportA.Metrics.Count);
            Assert.Equal(5, reportB.Metrics.Count);
            Assert.Equal(
                0.5m,
                Find(reportA, BuiltInOperationalMetricDefinitions.AvailabilityId).Value);
            Assert.Equal(
                0.8m,
                Find(reportB, BuiltInOperationalMetricDefinitions.AvailabilityId).Value);
            Assert.Equal(
                0.36m,
                Find(reportA, BuiltInOperationalMetricDefinitions.OeeId).Value);
            Assert.Equal(
                0.64m,
                Find(reportB, BuiltInOperationalMetricDefinitions.OeeId).Value);
            Assert.NotEqual(reportA.SourceRevision.StreamId, reportB.SourceRevision.StreamId);
        }

        var checkpointA = await projectionStore.ReadCheckpointAsync(
            ProjectionProcessorId(machineA),
            MetricInputStreamId.ForMachine(machineA),
            CancellationToken.None);
        var checkpointB = await projectionStore.ReadCheckpointAsync(
            ProjectionProcessorId(machineB),
            MetricInputStreamId.ForMachine(machineB),
            CancellationToken.None);
        Assert.NotNull(checkpointA);
        Assert.NotNull(checkpointB);

        using var restartedProvider = Compose(
            [machineA, machineB],
            aggregationStore,
            projectionStore);
        var restartedRuntimes = restartedProvider.GetRequiredService<
            OperationalMetricProjectionProcessingRuntimeSet>();
        var restartedReports = restartedProvider.GetRequiredService<
            IOperationalMetricReportReader>();

        Assert.False(await restartedRuntimes.RunCycleAsync());

        var restartedReport = await restartedReports.ReadProductionDayAsync(
            ProjectionProcessorId(machineA),
            machineA,
            day,
            OperationalMetricEvaluationContextKey.Unpartitioned,
            CancellationToken.None);
        Assert.NotNull(restartedReport);
        Assert.Equal(checkpointA.SourceRevision, restartedReport.SourceRevision);
        Assert.Equal(
            0.36m,
            Find(restartedReport, BuiltInOperationalMetricDefinitions.OeeId).Value);
    }

    [Fact]
    public async Task FailureInOneMachineDoesNotPreventAnotherMachineFromCommitting()
    {
        var healthyMachine = MachineId.New();
        var failingMachine = MachineId.New();
        var siteId = new SiteId("site-a");
        var day = new ProductionDayId(siteId, new DateOnly(2026, 8, 29));
        var aggregationStore = new InMemoryMetricAggregationStore();
        var projectionStore = new InMemoryOperationalMetricProjectionStore();

        await CommitMetricInputsAsync(
            aggregationStore,
            healthyMachine,
            siteId,
            day,
            actualProductionTime: 50m,
            plannedOperatingTime: 100m,
            productionReferenceTime: 40m,
            producedQuantity: 10m,
            goodQuantity: 9m,
            machinePowerOnTime: 120m);
        await CommitMetricInputsAsync(
            aggregationStore,
            failingMachine,
            siteId,
            day,
            actualProductionTime: 200m,
            plannedOperatingTime: 100m,
            productionReferenceTime: 100m,
            producedQuantity: 10m,
            goodQuantity: 10m,
            machinePowerOnTime: 200m);

        using var provider = Compose(
            [healthyMachine, failingMachine],
            aggregationStore,
            projectionStore);
        var runtimes = provider.GetRequiredService<
            OperationalMetricProjectionProcessingRuntimeSet>();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await runtimes.RunCycleAsync());

        var healthyCheckpoint = await projectionStore.ReadCheckpointAsync(
            ProjectionProcessorId(healthyMachine),
            MetricInputStreamId.ForMachine(healthyMachine),
            CancellationToken.None);
        var failingCheckpoint = await projectionStore.ReadCheckpointAsync(
            ProjectionProcessorId(failingMachine),
            MetricInputStreamId.ForMachine(failingMachine),
            CancellationToken.None);

        Assert.NotNull(healthyCheckpoint);
        Assert.Null(failingCheckpoint);
    }

    [Fact]
    public async Task PreCancelledComposedCycleDoesNotAdvanceProjectionCheckpoint()
    {
        var machineId = MachineId.New();
        var aggregationStore = new InMemoryMetricAggregationStore();
        var projectionStore = new InMemoryOperationalMetricProjectionStore();
        using var provider = Compose(
            [machineId],
            aggregationStore,
            projectionStore);
        var runtimes = provider.GetRequiredService<
            OperationalMetricProjectionProcessingRuntimeSet>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await runtimes.RunCycleAsync(cancellation.Token));

        Assert.Null(await projectionStore.ReadCheckpointAsync(
            ProjectionProcessorId(machineId),
            MetricInputStreamId.ForMachine(machineId),
            CancellationToken.None));
    }

    private static ServiceProvider Compose(
        IReadOnlyList<MachineId> machineIds,
        InMemoryMetricAggregationStore aggregationStore,
        InMemoryOperationalMetricProjectionStore projectionStore)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMetricAggregationRevisionReader>(aggregationStore);
        services.AddSingleton<IRevisionedOperationalMetricComponentSnapshotReader>(aggregationStore);
        services.AddSingleton<IOperationalMetricProjectionStore>(projectionStore);
        services.AddSingleton<IOperationalMetricProjectionQueryReader>(projectionStore);
        services.AddFactoryConnectOperationalMetrics(Configuration(), machineIds);
        return services.BuildServiceProvider();
    }

    private static async Task CommitMetricInputsAsync(
        InMemoryMetricAggregationStore store,
        MachineId machineId,
        SiteId siteId,
        ProductionDayId day,
        decimal actualProductionTime,
        decimal plannedOperatingTime,
        decimal productionReferenceTime,
        decimal producedQuantity,
        decimal goodQuantity,
        decimal machinePowerOnTime)
    {
        var streamId = MetricInputStreamId.ForMachine(machineId);
        var processorId = AggregationProcessorId(machineId);
        var assignmentId = new ShiftScheduleAssignmentId(
            $"schedule-{machineId.Value:D}");
        var shiftId = new ShiftId("shift-a");
        var occurrence = new ShiftOccurrenceId(
            siteId,
            assignmentId,
            shiftId,
            DayStartsAt,
            DayStartsAt.AddHours(8));
        var revision = new MetricAggregationCheckpoint(
            processorId,
            streamId,
            new MetricInputPosition(6));

        await store.CommitAsync(
            new MetricAggregationCommit(
                processorId,
                null,
                revision,
                [
                    Input(streamId, 1, machineId, siteId, shiftId, assignmentId, occurrence, day, MetricInputKeys.ActualProductionTime, actualProductionTime, MetricInputFactUnits.Seconds),
                    Input(streamId, 2, machineId, siteId, shiftId, assignmentId, occurrence, day, MetricInputKeys.PlannedOperatingTime, plannedOperatingTime, MetricInputFactUnits.Seconds),
                    Input(streamId, 3, machineId, siteId, shiftId, assignmentId, occurrence, day, MetricInputKeys.ProductionReferenceTime, productionReferenceTime, MetricInputFactUnits.Seconds),
                    Input(streamId, 4, machineId, siteId, shiftId, assignmentId, occurrence, day, MetricInputKeys.ProducedQuantity, producedQuantity, MetricInputFactUnits.Count),
                    Input(streamId, 5, machineId, siteId, shiftId, assignmentId, occurrence, day, MetricInputKeys.GoodQuantity, goodQuantity, MetricInputFactUnits.Count),
                    Input(streamId, 6, machineId, siteId, shiftId, assignmentId, occurrence, day, MetricInputKeys.MachinePowerOnTime, machinePowerOnTime, MetricInputFactUnits.Seconds),
                ]),
            CancellationToken.None);
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
                Id = new MetricInputFactId(
                    $"fc0276-{machineId.Value:D}-{position}"),
                Key = key,
                Value = value,
                Unit = unit,
                StartsAtUtc = occurrence.StartsAtUtc,
                EndsAtUtc = occurrence.StartsAtUtc.AddMinutes(1),
                CompanyId = new CompanyId("company-a"),
                SiteId = siteId,
                MachineId = machineId,
                ShiftId = shiftId,
                ShiftScheduleAssignmentId = assignmentId,
            },
            occurrence,
            day);

    private static OperationalMetricReportItem Find(
        ProductionDayOperationalMetricReport report,
        OperationalMetricDefinitionId definitionId) =>
        Assert.Single(
            report.Metrics,
            metric => metric.DefinitionId == definitionId);

    private static MetricAggregationProcessorId AggregationProcessorId(
        MachineId machineId) =>
        new($"metric-aggregation:{machineId.Value:D}");

    private static OperationalMetricProjectionProcessorId ProjectionProcessorId(
        MachineId machineId) =>
        new($"operational-metrics:{machineId.Value:D}:builtins-v1");

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OperationalMetrics:PollingInterval"] = "00:00:01",
                })
            .Build();
}
