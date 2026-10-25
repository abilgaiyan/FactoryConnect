using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class ProductionContextProcessingRuntimeTests
{
    [Fact]
    public async Task RuntimeProcessesMultipleBatchesAndResumesWithoutDuplicates()
    {
        var fixture = CreateFixture("LINE-1", "MACHINE-A", 8);
        fixture.ActivityReader.Add(CreateActivity(fixture.MachineId, fixture.StreamId, 1, fixture.Start, fixture.Start.AddHours(1)));
        fixture.ActivityReader.Add(CreateActivity(fixture.MachineId, fixture.StreamId, 2, fixture.Start.AddHours(1), fixture.Start.AddHours(2)));

        var firstRuntime = fixture.CreateRuntime(batchSize: 1);
        Assert.Equal(1, await firstRuntime.RunCycleAsync());

        var resumedRuntime = fixture.CreateRuntime(batchSize: 1);
        Assert.Equal(1, await resumedRuntime.RunCycleAsync());
        Assert.Equal(0, await resumedRuntime.RunCycleAsync());

        Assert.Equal(2, fixture.Store.ContextualizedActivity.Count);
        Assert.Equal(2, fixture.Store.EligibilityIntervals.Count);
        Assert.Equal(6, fixture.Store.MetricFacts.Count);

        var checkpoint = await fixture.Store.ReadCheckpointAsync(
            fixture.ProcessorId,
            fixture.StreamId,
            CancellationToken.None);
        Assert.NotNull(checkpoint);
        Assert.Equal(new ObservationPosition(2), checkpoint.Position);
    }

    [Fact]
    public async Task IndependentScopesKeepMachinesAndLinesIsolated()
    {
        var first = CreateFixture("LINE-1", "MACHINE-A", 8);
        var second = CreateFixture("LINE-2", "MACHINE-B", 8);
        var sharedStore = new InMemoryProductionContextProcessingStore();

        first.ActivityReader.Add(CreateActivity(first.MachineId, first.StreamId, 1, first.Start, first.Start.AddHours(1)));
        second.ActivityReader.Add(CreateActivity(second.MachineId, second.StreamId, 1, second.Start, second.Start.AddHours(1)));

        var firstRuntime = first.CreateRuntime(10, sharedStore);
        var secondRuntime = second.CreateRuntime(10, sharedStore);

        await firstRuntime.RunCycleAsync();
        await secondRuntime.RunCycleAsync();

        Assert.Equal(2, sharedStore.ContextualizedActivity.Count);
        Assert.Contains(sharedStore.ContextualizedActivity, item => item.MachineId == first.MachineId && item.ProductionLineId == first.LineId);
        Assert.Contains(sharedStore.ContextualizedActivity, item => item.MachineId == second.MachineId && item.ProductionLineId == second.LineId);

        var firstCheckpoint = await sharedStore.ReadCheckpointAsync(first.ProcessorId, first.StreamId, CancellationToken.None);
        var secondCheckpoint = await sharedStore.ReadCheckpointAsync(second.ProcessorId, second.StreamId, CancellationToken.None);
        Assert.NotNull(firstCheckpoint);
        Assert.NotNull(secondCheckpoint);
    }

    [Fact]
    public async Task MissingProductionContextPreservesActivityAndTimeFacts()
    {
        var fixture = CreateFixture("LINE-1", "MACHINE-A", 8, includeContext: false);
        fixture.ActivityReader.Add(CreateActivity(fixture.MachineId, fixture.StreamId, 1, fixture.Start, fixture.Start.AddHours(1)));

        await fixture.CreateRuntime(10).RunCycleAsync();

        var contextualized = Assert.Single(fixture.Store.ContextualizedActivity);
        Assert.Null(contextualized.ProductionContextAssignmentId);
        Assert.Equal(fixture.LineId, contextualized.ProductionLineId);

        Assert.Single(fixture.Store.EligibilityIntervals);
        Assert.Equal(3, fixture.Store.MetricFacts.Count);
    }

    [Fact]
    public async Task DurableCommitFailureDoesNotAdvanceCheckpoint()
    {
        var fixture = CreateFixture("LINE-1", "MACHINE-A", 8);
        fixture.ActivityReader.Add(CreateActivity(fixture.MachineId, fixture.StreamId, 1, fixture.Start, fixture.Start.AddHours(1)));
        var failingStore = new FailingProductionContextProcessingStore(fixture.Store);
        var runtime = fixture.CreateRuntime(10, failingStore);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RunCycleAsync());

        var checkpoint = await fixture.Store.ReadCheckpointAsync(
            fixture.ProcessorId,
            fixture.StreamId,
            CancellationToken.None);
        Assert.Null(checkpoint);
        Assert.Empty(fixture.Store.ContextualizedActivity);
        Assert.Empty(fixture.Store.EligibilityIntervals);
        Assert.Empty(fixture.Store.MetricFacts);
    }

    [Fact]
    public async Task BeforeBoundaryAdvancesWithoutOutputAndRestartResumes()
    {
        var fixture = CreateFixture("LINE-1", "MACHINE-A", 8);
        fixture = fixture with { Scope = fixture.Scope with { ProductionStartsAt = fixture.Start } };
        fixture.ActivityReader.Add(CreateActivity(fixture.MachineId, fixture.StreamId, 1,
            fixture.Start.AddDays(-300), fixture.Start));
        Assert.Equal(1, await fixture.CreateRuntime(1).RunCycleAsync());
        Assert.Empty(fixture.Store.ContextualizedActivity);
        Assert.Empty(fixture.Store.MetricFacts);
        var checkpoint = await fixture.Store.ReadCheckpointAsync(fixture.ProcessorId,
            fixture.StreamId, fixture.Start, CancellationToken.None);
        Assert.Equal(new ObservationPosition(1), checkpoint!.Position);
        fixture.ActivityReader.Add(CreateActivity(fixture.MachineId, fixture.StreamId, 2,
            fixture.Start, fixture.Start.AddMinutes(1)));
        Assert.Equal(1, await fixture.CreateRuntime(1).RunCycleAsync());
        Assert.Equal(fixture.Start, Assert.Single(fixture.Store.ContextualizedActivity).StartsAtUtc);
    }

    [Fact]
    public async Task CrossingBoundaryPreservesSourceAndAdmitsOnlyTail()
    {
        var fixture = CreateFixture("LINE-1", "MACHINE-A", 8);
        fixture = fixture with { Scope = fixture.Scope with { ProductionStartsAt = fixture.Start } };
        var original = CreateActivity(fixture.MachineId, fixture.StreamId, 1,
            fixture.Start.AddDays(-300), fixture.Start.AddMinutes(1));
        fixture.ActivityReader.Add(original);
        Assert.Equal(1, await fixture.CreateRuntime(10).RunCycleAsync());
        var output = Assert.Single(fixture.Store.ContextualizedActivity);
        Assert.Equal(fixture.Start, output.StartsAtUtc);
        Assert.Equal(fixture.Start.AddMinutes(1), output.EndsAtUtc);
        Assert.Equal(original.Position, output.SourcePosition);
        Assert.Equal(original.InstanceId, output.SourceInstanceId);
        Assert.Equal(original.Sequence, output.SourceSequence);
        Assert.Equal(fixture.Start.AddDays(-300), original.Period.StartedAt);
        Assert.Equal(0, await fixture.CreateRuntime(10).RunCycleAsync());
        Assert.Single(fixture.Store.ContextualizedActivity);
    }

    [Fact]
    public async Task BoundaryBindingRejectsChangedOrRemovedStart()
    {
        var fixture = CreateFixture("LINE-1", "MACHINE-A", 8);
        fixture = fixture with { Scope = fixture.Scope with { ProductionStartsAt = fixture.Start } };
        Assert.Equal(0, await fixture.CreateRuntime(10).RunCycleAsync());
        var changed = fixture with { Scope = fixture.Scope with { ProductionStartsAt = fixture.Start.AddSeconds(1) } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => changed.CreateRuntime(10).RunCycleAsync());
        var removed = fixture with { Scope = fixture.Scope with { ProductionStartsAt = null } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => removed.CreateRuntime(10).RunCycleAsync());
    }

    [Fact]
    public async Task ExistingProcessedStreamCannotAcquireBoundary()
    {
        var fixture = CreateFixture("LINE-1", "MACHINE-A", 8);
        fixture.ActivityReader.Add(CreateActivity(fixture.MachineId, fixture.StreamId, 1,
            fixture.Start, fixture.Start.AddMinutes(1)));
        await fixture.CreateRuntime(10).RunCycleAsync();
        fixture = fixture with { Scope = fixture.Scope with { ProductionStartsAt = fixture.Start } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateRuntime(10).RunCycleAsync());
    }

    [Fact]
    public async Task AdmittedShiftGapStillFailsWithoutCheckpoint()
    {
        var fixture = CreateFixture("LINE-1", "MACHINE-A", 0);
        fixture = fixture with { Scope = fixture.Scope with { ProductionStartsAt = fixture.Start } };
        fixture.ActivityReader.Add(CreateActivity(fixture.MachineId, fixture.StreamId, 1,
            fixture.Start, fixture.Start.AddHours(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateRuntime(10).RunCycleAsync());
        Assert.Null(await fixture.Store.ReadCheckpointAsync(fixture.ProcessorId,
            fixture.StreamId, fixture.Start, CancellationToken.None));
    }

    [Fact]
    public async Task ExcludedBatchCommitFailureRetriesWithoutAdvancing()
    {
        var fixture = CreateFixture("LINE-1", "MACHINE-A", 8);
        fixture = fixture with { Scope = fixture.Scope with { ProductionStartsAt = fixture.Start } };
        fixture.ActivityReader.Add(CreateActivity(fixture.MachineId, fixture.StreamId, 1,
            fixture.Start.AddHours(-1), fixture.Start));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateRuntime(10,
            new FailingProductionContextProcessingStore(fixture.Store)).RunCycleAsync());
        Assert.Null(await fixture.Store.ReadCheckpointAsync(fixture.ProcessorId,
            fixture.StreamId, fixture.Start, CancellationToken.None));
        Assert.Equal(1, await fixture.CreateRuntime(10).RunCycleAsync());
        Assert.Empty(fixture.Store.PositionedMetricInputs);
    }

    private static RuntimeFixture CreateFixture(
        string line,
        string machineName,
        int startHour,
        bool includeContext = true)
    {
        var machineId = new MachineId(CreateStableGuid(machineName));
        var streamId = new ObservationStreamId(machineId, "activity");
        var lineId = new ProductionLineId(line);
        var start = new DateTimeOffset(2026, 8, 26, startHour, 0, 0, TimeSpan.Zero);
        var siteId = new SiteId("SITE-1");
        var companyId = new CompanyId("COMP-1");
        var activityReader = new InMemoryProductionContextActivityReader();
        var contextReader = includeContext
            ? new InMemoryProductionContextReader([
                new ProductionContextAssignment
                {
                    Id = new ProductionContextAssignmentId($"CTX-{machineName}"),
                    CompanyId = companyId,
                    SiteId = siteId,
                    ProductionLineId = lineId,
                    MachineId = machineId,
                    EffectiveFrom = start.AddDays(-1),
                },
            ])
            : new InMemoryProductionContextReader();

        var shiftReader = new InMemoryShiftScheduleReader([
            new ShiftScheduleAssignment
            {
                Id = new ShiftScheduleAssignmentId($"SHIFT-{line}"),
                CompanyId = companyId,
                SiteId = siteId,
                ProductionLineId = lineId,
                TimeZoneId = new FactoryTimeZoneId("UTC"),
                ShiftId = new ShiftId("SHIFT-1"),
                Name = "Shift 1",
                StartsAtLocal = new TimeOnly(0, 1),
                EndsAtLocal = new TimeOnly(23, 59),
                EffectiveFrom = new DateOnly(2026, 1, 1),
            },
        ]);

        var plannedReader = new InMemoryPlannedProductionScheduleReader([
            new PlannedProductionScheduleAssignment
            {
                Id = new PlannedProductionScheduleAssignmentId($"POT-{line}"),
                CompanyId = companyId,
                SiteId = siteId,
                ProductionLineId = lineId,
                TimeZoneId = new FactoryTimeZoneId("UTC"),
                EffectiveFrom = new DateOnly(2026, 1, 1),
                PlannedWindows = [
                    new PlannedProductionWindow
                    {
                        StartsAtLocal = new TimeOnly(0, 1),
                        EndsAtLocal = new TimeOnly(23, 59),
                    },
                ],
            },
        ]);

        return new RuntimeFixture(
            machineId,
            streamId,
            lineId,
            start,
            new ObservationProcessorId($"production-context-{machineName}"),
            activityReader,
            contextReader,
            new ShiftOccurrenceResolver(shiftReader),
            new PlannedProductionIntervalResolver(plannedReader),
            new InMemoryProductionContextProcessingStore(),
            new ProductionContextProcessingScope
            {
                CompanyId = companyId,
                SiteId = siteId,
                ProductionLineId = lineId,
                MachineId = machineId,
                StreamId = streamId,
            });
    }

    private static DurableMachineActivityPeriod CreateActivity(
        MachineId machineId,
        ObservationStreamId streamId,
        ulong position,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt) =>
        new(
            new ObservationProcessorId("activity-projection"),
            new ObservationPosition(position),
            streamId,
            1,
            position,
            new MachineActivityPeriod(machineId, MachineState.Running, startsAt, endsAt));

    private static Guid CreateStableGuid(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    private sealed record RuntimeFixture(
        MachineId MachineId,
        ObservationStreamId StreamId,
        ProductionLineId LineId,
        DateTimeOffset Start,
        ObservationProcessorId ProcessorId,
        InMemoryProductionContextActivityReader ActivityReader,
        InMemoryProductionContextReader ContextReader,
        ShiftOccurrenceResolver ShiftResolver,
        PlannedProductionIntervalResolver PlannedResolver,
        InMemoryProductionContextProcessingStore Store,
        ProductionContextProcessingScope Scope)
    {
        public ProductionContextProcessingRuntime CreateRuntime(
            int batchSize,
            IProductionContextProcessingStore? store = null) =>
            new(
                ProcessorId,
                ActivityReader,
                ContextReader,
                ShiftResolver,
                PlannedResolver,
                store ?? Store,
                Scope,
                batchSize);
    }

    private sealed class FailingProductionContextProcessingStore : IProductionContextProcessingStore
    {
        private readonly IProductionContextProcessingStore _inner;

        public FailingProductionContextProcessingStore(IProductionContextProcessingStore inner)
        {
            _inner = inner;
        }

        public Task<ObservationProcessingCheckpoint?> ReadCheckpointAsync(
            ObservationProcessorId processorId,
            ObservationStreamId streamId,
            CancellationToken cancellationToken) =>
            _inner.ReadCheckpointAsync(processorId, streamId, cancellationToken);

        public Task<ObservationProcessingCheckpoint?> ReadCheckpointAsync(
            ObservationProcessorId processorId,
            ObservationStreamId streamId,
            DateTimeOffset? productionStartsAt,
            CancellationToken cancellationToken) =>
            _inner.ReadCheckpointAsync(processorId, streamId, productionStartsAt, cancellationToken);

        public Task CommitAsync(
            ProductionContextProcessingCommit commit,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Injected durable commit failure.");
    }
}
