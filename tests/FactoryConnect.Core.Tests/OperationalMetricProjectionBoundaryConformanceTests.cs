using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricProjectionBoundaryConformanceTests
{
    private static readonly DateTimeOffset DayStartsAt = new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProductionDayMetricsUseProductionDayComponentsInsteadOfAveragingShiftRatios()
    {
        var machineId = MachineId.New();
        var siteId = new SiteId("site-a");
        var day = new ProductionDayId(siteId, new DateOnly(2026, 8, 29));
        var shiftIdA = new ShiftId("shift-a");
        var shiftIdB = new ShiftId("shift-b");
        var assignmentA = new ShiftScheduleAssignmentId("schedule-a");
        var assignmentB = new ShiftScheduleAssignmentId("schedule-b");
        var shiftA = new ShiftOccurrenceId(
            siteId,
            assignmentA,
            shiftIdA,
            DayStartsAt,
            DayStartsAt.AddHours(8));
        var shiftB = new ShiftOccurrenceId(
            siteId,
            assignmentB,
            shiftIdB,
            DayStartsAt.AddHours(8),
            DayStartsAt.AddHours(16));
        var streamId = MetricInputStreamId.ForMachine(machineId);
        var aggregationProcessorId = new MetricAggregationProcessorId("aggregate-weighted-day");
        var aggregationStore = new InMemoryMetricAggregationStore();
        var revision = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            streamId,
            new MetricInputPosition(12));

        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(
                aggregationProcessorId,
                null,
                revision,
                [
                    Input(streamId, 1, machineId, siteId, shiftIdA, assignmentA, shiftA, day, MetricInputKeys.ActualProductionTime, 50m, MetricInputFactUnits.Seconds),
                    Input(streamId, 2, machineId, siteId, shiftIdA, assignmentA, shiftA, day, MetricInputKeys.PlannedOperatingTime, 100m, MetricInputFactUnits.Seconds),
                    Input(streamId, 3, machineId, siteId, shiftIdA, assignmentA, shiftA, day, MetricInputKeys.ProductionReferenceTime, 50m, MetricInputFactUnits.Seconds),
                    Input(streamId, 4, machineId, siteId, shiftIdA, assignmentA, shiftA, day, MetricInputKeys.ProducedQuantity, 10m, MetricInputFactUnits.Count),
                    Input(streamId, 5, machineId, siteId, shiftIdA, assignmentA, shiftA, day, MetricInputKeys.GoodQuantity, 10m, MetricInputFactUnits.Count),
                    Input(streamId, 6, machineId, siteId, shiftIdA, assignmentA, shiftA, day, MetricInputKeys.MachinePowerOnTime, 100m, MetricInputFactUnits.Seconds),
                    Input(streamId, 7, machineId, siteId, shiftIdB, assignmentB, shiftB, day, MetricInputKeys.ActualProductionTime, 810m, MetricInputFactUnits.Seconds),
                    Input(streamId, 8, machineId, siteId, shiftIdB, assignmentB, shiftB, day, MetricInputKeys.PlannedOperatingTime, 900m, MetricInputFactUnits.Seconds),
                    Input(streamId, 9, machineId, siteId, shiftIdB, assignmentB, shiftB, day, MetricInputKeys.ProductionReferenceTime, 810m, MetricInputFactUnits.Seconds),
                    Input(streamId, 10, machineId, siteId, shiftIdB, assignmentB, shiftB, day, MetricInputKeys.ProducedQuantity, 90m, MetricInputFactUnits.Count),
                    Input(streamId, 11, machineId, siteId, shiftIdB, assignmentB, shiftB, day, MetricInputKeys.GoodQuantity, 90m, MetricInputFactUnits.Count),
                    Input(streamId, 12, machineId, siteId, shiftIdB, assignmentB, shiftB, day, MetricInputKeys.MachinePowerOnTime, 900m, MetricInputFactUnits.Seconds),
                ]),
            CancellationToken.None);

        var catalog = Catalog();
        var source = new CoherentOperationalMetricEvaluationBatchSource(
            catalog,
            aggregationStore,
            aggregationStore,
            aggregationProcessorId,
            streamId,
            OperationalMetricEvaluationContextKey.Unpartitioned);
        var batch = await source.ReadAsync(
            new OperationalMetricEvaluationBatchRequest(aggregationProcessorId, streamId, null),
            CancellationToken.None);

        Assert.NotNull(batch);
        Assert.Equal(15, batch.Evaluations.Count);
        Assert.Equal(0.5m, Find(batch, new OperationalMetricPeriodId.Shift(shiftA), BuiltInOperationalMetricDefinitions.AvailabilityId).Value);
        Assert.Equal(0.9m, Find(batch, new OperationalMetricPeriodId.Shift(shiftB), BuiltInOperationalMetricDefinitions.AvailabilityId).Value);

        var dayPeriod = new OperationalMetricPeriodId.ProductionDay(day);
        var dayAvailability = Find(batch, dayPeriod, BuiltInOperationalMetricDefinitions.AvailabilityId);
        var dayOee = Find(batch, dayPeriod, BuiltInOperationalMetricDefinitions.OeeId);
        Assert.Equal(0.86m, dayAvailability.Value);
        Assert.Equal(0.86m, dayOee.Value);
        Assert.NotEqual((0.5m + 0.9m) / 2m, dayAvailability.Value);
        Assert.NotEqual((0.5m + 0.9m) / 2m, dayOee.Value);
    }

    [Fact]
    public async Task PreCancelledBatchSourceReadDoesNotCallRevisionReader()
    {
        var fixture = SourceFailureFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Source.ReadAsync(
                new OperationalMetricEvaluationBatchRequest(
                    fixture.AggregationProcessorId,
                    fixture.StreamId,
                    null),
                cancellation.Token));

        Assert.Equal(0, fixture.RevisionReader.ReadCount);
    }

    [Fact]
    public async Task RevisionReaderFailureDoesNotAdvanceProjectionCheckpoint()
    {
        var fixture = SourceFailureFixture(revisionReaderFailure: new InvalidOperationException("revision failure"));
        var projectionStore = new InMemoryOperationalMetricProjectionStore();
        var runtime = Runtime(fixture, projectionStore);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runtime.RunCycleAsync());

        Assert.Null(await projectionStore.ReadCheckpointAsync(
            fixture.ProjectionProcessorId,
            fixture.StreamId,
            CancellationToken.None));
    }

    [Fact]
    public async Task ExactSnapshotFailureDoesNotAdvanceProjectionCheckpoint()
    {
        var fixture = SourceFailureFixture(snapshotFailure: new InvalidOperationException("snapshot failure"));
        var projectionStore = new InMemoryOperationalMetricProjectionStore();
        var runtime = Runtime(fixture, projectionStore);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runtime.RunCycleAsync());

        Assert.Null(await projectionStore.ReadCheckpointAsync(
            fixture.ProjectionProcessorId,
            fixture.StreamId,
            CancellationToken.None));
    }

    [Fact]
    public async Task WrongRevisionSnapshotDoesNotAdvanceProjectionCheckpoint()
    {
        var fixture = SourceFailureFixture(returnWrongRevision: true);
        var projectionStore = new InMemoryOperationalMetricProjectionStore();
        var runtime = Runtime(fixture, projectionStore);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await runtime.RunCycleAsync());

        Assert.Null(await projectionStore.ReadCheckpointAsync(
            fixture.ProjectionProcessorId,
            fixture.StreamId,
            CancellationToken.None));
    }

    [Fact]
    public async Task ConsecutiveNonEmptyRevisionsMayAffectDifferentPeriodSets()
    {
        var machineId = MachineId.New();
        var siteId = new SiteId("site-a");
        var day = new ProductionDayId(siteId, new DateOnly(2026, 8, 29));
        var shiftA = new ShiftOccurrenceId(
            siteId,
            new ShiftScheduleAssignmentId("schedule-a"),
            new ShiftId("shift-a"),
            DayStartsAt,
            DayStartsAt.AddHours(8));
        var shiftB = new ShiftOccurrenceId(
            siteId,
            new ShiftScheduleAssignmentId("schedule-b"),
            new ShiftId("shift-b"),
            DayStartsAt.AddHours(8),
            DayStartsAt.AddHours(16));
        var streamId = MetricInputStreamId.ForMachine(machineId);
        var processorId = new MetricAggregationProcessorId("aggregate-period-sets");
        var revision1 = new MetricAggregationCheckpoint(processorId, streamId, new MetricInputPosition(1));
        var revision2 = new MetricAggregationCheckpoint(processorId, streamId, new MetricInputPosition(2));
        var revisionReader = new QueueRevisionReader(
            new MetricAggregationRevisionChange(revision1, [shiftA], [day]),
            new MetricAggregationRevisionChange(revision2, [shiftB], []));
        var snapshotReader = new ConstantRevisionSnapshotReader();
        var source = new CoherentOperationalMetricEvaluationBatchSource(
            Catalog(),
            revisionReader,
            snapshotReader,
            processorId,
            streamId,
            OperationalMetricEvaluationContextKey.Unpartitioned);

        var first = await source.ReadAsync(
            new OperationalMetricEvaluationBatchRequest(processorId, streamId, null),
            CancellationToken.None);
        var second = await source.ReadAsync(
            new OperationalMetricEvaluationBatchRequest(processorId, streamId, revision1),
            CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(10, first.Evaluations.Count);
        Assert.Equal(5, second.Evaluations.Count);
        Assert.Contains(first.Evaluations, evaluation => evaluation.Key.PeriodId == new OperationalMetricPeriodId.Shift(shiftA));
        Assert.Contains(first.Evaluations, evaluation => evaluation.Key.PeriodId == new OperationalMetricPeriodId.ProductionDay(day));
        Assert.All(second.Evaluations, evaluation => Assert.Equal(new OperationalMetricPeriodId.Shift(shiftB), evaluation.Key.PeriodId));
    }

    [Fact]
    public async Task ChangedDefinitionSetAtSameRevisionRequiresNewProjectionProcessorIdentity()
    {
        var machineId = MachineId.New();
        var siteId = new SiteId("site-a");
        var day = new ProductionDayId(siteId, new DateOnly(2026, 8, 29));
        var streamId = MetricInputStreamId.ForMachine(machineId);
        var aggregationProcessorId = new MetricAggregationProcessorId("aggregate-definition-set");
        var revision = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            streamId,
            new MetricInputPosition(1));
        var change = new MetricAggregationRevisionChange(revision, [], [day]);
        var reader = new QueueRevisionReader(change);
        var snapshotReader = new ConstantRevisionSnapshotReader();
        var projectionStore = new InMemoryOperationalMetricProjectionStore();
        var originalCatalog = Catalog();
        var processorId = new OperationalMetricProjectionProcessorId("projection-definition-set-v1");
        var originalRuntime = new OperationalMetricProjectionProcessingRuntime(
            processorId,
            aggregationProcessorId,
            streamId,
            new CoherentOperationalMetricEvaluationBatchSource(
                originalCatalog,
                reader,
                snapshotReader,
                aggregationProcessorId,
                streamId,
                OperationalMetricEvaluationContextKey.Unpartitioned),
            new OperationalMetricProjectionFactory(originalCatalog, processorId),
            projectionStore);

        Assert.Equal(5, await originalRuntime.RunCycleAsync());

        var changedCatalog = new OperationalMetricDefinitionCatalog(
            [.. BuiltInOperationalMetricDefinitions.All, AdditionalAvailabilityDefinition()]);
        var changedRuntimeSameProcessor = new OperationalMetricProjectionProcessingRuntime(
            processorId,
            aggregationProcessorId,
            streamId,
            new CoherentOperationalMetricEvaluationBatchSource(
                changedCatalog,
                new QueueRevisionReader(change),
                snapshotReader,
                aggregationProcessorId,
                streamId,
                OperationalMetricEvaluationContextKey.Unpartitioned),
            new OperationalMetricProjectionFactory(changedCatalog, processorId),
            projectionStore);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await changedRuntimeSameProcessor.RunCycleAsync());

        var newProcessorId = new OperationalMetricProjectionProcessorId("projection-definition-set-v2");
        var changedRuntimeNewProcessor = new OperationalMetricProjectionProcessingRuntime(
            newProcessorId,
            aggregationProcessorId,
            streamId,
            new CoherentOperationalMetricEvaluationBatchSource(
                changedCatalog,
                new QueueRevisionReader(change),
                snapshotReader,
                aggregationProcessorId,
                streamId,
                OperationalMetricEvaluationContextKey.Unpartitioned),
            new OperationalMetricProjectionFactory(changedCatalog, newProcessorId),
            projectionStore);

        Assert.Equal(6, await changedRuntimeNewProcessor.RunCycleAsync());
    }

    private static OperationalMetricDefinition AdditionalAvailabilityDefinition() => new()
    {
        Id = new OperationalMetricDefinitionId("availability.secondary", "1.0"),
        DisplayName = "Secondary Availability",
        SupportedScopes = new OperationalMetricScopeSet
        {
            SupportsShift = true,
            SupportsProductionDay = true,
        },
        Operands =
        [
            new OperationalMetricOperandDefinition
            {
                OperandName = "ActualProductionTime",
                Source = new OperationalMetricOperandSource.Component(MetricInputKeys.ActualProductionTime),
                RequiredDimension = MetricDimension.Duration,
                RequiredUnit = MetricInputFactUnits.Seconds,
            },
            new OperationalMetricOperandDefinition
            {
                OperandName = "PlannedOperatingTime",
                Source = new OperationalMetricOperandSource.Component(MetricInputKeys.PlannedOperatingTime),
                RequiredDimension = MetricDimension.Duration,
                RequiredUnit = MetricInputFactUnits.Seconds,
            },
        ],
        ResultUnit = OperationalMetricUnits.Ratio,
        Formula = new OperationalMetricFormula.Ratio("ActualProductionTime", "PlannedOperatingTime"),
        DomainConstraints = new OperationalMetricDomainConstraints
        {
            MinimumInclusive = 0m,
            MaximumInclusive = 1m,
        },
        PrecisionPolicy = new OperationalMetricPrecisionPolicy
        {
            DurableDecimalScale = 8,
            RoundingMode = MidpointRounding.ToEven,
        },
    };

    private static OperationalMetricEvaluation Find(
        OperationalMetricEvaluationBatch batch,
        OperationalMetricPeriodId periodId,
        OperationalMetricDefinitionId definitionId) =>
        Assert.Single(
            batch.Evaluations,
            evaluation =>
                evaluation.Key.PeriodId == periodId &&
                evaluation.Key.DefinitionId == definitionId);

    private static OperationalMetricDefinitionCatalog Catalog() =>
        new(BuiltInOperationalMetricDefinitions.All);

    private static RuntimeFailureFixture SourceFailureFixture(
        Exception? revisionReaderFailure = null,
        Exception? snapshotFailure = null,
        bool returnWrongRevision = false)
    {
        var machineId = MachineId.New();
        var streamId = MetricInputStreamId.ForMachine(machineId);
        var aggregationProcessorId = new MetricAggregationProcessorId("aggregate-failure");
        var projectionProcessorId = new OperationalMetricProjectionProcessorId("projection-failure");
        var revision = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            streamId,
            new MetricInputPosition(1));
        var day = new ProductionDayId(new SiteId("site-a"), new DateOnly(2026, 8, 29));
        var revisionReader = new ThrowingRevisionReader(
            new MetricAggregationRevisionChange(revision, [], [day]),
            revisionReaderFailure);
        var snapshotReader = new ConfigurableSnapshotReader(snapshotFailure, returnWrongRevision);
        var catalog = Catalog();
        var source = new CoherentOperationalMetricEvaluationBatchSource(
            catalog,
            revisionReader,
            snapshotReader,
            aggregationProcessorId,
            streamId,
            OperationalMetricEvaluationContextKey.Unpartitioned);

        return new RuntimeFailureFixture(
            aggregationProcessorId,
            projectionProcessorId,
            streamId,
            revisionReader,
            source,
            catalog);
    }

    private static OperationalMetricProjectionProcessingRuntime Runtime(
        RuntimeFailureFixture fixture,
        InMemoryOperationalMetricProjectionStore projectionStore) => new(
            fixture.ProjectionProcessorId,
            fixture.AggregationProcessorId,
            fixture.StreamId,
            fixture.Source,
            new OperationalMetricProjectionFactory(fixture.Catalog, fixture.ProjectionProcessorId),
            projectionStore);

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
                Id = new MetricInputFactId($"boundary-fact-{position}"),
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

    private sealed class ThrowingRevisionReader : IMetricAggregationRevisionReader
    {
        private readonly MetricAggregationRevisionChange _change;
        private readonly Exception? _failure;

        public ThrowingRevisionReader(MetricAggregationRevisionChange change, Exception? failure)
        {
            _change = change;
            _failure = failure;
        }

        public int ReadCount { get; private set; }

        public ValueTask<MetricAggregationRevisionChange?> ReadNextAsync(
            MetricAggregationProcessorId processorId,
            MetricInputStreamId streamId,
            MetricAggregationCheckpoint? afterRevision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            if (_failure is not null)
            {
                throw _failure;
            }

            return ValueTask.FromResult<MetricAggregationRevisionChange?>(_change);
        }

        public ValueTask<MetricAggregationRevisionChange?> ReadExactAsync(
            MetricAggregationCheckpoint revision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<MetricAggregationRevisionChange?>(_change);
        }
    }

    private sealed class QueueRevisionReader : IMetricAggregationRevisionReader
    {
        private readonly MetricAggregationRevisionChange[] _changes;

        public QueueRevisionReader(params MetricAggregationRevisionChange[] changes)
        {
            _changes = changes;
        }

        public ValueTask<MetricAggregationRevisionChange?> ReadNextAsync(
            MetricAggregationProcessorId processorId,
            MetricInputStreamId streamId,
            MetricAggregationCheckpoint? afterRevision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = _changes
                .Where(change =>
                    change.Revision.ProcessorId == processorId &&
                    change.Revision.StreamId == streamId &&
                    (afterRevision is null || change.Revision.Position > afterRevision.Position))
                .OrderBy(change => change.Revision.Position.Value)
                .FirstOrDefault();
            return ValueTask.FromResult<MetricAggregationRevisionChange?>(next);
        }

        public ValueTask<MetricAggregationRevisionChange?> ReadExactAsync(
            MetricAggregationCheckpoint revision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exact = _changes.FirstOrDefault(change => change.Revision == revision);
            return ValueTask.FromResult<MetricAggregationRevisionChange?>(exact);
        }
    }

    private sealed class ConstantRevisionSnapshotReader : IRevisionedOperationalMetricComponentSnapshotReader
    {
        public ValueTask<OperationalMetricComponentSnapshot> ReadAtRevisionAsync(
            OperationalMetricComponentSnapshotRequest request,
            MetricAggregationCheckpoint requiredRevision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CreateSnapshot(request, requiredRevision));
        }
    }

    private sealed class ConfigurableSnapshotReader : IRevisionedOperationalMetricComponentSnapshotReader
    {
        private readonly Exception? _failure;
        private readonly bool _returnWrongRevision;

        public ConfigurableSnapshotReader(Exception? failure, bool returnWrongRevision)
        {
            _failure = failure;
            _returnWrongRevision = returnWrongRevision;
        }

        public ValueTask<OperationalMetricComponentSnapshot> ReadAtRevisionAsync(
            OperationalMetricComponentSnapshotRequest request,
            MetricAggregationCheckpoint requiredRevision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_failure is not null)
            {
                throw _failure;
            }

            var revision = _returnWrongRevision
                ? new MetricAggregationCheckpoint(
                    requiredRevision.ProcessorId,
                    requiredRevision.StreamId,
                    new MetricInputPosition(requiredRevision.Position.Value + 1))
                : requiredRevision;
            return ValueTask.FromResult(CreateSnapshot(request, revision));
        }
    }

    private static OperationalMetricComponentSnapshot CreateSnapshot(
        OperationalMetricComponentSnapshotRequest request,
        MetricAggregationCheckpoint revision)
    {
        var aggregates = new Dictionary<string, MetricAggregateValue>(StringComparer.Ordinal)
        {
            [MetricInputKeys.ActualProductionTime] = Aggregate(50m, MetricInputFactUnits.Seconds),
            [MetricInputKeys.PlannedOperatingTime] = Aggregate(100m, MetricInputFactUnits.Seconds),
            [MetricInputKeys.ProductionReferenceTime] = Aggregate(50m, MetricInputFactUnits.Seconds),
            [MetricInputKeys.ProducedQuantity] = Aggregate(10m, MetricInputFactUnits.Count),
            [MetricInputKeys.GoodQuantity] = Aggregate(10m, MetricInputFactUnits.Count),
            [MetricInputKeys.MachinePowerOnTime] = Aggregate(100m, MetricInputFactUnits.Seconds),
        };
        var components = request.Operands
            .Select(operand => (Operand: operand, Source: (OperationalMetricOperandSource.Component)operand.Source))
            .Where(item => aggregates.ContainsKey(item.Source.ComponentKey))
            .Select(item => new OperationalMetricComponent(
                item.Operand.OperandName,
                new OperationalMetricAggregateSourceIdentity(
                    request.ProcessorId,
                    request.EvaluationKey.MachineId,
                    request.EvaluationKey.PeriodId,
                    item.Source.ComponentKey),
                item.Operand.RequiredDimension,
                aggregates[item.Source.ComponentKey]))
            .ToArray();
        return new OperationalMetricComponentSnapshot(request.EvaluationKey, revision, components);
    }

    private static MetricAggregateValue Aggregate(decimal value, string unit) =>
        new(
            value,
            unit,
            1,
            DayStartsAt,
            DayStartsAt.AddMinutes(1));

    private sealed record RuntimeFailureFixture(
        MetricAggregationProcessorId AggregationProcessorId,
        OperationalMetricProjectionProcessorId ProjectionProcessorId,
        MetricInputStreamId StreamId,
        ThrowingRevisionReader RevisionReader,
        CoherentOperationalMetricEvaluationBatchSource Source,
        OperationalMetricDefinitionCatalog Catalog);
}
