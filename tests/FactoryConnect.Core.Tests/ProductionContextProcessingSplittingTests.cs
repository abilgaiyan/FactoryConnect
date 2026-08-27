using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class ProductionContextProcessingSplittingTests
{
    [Fact]
    public async Task RuntimeComposesContextShiftPlannedAndBreakSplitsWithConservation()
    {
        var machineId = new MachineId(new Guid("22222222-2222-2222-2222-222222222222"));
        var streamId = new ObservationStreamId(machineId, "activity");
        var companyId = new CompanyId("COMP-1");
        var siteId = new SiteId("SITE-1");
        var lineId = new ProductionLineId("LINE-1");
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(4);
        var activityReader = new InMemoryProductionContextActivityReader();
        activityReader.Add(CreateActivity(machineId, streamId, start, end));

        var contextReader = new InMemoryProductionContextReader([
            CreateContext("CTX-A", companyId, siteId, lineId, machineId, start, start.AddHours(1)),
            CreateContext("CTX-B", companyId, siteId, lineId, machineId, start.AddHours(1), null),
        ]);

        var shiftResolver = new ShiftOccurrenceResolver(new InMemoryShiftScheduleReader([
            CreateShift("SHIFT-A", "A", companyId, siteId, lineId, new TimeOnly(8, 0), new TimeOnly(10, 0)),
            CreateShift("SHIFT-B", "B", companyId, siteId, lineId, new TimeOnly(10, 0), new TimeOnly(12, 0)),
        ]));

        var plannedResolver = new PlannedProductionIntervalResolver(
            new InMemoryPlannedProductionScheduleReader([
                new PlannedProductionScheduleAssignment
                {
                    Id = new PlannedProductionScheduleAssignmentId("POT-1"),
                    CompanyId = companyId,
                    SiteId = siteId,
                    ProductionLineId = lineId,
                    TimeZoneId = new FactoryTimeZoneId("UTC"),
                    EffectiveFrom = new DateOnly(2026, 1, 1),
                    PlannedWindows = [
                        new PlannedProductionWindow
                        {
                            StartsAtLocal = new TimeOnly(8, 0),
                            EndsAtLocal = new TimeOnly(11, 30),
                        },
                    ],
                    BreakWindows = [
                        new PlannedProductionWindow
                        {
                            StartsAtLocal = new TimeOnly(10, 30),
                            EndsAtLocal = new TimeOnly(11, 0),
                        },
                    ],
                },
            ]));

        var store = new InMemoryProductionContextProcessingStore();
        var processorId = new ObservationProcessorId("fc025-split");
        var scope = new ProductionContextProcessingScope
        {
            CompanyId = companyId,
            SiteId = siteId,
            ProductionLineId = lineId,
            MachineId = machineId,
            StreamId = streamId,
        };

        var runtime = new ProductionContextProcessingRuntime(
            processorId,
            activityReader,
            contextReader,
            shiftResolver,
            plannedResolver,
            store,
            scope,
            10);

        Assert.Equal(1, await runtime.RunCycleAsync());

        Assert.Equal(3, store.ContextualizedActivity.Count);
        Assert.Equal(TimeSpan.FromHours(4), SumDuration(store.ContextualizedActivity));
        Assert.Equal(TimeSpan.FromHours(4), SumDuration(store.EligibilityIntervals));
        Assert.Contains(store.ContextualizedActivity, item => item.EndsAtUtc == start.AddHours(1));
        Assert.Contains(store.ContextualizedActivity, item => item.EndsAtUtc == start.AddHours(2));
        Assert.Contains(store.EligibilityIntervals, item => item.StartsAtUtc == start.AddHours(2.5) && !item.IsPlannedProductionTime);
        Assert.Contains(store.EligibilityIntervals, item => item.StartsAtUtc == start.AddHours(3.5) && !item.IsPlannedProductionTime);

        var scheduledSeconds = store.MetricFacts
            .Where(fact => fact.Key == MetricInputFactKeys.ScheduledDuration)
            .Sum(static fact => fact.Value);
        Assert.Equal(14400m, scheduledSeconds);

        var contextualizedCount = store.ContextualizedActivity.Count;
        var eligibilityCount = store.EligibilityIntervals.Count;
        var metricCount = store.MetricFacts.Count;
        var restarted = new ProductionContextProcessingRuntime(
            processorId,
            activityReader,
            contextReader,
            shiftResolver,
            plannedResolver,
            store,
            scope,
            10);

        Assert.Equal(0, await restarted.RunCycleAsync());
        Assert.Equal(contextualizedCount, store.ContextualizedActivity.Count);
        Assert.Equal(eligibilityCount, store.EligibilityIntervals.Count);
        Assert.Equal(metricCount, store.MetricFacts.Count);
    }

    [Fact]
    public async Task MissingContextForOnlyPartOfActivityIsPreserved()
    {
        var machineId = new MachineId(new Guid("33333333-3333-3333-3333-333333333333"));
        var streamId = new ObservationStreamId(machineId, "activity");
        var companyId = new CompanyId("COMP-1");
        var siteId = new SiteId("SITE-1");
        var lineId = new ProductionLineId("LINE-1");
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var reader = new InMemoryProductionContextActivityReader();
        reader.Add(CreateActivity(machineId, streamId, start, start.AddHours(3)));
        var contexts = new InMemoryProductionContextReader([
            CreateContext("CTX-A", companyId, siteId, lineId, machineId, start, start.AddHours(1)),
            CreateContext("CTX-B", companyId, siteId, lineId, machineId, start.AddHours(2), null),
        ]);
        var shifts = new ShiftOccurrenceResolver(new InMemoryShiftScheduleReader([
            CreateShift("SHIFT-A", "A", companyId, siteId, lineId, new TimeOnly(8, 0), new TimeOnly(11, 0)),
        ]));
        var planned = new PlannedProductionIntervalResolver(new InMemoryPlannedProductionScheduleReader([
            new PlannedProductionScheduleAssignment
            {
                Id = new PlannedProductionScheduleAssignmentId("POT-1"),
                CompanyId = companyId,
                SiteId = siteId,
                ProductionLineId = lineId,
                TimeZoneId = new FactoryTimeZoneId("UTC"),
                EffectiveFrom = new DateOnly(2026, 1, 1),
                PlannedWindows = [new PlannedProductionWindow { StartsAtLocal = new TimeOnly(8, 0), EndsAtLocal = new TimeOnly(11, 0) }],
            },
        ]));
        var store = new InMemoryProductionContextProcessingStore();
        var runtime = new ProductionContextProcessingRuntime(
            new ObservationProcessorId("fc025-gap"),
            reader,
            contexts,
            shifts,
            planned,
            store,
            new ProductionContextProcessingScope
            {
                CompanyId = companyId,
                SiteId = siteId,
                ProductionLineId = lineId,
                MachineId = machineId,
                StreamId = streamId,
            },
            10);

        await runtime.RunCycleAsync();

        Assert.Equal(3, store.ContextualizedActivity.Count);
        Assert.Contains(store.ContextualizedActivity, item =>
            item.StartsAtUtc == start.AddHours(1) &&
            item.EndsAtUtc == start.AddHours(2) &&
            item.ProductionContextAssignmentId is null);
        Assert.Equal(TimeSpan.FromHours(3), SumDuration(store.ContextualizedActivity));
    }

    private static ProductionContextAssignment CreateContext(
        string id,
        CompanyId companyId,
        SiteId siteId,
        ProductionLineId lineId,
        MachineId machineId,
        DateTimeOffset from,
        DateTimeOffset? to) =>
        new()
        {
            Id = new ProductionContextAssignmentId(id),
            CompanyId = companyId,
            SiteId = siteId,
            ProductionLineId = lineId,
            MachineId = machineId,
            EffectiveFrom = from,
            EffectiveTo = to,
        };

    private static ShiftScheduleAssignment CreateShift(
        string id,
        string shift,
        CompanyId companyId,
        SiteId siteId,
        ProductionLineId lineId,
        TimeOnly from,
        TimeOnly to) =>
        new()
        {
            Id = new ShiftScheduleAssignmentId(id),
            CompanyId = companyId,
            SiteId = siteId,
            ProductionLineId = lineId,
            TimeZoneId = new FactoryTimeZoneId("UTC"),
            ShiftId = new ShiftId(shift),
            Name = shift,
            StartsAtLocal = from,
            EndsAtLocal = to,
            EffectiveFrom = new DateOnly(2026, 1, 1),
        };

    private static DurableMachineActivityPeriod CreateActivity(
        MachineId machineId,
        ObservationStreamId streamId,
        DateTimeOffset from,
        DateTimeOffset to) =>
        new(
            new ObservationProcessorId("activity-projection"),
            new ObservationPosition(1),
            streamId,
            1,
            1,
            new MachineActivityPeriod(machineId, MachineState.Running, from, to));

    private static TimeSpan SumDuration(IEnumerable<ContextualizedActivityInterval> items) =>
        items.Aggregate(TimeSpan.Zero, static (total, item) => total + item.Duration);

    private static TimeSpan SumDuration(IEnumerable<ProductionTimeEligibilityInterval> items) =>
        items.Aggregate(TimeSpan.Zero, static (total, item) => total + item.Duration);
}
