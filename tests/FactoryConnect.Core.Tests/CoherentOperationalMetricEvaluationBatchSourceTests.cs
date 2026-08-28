using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class CoherentOperationalMetricEvaluationBatchSourceTests
{
    private static readonly DateTimeOffset StartsAt = new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndsAt = new(2026, 8, 29, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompleteDefinitionSetUsesOneCoherentSnapshotRevision()
    {
        var fixture = CreateReaderFixture();

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

        Assert.Equal(0.5m, Find(batch, BuiltInOperationalMetricDefinitions.AvailabilityId).Value);
        Assert.Equal(0.8m, Find(batch, BuiltInOperationalMetricDefinitions.PerformanceId).Value);
        Assert.Equal(0.9m, Find(batch, BuiltInOperationalMetricDefinitions.QualityId).Value);
        Assert.Equal(0.36m, Find(batch, BuiltInOperationalMetricDefinitions.OeeId).Value);
        Assert.Equal(0.4m, Find(batch, BuiltInOperationalMetricDefinitions.UtilizationId).Value);
    }

    [Fact]
    public async Task OeeKeepsFullPrecisionAndExactDependencyLineageWithinPinnedSnapshot()
    {
        var fixture = CreateReaderFixture(
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
        var oee = Find(batch!, BuiltInOperationalMetricDefinitions.OeeId);

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
    public async Task KnownRevisionAheadOfCurrentSnapshotFails()
    {
        var fixture = CreateReaderFixture();
        var ahead = new MetricAggregationCheckpoint(
            fixture.AggregationProcessorId,
            fixture.StreamId,
            new MetricInputPosition(fixture.Revision.Position.Value + 1));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Source.ReadAsync(
                new OperationalMetricEvaluationBatchRequest(
                    fixture.AggregationProcessorId,
                    fixture.StreamId,
                    ahead),
                CancellationToken.None));
    }

    [Fact]
    public async Task InMemoryAggregationThroughProjectionRuntimePersistsCompleteMetricSetAndReplaysAfterRestart()
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

        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var source = new CoherentOperationalMetricEvaluationBatchSource(
            catalog,
            aggregationStore,
            aggregationProcessorId,
            streamId,
            new OperationalMetricPeriodId.ProductionDay(day),
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

        Assert.Equal(5, await runtime.RunCycleAsync());

        var oeeKey = Key(machineId, day, BuiltInOperationalMetricDefinitions.OeeId);
        var oee = await projectionStore.ReadProjectionAsync(
            projectionProcessorId,
            oeeKey,
            CancellationToken.None);
        Assert.NotNull(oee);
        Assert.Equal(0.36m, oee.Value);
        Assert.Equal(3, oee.DependencyEvidence.Count);
        Assert.Equal(aggregationCheckpoint, oee.SourceRevision);

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
        Assert.Equal(5, checkpoint.BatchManifest.EvaluationKeys.Count);
    }

    private static ReaderFixture CreateReaderFixture(
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
        var periodId = new OperationalMetricPeriodId.ProductionDay(
            new ProductionDayId(new SiteId("site-a"), new DateOnly(2026, 8, 29)));
        var reader = new CapturingSnapshotReader(revision, new Dictionary<string, MetricAggregateValue>(StringComparer.Ordinal)
        {
            [MetricInputKeys.ActualProductionTime] = Aggregate(actualProductionTime, MetricInputFactUnits.Seconds),
            [MetricInputKeys.PlannedOperatingTime] = Aggregate(plannedOperatingTime, MetricInputFactUnits.Seconds),
            [MetricInputKeys.ProductionReferenceTime] = Aggregate(productionReferenceTime, MetricInputFactUnits.Seconds),
            [MetricInputKeys.ProducedQuantity] = Aggregate(producedQuantity, MetricInputFactUnits.Count),
            [MetricInputKeys.GoodQuantity] = Aggregate(goodQuantity, MetricInputFactUnits.Count),
            [MetricInputKeys.MachinePowerOnTime] = Aggregate(machinePowerOnTime, MetricInputFactUnits.Seconds),
        });
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var source = new CoherentOperationalMetricEvaluationBatchSource(
            catalog,
            reader,
            aggregationProcessorId,
            streamId,
            periodId,
            OperationalMetricEvaluationContextKey.Unpartitioned);

        return new ReaderFixture(
            aggregationProcessorId,
            streamId,
            revision,
            reader,
            source);
    }

    private static MetricAggregateValue Aggregate(decimal value, string unit) =>
        new(value, unit, 1, StartsAt, EndsAt);

    private static OperationalMetricEvaluation Find(
        OperationalMetricEvaluationBatch batch,
        OperationalMetricDefinitionId definitionId) =>
        Assert.Single(batch.Evaluations.Where(evaluation => evaluation.Key.DefinitionId == definitionId));

    private static OperationalMetricEvaluationKey Key(
        MachineId machineId,
        ProductionDayId day,
        OperationalMetricDefinitionId definitionId) => new(
            machineId,
            new OperationalMetricPeriodId.ProductionDay(day),
            definitionId,
            OperationalMetricEvaluationContextKey.Unpartitioned);

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

    private sealed record ReaderFixture(
        MetricAggregationProcessorId AggregationProcessorId,
        MetricInputStreamId StreamId,
        MetricAggregationCheckpoint Revision,
        CapturingSnapshotReader Reader,
        CoherentOperationalMetricEvaluationBatchSource Source);
}
