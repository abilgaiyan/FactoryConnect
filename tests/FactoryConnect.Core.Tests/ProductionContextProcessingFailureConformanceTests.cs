using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class ProductionContextProcessingFailureConformanceTests
{
    public static TheoryData<FailureBoundary> FailureBoundaries =>
        new()
        {
            FailureBoundary.CheckpointRestore,
            FailureBoundary.ActivityRead,
            FailureBoundary.ContextRead,
            FailureBoundary.ShiftAssignmentRead,
            FailureBoundary.ShiftOverrideRead,
            FailureBoundary.PlannedAssignmentRead,
            FailureBoundary.PlannedOverrideRead,
        };

    [Theory]
    [MemberData(nameof(FailureBoundaries))]
    public async Task ProviderFailurePropagatesWithoutProgressAndRetryProcessesExactlyOnce(
        FailureBoundary boundary)
    {
        var fixture = CreateFixture(boundary);

        var error = await Assert.ThrowsAsync<InjectedBoundaryException>(() =>
            fixture.Runtime.RunCycleAsync());
        Assert.Equal(boundary, error.Boundary);
        Assert.Empty(fixture.InnerStore.ContextualizedActivity);
        Assert.Empty(fixture.InnerStore.EligibilityIntervals);
        Assert.Empty(fixture.InnerStore.MetricFacts);
        Assert.Null(await fixture.InnerStore.ReadCheckpointAsync(
            fixture.ProcessorId,
            fixture.StreamId,
            CancellationToken.None));

        Assert.Equal(1, await fixture.Runtime.RunCycleAsync());
        Assert.Single(fixture.InnerStore.ContextualizedActivity);
        Assert.Single(fixture.InnerStore.EligibilityIntervals);
        Assert.Equal(3, fixture.InnerStore.MetricFacts.Count);
        Assert.NotNull(await fixture.InnerStore.ReadCheckpointAsync(
            fixture.ProcessorId,
            fixture.StreamId,
            CancellationToken.None));

        var restarted = fixture.CreateRestartedRuntime();
        Assert.Equal(0, await restarted.RunCycleAsync());
        Assert.Single(fixture.InnerStore.ContextualizedActivity);
        Assert.Single(fixture.InnerStore.EligibilityIntervals);
        Assert.Equal(3, fixture.InnerStore.MetricFacts.Count);
    }

    private static FailureFixture CreateFixture(FailureBoundary boundary)
    {
        var machineId = new MachineId(new Guid("44444444-4444-4444-4444-444444444444"));
        var streamId = new ObservationStreamId(machineId, "activity");
        var processorId = new ObservationProcessorId("fc025-failure");
        var companyId = new CompanyId("COMP-1");
        var siteId = new SiteId("SITE-1");
        var lineId = new ProductionLineId("LINE-1");
        var start = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);

        var activityInner = new InMemoryProductionContextActivityReader();
        activityInner.Add(new DurableMachineActivityPeriod(
            new ObservationProcessorId("activity-projection"),
            new ObservationPosition(1),
            streamId,
            1,
            1,
            new MachineActivityPeriod(machineId, MachineState.Running, start, start.AddHours(1))));
        IProductionContextActivityReader activityReader =
            new FailOnceActivityReader(activityInner, boundary == FailureBoundary.ActivityRead);

        var contextInner = new InMemoryProductionContextReader([
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
        IProductionContextReader contextReader =
            new FailOnceContextReader(contextInner, boundary == FailureBoundary.ContextRead);

        var shiftInner = new InMemoryShiftScheduleReader([
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
        ]);
        var shiftReader = new FailOnceShiftReader(
            shiftInner,
            boundary == FailureBoundary.ShiftAssignmentRead,
            boundary == FailureBoundary.ShiftOverrideRead);

        var plannedInner = new InMemoryPlannedProductionScheduleReader([
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
                        EndsAtLocal = new TimeOnly(9, 0),
                    },
                ],
            },
        ]);
        var plannedReader = new FailOncePlannedReader(
            plannedInner,
            boundary == FailureBoundary.PlannedAssignmentRead,
            boundary == FailureBoundary.PlannedOverrideRead);

        var innerStore = new InMemoryProductionContextProcessingStore();
        IProductionContextProcessingStore store =
            new FailOnceCheckpointStore(innerStore, boundary == FailureBoundary.CheckpointRestore);
        var scope = new ProductionContextProcessingScope
        {
            CompanyId = companyId,
            SiteId = siteId,
            ProductionLineId = lineId,
            MachineId = machineId,
            StreamId = streamId,
        };

        ProductionContextProcessingRuntime Factory() =>
            new(
                processorId,
                activityReader,
                contextReader,
                new ShiftOccurrenceResolver(shiftReader),
                new PlannedProductionIntervalResolver(plannedReader),
                store,
                scope,
                10);

        return new FailureFixture(
            processorId,
            streamId,
            innerStore,
            Factory(),
            Factory);
    }

    public enum FailureBoundary
    {
        CheckpointRestore,
        ActivityRead,
        ContextRead,
        ShiftAssignmentRead,
        ShiftOverrideRead,
        PlannedAssignmentRead,
        PlannedOverrideRead,
    }

    private sealed class InjectedBoundaryException : Exception
    {
        public InjectedBoundaryException(FailureBoundary boundary)
            : base($"Injected failure at {boundary}.")
        {
            Boundary = boundary;
        }

        public FailureBoundary Boundary { get; }
    }

    private sealed record FailureFixture(
        ObservationProcessorId ProcessorId,
        ObservationStreamId StreamId,
        InMemoryProductionContextProcessingStore InnerStore,
        ProductionContextProcessingRuntime Runtime,
        Func<ProductionContextProcessingRuntime> RuntimeFactory)
    {
        public ProductionContextProcessingRuntime CreateRestartedRuntime() => RuntimeFactory();
    }

    private sealed class FailOnceCheckpointStore : IProductionContextProcessingStore
    {
        private readonly IProductionContextProcessingStore _inner;
        private bool _fail;

        public FailOnceCheckpointStore(IProductionContextProcessingStore inner, bool fail)
        {
            _inner = inner;
            _fail = fail;
        }

        public Task<ObservationProcessingCheckpoint?> ReadCheckpointAsync(
            ObservationProcessorId processorId,
            ObservationStreamId streamId,
            CancellationToken cancellationToken)
        {
            if (_fail)
            {
                _fail = false;
                throw new InjectedBoundaryException(FailureBoundary.CheckpointRestore);
            }

            return _inner.ReadCheckpointAsync(processorId, streamId, cancellationToken);
        }

        public Task CommitAsync(ProductionContextProcessingCommit commit, CancellationToken cancellationToken) =>
            _inner.CommitAsync(commit, cancellationToken);
    }

    private sealed class FailOnceActivityReader : IProductionContextActivityReader
    {
        private readonly IProductionContextActivityReader _inner;
        private bool _fail;

        public FailOnceActivityReader(IProductionContextActivityReader inner, bool fail)
        {
            _inner = inner;
            _fail = fail;
        }

        public Task<IReadOnlyList<DurableMachineActivityPeriod>> ReadAsync(
            ObservationStreamId streamId,
            ObservationPosition? afterPosition,
            int batchSize,
            CancellationToken cancellationToken)
        {
            if (_fail)
            {
                _fail = false;
                throw new InjectedBoundaryException(FailureBoundary.ActivityRead);
            }

            return _inner.ReadAsync(streamId, afterPosition, batchSize, cancellationToken);
        }
    }

    private sealed class FailOnceContextReader : IProductionContextReader
    {
        private readonly IProductionContextReader _inner;
        private bool _fail;

        public FailOnceContextReader(IProductionContextReader inner, bool fail)
        {
            _inner = inner;
            _fail = fail;
        }

        public Task<ProductionContextAssignment?> ResolveAsync(
            MachineId machineId,
            DateTimeOffset timestamp,
            CancellationToken cancellationToken) =>
            _inner.ResolveAsync(machineId, timestamp, cancellationToken);

        public Task<IReadOnlyList<ProductionContextAssignment>> ReadAsync(
            MachineId machineId,
            DateTimeOffset effectiveFrom,
            DateTimeOffset effectiveTo,
            CancellationToken cancellationToken)
        {
            if (_fail)
            {
                _fail = false;
                throw new InjectedBoundaryException(FailureBoundary.ContextRead);
            }

            return _inner.ReadAsync(machineId, effectiveFrom, effectiveTo, cancellationToken);
        }
    }

    private sealed class FailOnceShiftReader : IShiftScheduleReader
    {
        private readonly IShiftScheduleReader _inner;
        private bool _failAssignments;
        private bool _failOverrides;

        public FailOnceShiftReader(IShiftScheduleReader inner, bool failAssignments, bool failOverrides)
        {
            _inner = inner;
            _failAssignments = failAssignments;
            _failOverrides = failOverrides;
        }

        public Task<IReadOnlyList<ShiftScheduleAssignment>> ReadAssignmentsAsync(
            SiteId siteId,
            DateOnly factoryDateFrom,
            DateOnly factoryDateTo,
            CancellationToken cancellationToken)
        {
            if (_failAssignments)
            {
                _failAssignments = false;
                throw new InjectedBoundaryException(FailureBoundary.ShiftAssignmentRead);
            }

            return _inner.ReadAssignmentsAsync(siteId, factoryDateFrom, factoryDateTo, cancellationToken);
        }

        public Task<IReadOnlyList<ShiftCalendarOverride>> ReadExceptionsAsync(
            SiteId siteId,
            DateOnly factoryDateFrom,
            DateOnly factoryDateTo,
            CancellationToken cancellationToken)
        {
            if (_failOverrides)
            {
                _failOverrides = false;
                throw new InjectedBoundaryException(FailureBoundary.ShiftOverrideRead);
            }

            return _inner.ReadExceptionsAsync(siteId, factoryDateFrom, factoryDateTo, cancellationToken);
        }
    }

    private sealed class FailOncePlannedReader : IPlannedProductionScheduleReader
    {
        private readonly IPlannedProductionScheduleReader _inner;
        private bool _failAssignments;
        private bool _failOverrides;

        public FailOncePlannedReader(IPlannedProductionScheduleReader inner, bool failAssignments, bool failOverrides)
        {
            _inner = inner;
            _failAssignments = failAssignments;
            _failOverrides = failOverrides;
        }

        public Task<IReadOnlyList<PlannedProductionScheduleAssignment>> ReadAssignmentsAsync(
            SiteId siteId,
            DateOnly factoryDateFrom,
            DateOnly factoryDateTo,
            CancellationToken cancellationToken)
        {
            if (_failAssignments)
            {
                _failAssignments = false;
                throw new InjectedBoundaryException(FailureBoundary.PlannedAssignmentRead);
            }

            return _inner.ReadAssignmentsAsync(siteId, factoryDateFrom, factoryDateTo, cancellationToken);
        }

        public Task<IReadOnlyList<PlannedProductionCalendarOverride>> ReadOverridesAsync(
            SiteId siteId,
            DateOnly factoryDateFrom,
            DateOnly factoryDateTo,
            CancellationToken cancellationToken)
        {
            if (_failOverrides)
            {
                _failOverrides = false;
                throw new InjectedBoundaryException(FailureBoundary.PlannedOverrideRead);
            }

            return _inner.ReadOverridesAsync(siteId, factoryDateFrom, factoryDateTo, cancellationToken);
        }
    }
}
