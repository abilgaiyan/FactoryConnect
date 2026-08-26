using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class ProductionContextProcessingCommitRetryTests
{
    [Fact]
    public async Task DurableCommitFailureCanRetrySameActivityExactlyOnce()
    {
        var machineId = new MachineId(new Guid("66666666-6666-6666-6666-666666666666"));
        var streamId = new ObservationStreamId(machineId, "activity");
        var processorId = new ObservationProcessorId("fc025-commit-retry");
        var companyId = new CompanyId("COMP-1");
        var siteId = new SiteId("SITE-1");
        var lineId = new ProductionLineId("LINE-1");
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var activityReader = new InMemoryProductionContextActivityReader();
        activityReader.Add(new DurableMachineActivityPeriod(
            new ObservationProcessorId("activity-projection"),
            new ObservationPosition(1),
            streamId,
            1,
            1,
            new MachineActivityPeriod(machineId, MachineState.Running, start, start.AddHours(1))));
        var contextReader = new InMemoryProductionContextReader([
            new ProductionContextAssignment
            {
                Id = new ProductionContextAssignmentId("CTX-1"),
                CompanyId = companyId,
                SiteId = siteId,
                ProductionLineId = lineId,
                MachineId = machineId,
                EffectiveFrom = start.AddDays(-1),
            },
        ]);
        var shiftResolver = new ShiftOccurrenceResolver(new InMemoryShiftScheduleReader([
            new ShiftScheduleAssignment
            {
                Id = new ShiftScheduleAssignmentId("SHIFT-SCHEDULE-1"),
                CompanyId = companyId,
                SiteId = siteId,
                ProductionLineId = lineId,
                TimeZoneId = new FactoryTimeZoneId("UTC"),
                ShiftId = new ShiftId("SHIFT-1"),
                Name = "Shift 1",
                StartsAtLocal = new TimeOnly(8, 0),
                EndsAtLocal = new TimeOnly(9, 0),
                EffectiveFrom = new DateOnly(2026, 1, 1),
            },
        ]));
        var plannedResolver = new PlannedProductionIntervalResolver(new InMemoryPlannedProductionScheduleReader([
            new PlannedProductionScheduleAssignment
            {
                Id = new PlannedProductionScheduleAssignmentId("POT-1"),
                CompanyId = companyId,
                SiteId = siteId,
                ProductionLineId = lineId,
                TimeZoneId = new FactoryTimeZoneId("UTC"),
                EffectiveFrom = new DateOnly(2026, 1, 1),
                PlannedWindows = [new PlannedProductionWindow { StartsAtLocal = new TimeOnly(8, 0), EndsAtLocal = new TimeOnly(9, 0) }],
            },
        ]));
        var innerStore = new InMemoryProductionContextProcessingStore();
        var store = new FailOnceCommitStore(innerStore);
        var runtime = new ProductionContextProcessingRuntime(
            processorId,
            activityReader,
            contextReader,
            shiftResolver,
            plannedResolver,
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

        await Assert.ThrowsAsync<CommitFailureException>(() => runtime.RunCycleAsync());
        Assert.Empty(innerStore.ContextualizedActivity);
        Assert.Empty(innerStore.EligibilityIntervals);
        Assert.Empty(innerStore.MetricFacts);
        Assert.Null(await innerStore.ReadCheckpointAsync(processorId, streamId, CancellationToken.None));

        Assert.Equal(1, await runtime.RunCycleAsync());
        Assert.Single(innerStore.ContextualizedActivity);
        Assert.Single(innerStore.EligibilityIntervals);
        Assert.Equal(3, innerStore.MetricFacts.Count);
        Assert.NotNull(await innerStore.ReadCheckpointAsync(processorId, streamId, CancellationToken.None));

        var restarted = new ProductionContextProcessingRuntime(
            processorId,
            activityReader,
            contextReader,
            shiftResolver,
            plannedResolver,
            innerStore,
            new ProductionContextProcessingScope
            {
                CompanyId = companyId,
                SiteId = siteId,
                ProductionLineId = lineId,
                MachineId = machineId,
                StreamId = streamId,
            },
            10);
        Assert.Equal(0, await restarted.RunCycleAsync());
        Assert.Equal(3, innerStore.MetricFacts.Count);
    }

    private sealed class CommitFailureException : Exception
    {
    }

    private sealed class FailOnceCommitStore : IProductionContextProcessingStore
    {
        private readonly IProductionContextProcessingStore _inner;
        private bool _fail = true;

        public FailOnceCommitStore(IProductionContextProcessingStore inner)
        {
            _inner = inner;
        }

        public Task<ObservationProcessingCheckpoint?> ReadCheckpointAsync(
            ObservationProcessorId processorId,
            ObservationStreamId streamId,
            CancellationToken cancellationToken) =>
            _inner.ReadCheckpointAsync(processorId, streamId, cancellationToken);

        public Task CommitAsync(ProductionContextProcessingCommit commit, CancellationToken cancellationToken)
        {
            if (_fail)
            {
                _fail = false;
                throw new CommitFailureException();
            }

            return _inner.CommitAsync(commit, cancellationToken);
        }
    }
}
