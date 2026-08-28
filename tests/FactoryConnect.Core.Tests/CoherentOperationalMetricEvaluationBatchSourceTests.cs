using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class CoherentOperationalMetricEvaluationBatchSourceTests
{
    private static readonly DateTimeOffset StartsAt = new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndsAt = new(2026, 8, 29, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProductionDayChangeEvaluatesCompleteDefinitionSetFromOneExactSnapshot()
    {
        var fixture = CreateReaderFixture(includeShift: false);

        var batch = await fixture.Source.ReadAsync(
            new OperationalMetricEvaluationBatchRequest(
                fixture.AggregationProcessorId,
                fixture.StreamId,
                null),
            CancellationToken.None);

        Assert.NotNull(batch);
        Assert.Equal(1, fixture.Reader.ReadCount);
        Assert.Equal(fixture.Revision, batch.SourceRevision);
        Assert.Equal(5, batch.Evaluations.Count);
        Assert.All(batch.Evaluations, evaluation => Assert.Equal(fixture.Revision, evaluation.SourceRevision));

        Assert.Equal(0.5m, Find(batch, fixture.DayPeriod, BuiltInOperationalMetricDefinitions.AvailabilityId).Value);
        Assert.Equal(0.8m, Find(batch, fixture.DayPeriod, BuiltInOperationalMetricDefinitions.PerformanceId).Value);
        Assert.Equal(0.9m, Find(batch, fixture.DayPeriod, BuiltInOperationalMetricDefinitions.QualityId).Value);
        Assert.Equal(0.36m, Find(batch, fixture.DayPeriod, BuiltInOperationalMetricDefinitions.OeeId).Value);
        Assert.Equal(0.4m, Find(batch, fixture.DayPeriod, BuiltInOperationalMetricDefinitions.UtilizationId).Value);
    }

    [Fact]
    public async Task OneRevisionMayEvaluateShiftAndProductionDayWithoutRevisionDrift()
    {
        var fixture = CreateReaderFixture(includeShift: true);

        var batch = await fixture.Source.ReadAsync(
            new OperationalMetricEvaluationBatchRequest(
                fixture.AggregationProcessorId,
                fixture.StreamId,
                null),
            CancellationToken.None);

        Assert.NotNull(batch);
        Assert.Equal(2, fixture.Reader.ReadCount);
        Assert.Equal(10, batch.Evaluations.Count);
        Assert.Equal(5, batch.Evaluations.Count(evaluation => evaluation.Key.Scope == OperationalMetricEvaluationScope.Shift));
        Assert.Equal(5, batch.Evaluations.Count(evaluation => evaluation.Key.Scope == OperationalMetricEvaluationScope.ProductionDay));
        Assert.All(batch.Evaluations, evaluation => Assert.Equal(fixture.Revision, evaluation.SourceRevision));
    }

    [Fact]
    public async Task OeeKeepsFullPrecisionAndExactDependencyLineageWithinPinnedRevision()
    {
        var fixture = CreateReaderFixture(
            includeShift: false,
            actualProductionTime: 1m,
            plannedOperatingTime: 3m,
            productionReferenceTime: 1m,
            producedQuantity: 3m,
            goodQuantity: 1m,
            machinePowerOnTime: 4m);

        var batch = await fixture.Source.ReadAsync(
            new OperationalMetricEvaluationBatchRequest(
                fixture.AggregationProcessorId,
                fixture.StreamId,
                null),
            CancellationToken.None);
        var oee = Find(
            batch!,
            fixture.DayPeriod,
            BuiltInOperationalMetricDefinitions.OeeId);

        var expected = (1m / 3m) * 1m * (1m / 3m);
        Assert.Equal(expected, oee.Value);
        Assert.NotEqual(decimal.Round(expected, 8, MidpointRounding.ToEven), oee.Value);
        Assert.Collection(
            oee.DependencyEvidence,
            availability => Assert.Equal(BuiltInOperationalMetricDefinitions.AvailabilityId, availability.DefinitionId),
            performance => Assert.Equal(BuiltInOperationalMetricDefinitions.PerformanceId, performance.DefinitionId),
            quality => Assert.Equal(BuiltInOperationalMetricDefinitions.QualityId, quality.DefinitionId));
        Assert.All(oee.DependencyEvidence, evidence =>
            Assert.Equal(fixture.Revision, evidence.Evaluation.SourceRevision));
    }

    [Fact]
    public async Task InvalidEvaluationFailsBeforeProjectionCheckpointAdvances()
    {
        var fixture = CreateReaderFixture(
            includeShift: false,
            actualProductionTime: 700m,
            plannedOperatingTime: 600m);
        var projectionProcessorId = new OperationalMetricProjectionProcessorId("projection-m01");
        var projectionStore = new InMemoryOperationalMetricProjectionStore();
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var factory = new OperationalMetricProjectionFactory(catalog, projectionProcessorId);
        var runtime = new OperationalMetricProjectionProcessingRuntime(
            projectionProcessorId,
            fixture.AggregationProcessorId,
            fixture.StreamId,
            fixture.Source,
            factory,
            projectionStore);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await runtime.RunCycleAsync());

        Assert.Null(await projectionStore.ReadCheckpointAsync(
            projectionProcessorId,
            fixture.StreamId,
            CancellationToken.None));
    }

    [Fact]
    public async Task InMemoryAggregationThroughProjectionRuntimePersistsBothScopesAndReplaysAfterRestart()
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
        var aggregationCheckpoint = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            streamId,
            new MetricInputPosition(6));
        var inputs = new[]
        {
            Input(streamId, 1, machineId, siteId, shiftId, assignmentId, occurrence, day, MetricInputKeys.ActualProductionTime, 300m, MetricInputFactUnits.Seconds),
            Input(streamId, 2, machineId, siteId, shiftId, assignmentId, occurrence, day, MetricInputKeys.PlannedOperatingTime, 600m, MetricInputFactUnits.Seconds),
            Input(streamId, 3, machineId, siteId, shiftId, assignmentId, occurrence, day, MetricInputKeys.ProductionReferenceTime, 240m, MetricInputFactUnits.Seconds),
            Input(streamId, 4, machineId, siteId, shiftId, assignmentId, occurrence, day, MetricInputKeys.ProducedQuantity, 100m, MetricInputFactUnits.Count),
            Input(streamId, 5, machineId, siteId, shiftId, assignmentId, occurrence, day, MetricInputKeys.GoodQuantity, 90m, MetricInputFactUnits.Count),
            Input(streamId, 6, machineId, siteId, shiftId, assignmentId, occurrence, day, MetricInputKeys.MachinePowerOnTime, 750m, MetricInputFactUnits.Seconds),
        };
        await aggregationStore.CommitAsync(
            new MetricAggregationCommit(
                aggregationProcessorId,
                null,
                aggregationCheckpoint,
                inputs),
            CancellationToken.None);

        var change = new MetricAggregationRevisionChange(
            aggregationCheckpoint,
            [occurrence],
            [day]);
        var adapter = new SingleRevisionAggregationAdapter(aggregationStore, change);
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var source = new CoherentOperationalMetricEvaluationBatchSource(
            catalog,
            adapter,
            adapter,
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

        Assert.Equal(10, await runtime.RunCycleAsync());

        var dayOee = await projectionStore.ReadProjectionAsync(
            projectionProcessorId,
            new OperationalMetricEvaluationKey(
                machineId,
                new OperationalMetricPeriodId.ProductionDay(day),
                BuiltInOperationalMetricDefinitions.OeeId,
                OperationalMetricEvaluationContextKey.Unpartitioned),
            CancellationToken.None);
        var shiftOee = await projectionStore.ReadProjectionAsync(
            projectionProcessorId,
            new OperationalMetricEvaluationKey(
                machineId,
                new OperationalMetricPeriodId.Shift(occurrence),
                BuiltInOperationalMetricDefinitions.OeeId,
                OperationalMetricEvaluationContextKey.Unpartitioned),
            CancellationToken.None);
        Assert.NotNull(dayOee);
        Assert.NotNull(shiftOee);
        Assert.Equal(0.36m, dayOee.Value);
        Assert.Equal(0.36m, shiftOee.Value);
        Assert.Equal(3, dayOee.DependencyEvidence.Count);
        Assert.Equal(aggregationCheckpoint, dayOee.SourceRevision);

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
        Assert.Equal(aggregationCheckpoint, checkpoint.SourceRevision);
        Assert.Equal(10, checkpoint.BatchManifest.ProjectionKeys.Count);
    }

    private static ReaderFixture CreateReaderFixture(
        bool includeShift,
        decimal actualProductionTime = 300m,
        decimal plannedOperatingTime = 600m,
        decimal productionReferenceTime = 240m,
        decimal producedQuantity = 100m,
        decimal goodQuantity = 90m,
        decimal machinePowerOnTime = 750m)
    {
        var machineId = MachineId.New();
        var streamId = MetricInputStreamId.ForMachine(machineId);
        var aggregationProcessorId = new MetricAggregationProcessorId("aggregate-m01");
        var revision = new MetricAggregationCheckpoint(
            aggregationProcessorId,
            streamId,
            new MetricInputPosition(42));
        var siteId = new SiteId("site-a");
        var day = new ProductionDayId(siteId, new DateOnly(2026, 8, 29));
        var occurrence = new ShiftOccurrenceId(
            siteId,
            new ShiftScheduleAssignmentId("schedule-a"),
            new ShiftId("shift-a"),
            StartsAt,
            new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero));
        var dayPeriod = new OperationalMetricPeriodId.ProductionDay(day);
        var reader = new CapturingSnapshotReader(revision, new Dictionary<string, MetricAggregateValue>(StringComparer.Ordinal)
        {
            [MetricInputKeys.ActualProductionTime] = Aggregate(actualProductionTime, MetricInputFactUnits.Seconds),
            [MetricInputKeys.PlannedOperatingTime] = Aggregate(plannedOperatingTime, MetricInputFactUnits.Seconds),
            [MetricInputKeys.ProductionReferenceTime] = Aggregate(productionReferenceTime, MetricInputFactUnits.Seconds),
            [MetricInputKeys.ProducedQuantity] = Aggregate(producedQuantity, MetricInputFactUnits.Count),
            [MetricInputKeys.GoodQuantity] = Aggregate(goodQuantity, MetricInputFactUnits.Count),
            [MetricInputKeys.MachinePowerOnTime] = Aggregate(machinePowerOnTime, MetricInputFactUnits.Seconds),
        });
        var change = new MetricAggregationRevisionChange(
            revision,
            includeShift ? [occurrence] : [],
            [day]);
        var adapter = new SingleRevisionAggregationAdapter(reader, change);
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var source = new CoherentOperationalMetricEvaluationBatchSource(
            catalog,
            adapter,
            adapter,
            aggregationProcessorId,
            streamId,
            OperationalMetricEvaluationContextKey.Unpartitioned);

        return new ReaderFixture(
            aggregationProcessorId,
            streamId,
            revision,
            dayPeriod,
            reader,
            source);
    }

    private static MetricAggregateValue Aggregate(decimal value, string unit) =>
        new(value, unit, 1, StartsAt, EndsAt);

    private static OperationalMetricEvaluation Find(
        OperationalMetricEvaluationBatch batch,
        OperationalMetricPeriodId periodId,
        OperationalMetricDefinitionId definitionId) =>
        Assert.Single(
            batch.Evaluations,
            evaluation =>
                evaluation.Key.PeriodId == periodId &&
                evaluation.Key.DefinitionId == definitionId);

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

    private sealed class CapturingSnapshotReader : IOperationalMetricComponentSnapshotReader
    {
        private readonly MetricAggregationCheckpoint _revision;
        private readonly IReadOnlyDictionary<string, MetricAggregateValue> _aggregates;

        public CapturingSnapshotReader(
            MetricAggregationCheckpoint revision,
            IReadOnlyDictionary<string, MetricAggregateValue> aggregates)
        {
            _revision = revision;
            _aggregates = aggregates;
        }

        public int ReadCount { get; private set; }

        public ValueTask<OperationalMetricComponentSnapshot> ReadAsync(
            OperationalMetricComponentSnapshotRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            var components = request.Operands
                .Select(operand => (Operand: operand, Source: (OperationalMetricOperandSource.Component)operand.Source))
                .Where(item => _aggregates.ContainsKey(item.Source.ComponentKey))
                .Select(item => new OperationalMetricComponent(
                    item.Operand.OperandName,
                    new OperationalMetricAggregateSourceIdentity(
                        request.ProcessorId,
                        request.EvaluationKey.MachineId,
                        request.EvaluationKey.PeriodId,
                        item.Source.ComponentKey),
                    item.Operand.RequiredDimension,
                    _aggregates[item.Source.ComponentKey]))
                .ToArray();
            return ValueTask.FromResult(new OperationalMetricComponentSnapshot(
                request.EvaluationKey,
                _revision,
                components));
        }
    }

    private sealed class SingleRevisionAggregationAdapter :
        IMetricAggregationRevisionReader,
        IRevisionedOperationalMetricComponentSnapshotReader
    {
        private readonly IOperationalMetricComponentSnapshotReader _snapshotReader;
        private readonly MetricAggregationRevisionChange _change;

        public SingleRevisionAggregationAdapter(
            IOperationalMetricComponentSnapshotReader snapshotReader,
            MetricAggregationRevisionChange change)
        {
            _snapshotReader = snapshotReader;
            _change = change;
        }

        public ValueTask<MetricAggregationRevisionChange?> ReadNextAsync(
            MetricAggregationProcessorId processorId,
            MetricInputStreamId streamId,
            MetricAggregationCheckpoint? afterRevision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_change.Revision.ProcessorId != processorId ||
                _change.Revision.StreamId != streamId)
            {
                return ValueTask.FromResult<MetricAggregationRevisionChange?>(null);
            }

            return ValueTask.FromResult<MetricAggregationRevisionChange?>(
                afterRevision is null || _change.Revision.Position > afterRevision.Position
                    ? _change
                    : null);
        }

        public ValueTask<MetricAggregationRevisionChange?> ReadExactAsync(
            MetricAggregationCheckpoint revision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<MetricAggregationRevisionChange?>(
                _change.Revision == revision ? _change : null);
        }

        public async ValueTask<OperationalMetricComponentSnapshot> ReadAtRevisionAsync(
            OperationalMetricComponentSnapshotRequest request,
            MetricAggregationCheckpoint requiredRevision,
            CancellationToken cancellationToken)
        {
            if (requiredRevision != _change.Revision)
            {
                throw new InvalidDataException("Requested component revision is not available.");
            }

            var snapshot = await _snapshotReader.ReadAsync(request, cancellationToken);
            if (snapshot.Revision != requiredRevision)
            {
                throw new InvalidDataException("Component snapshot did not preserve the required revision.");
            }

            return snapshot;
        }
    }

    private sealed record ReaderFixture(
        MetricAggregationProcessorId AggregationProcessorId,
        MetricInputStreamId StreamId,
        MetricAggregationCheckpoint Revision,
        OperationalMetricPeriodId DayPeriod,
        CapturingSnapshotReader Reader,
        CoherentOperationalMetricEvaluationBatchSource Source);
}
