using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricEvaluatorTests
{
    private static readonly DateTimeOffset FirstTimestamp = new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastTimestamp = new(2026, 8, 28, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AvailabilityUsesOneCoherentProductionDaySnapshot()
    {
        var fixture = CreateFixture();
        fixture.Reader.Set(MetricInputKeys.ActualProductionTime, Aggregate(450m, MetricInputFactUnits.Seconds));
        fixture.Reader.Set(MetricInputKeys.PlannedOperatingTime, Aggregate(600m, MetricInputFactUnits.Seconds));

        var result = await fixture.Evaluator.EvaluateAsync(
            Key(fixture, BuiltInOperationalMetricDefinitions.AvailabilityId),
            CancellationToken.None);

        Assert.Equal(OperationalMetricEvaluationStatus.Calculated, result.Status);
        Assert.Equal(0.75m, result.Value);
        Assert.Equal(fixture.Revision, result.SourceRevision);
        Assert.Equal(1, fixture.Reader.ReadCount);
        Assert.All(result.OperandEvidence, evidence => Assert.Equal(fixture.Revision, evidence.SourceRevision));
        Assert.Equal(MetricInputKeys.ActualProductionTime, result.OperandEvidence[0].SourceIdentity.ComponentKey);
        Assert.Equal(MetricInputKeys.PlannedOperatingTime, result.OperandEvidence[1].SourceIdentity.ComponentKey);
    }

    [Fact]
    public async Task ShiftScopeRequestsShiftPeriodComponents()
    {
        var fixture = CreateFixture();
        fixture.Reader.Set(MetricInputKeys.ActualProductionTime, Aggregate(30m, MetricInputFactUnits.Seconds));
        fixture.Reader.Set(MetricInputKeys.PlannedOperatingTime, Aggregate(60m, MetricInputFactUnits.Seconds));
        var occurrence = new ShiftOccurrenceId(
            new SiteId("site-a"),
            new ShiftScheduleAssignmentId("schedule-a"),
            new ShiftId("shift-a"),
            new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 28, 8, 0, 0, TimeSpan.Zero));
        var evaluationKey = new OperationalMetricEvaluationKey(
            fixture.MachineId,
            new OperationalMetricPeriodId.Shift(occurrence),
            BuiltInOperationalMetricDefinitions.AvailabilityId,
            OperationalMetricEvaluationContextKey.Unpartitioned);

        var result = await fixture.Evaluator.EvaluateAsync(evaluationKey, CancellationToken.None);

        Assert.Equal(0.5m, result.Value);
        var request = Assert.IsType<OperationalMetricComponentSnapshotRequest>(fixture.Reader.LastRequest);
        var period = Assert.IsType<OperationalMetricPeriodId.Shift>(request.EvaluationKey.PeriodId);
        Assert.Equal(occurrence, period.ShiftOccurrenceId);
    }

    [Fact]
    public async Task PreCancelledTokenPropagatesWithoutEvaluation()
    {
        var fixture = CreateFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Evaluator.EvaluateAsync(
                Key(fixture, BuiltInOperationalMetricDefinitions.AvailabilityId),
                cancellation.Token));

        Assert.Equal(0, fixture.Reader.ReadCount);
    }

    [Fact]
    public async Task ZeroNumeratorWithNonzeroDenominatorCalculatesZero()
    {
        var fixture = CreateFixture();
        fixture.Reader.Set(MetricInputKeys.ActualProductionTime, Aggregate(0m, MetricInputFactUnits.Seconds));
        fixture.Reader.Set(MetricInputKeys.PlannedOperatingTime, Aggregate(60m, MetricInputFactUnits.Seconds));

        var result = await fixture.Evaluator.EvaluateAsync(
            Key(fixture, BuiltInOperationalMetricDefinitions.AvailabilityId),
            CancellationToken.None);

        Assert.Equal(OperationalMetricEvaluationStatus.Calculated, result.Status);
        Assert.Equal(0m, result.Value);
        Assert.Null(result.ReasonCode);
    }

    [Fact]
    public async Task BothOperandsMissingProducesDeterministicInsufficientEvidence()
    {
        var fixture = CreateFixture();

        var result = await fixture.Evaluator.EvaluateAsync(
            Key(fixture, BuiltInOperationalMetricDefinitions.AvailabilityId),
            CancellationToken.None);

        Assert.Equal(OperationalMetricEvaluationStatus.InsufficientEvidence, result.Status);
        Assert.Equal(OperationalMetricEvaluationReasonCode.MissingOperand, result.ReasonCode);
        Assert.Equal("ActualProductionTime", result.ReasonOperandName);
        Assert.Empty(result.OperandEvidence);
    }

    [Fact]
    public async Task PartitionedContextIsRejectedBeforeSnapshotRead()
    {
        var fixture = CreateFixture();
        var key = new OperationalMetricEvaluationKey(
            fixture.MachineId,
            new OperationalMetricPeriodId.ProductionDay(fixture.ProductionDayId),
            BuiltInOperationalMetricDefinitions.AvailabilityId,
            new OperationalMetricEvaluationContextKey
            {
                ProductionOrderId = new ProductionOrderId("order-1"),
            });

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await fixture.Evaluator.EvaluateAsync(key, CancellationToken.None));

        Assert.Equal(0, fixture.Reader.ReadCount);
    }

    [Fact]
    public async Task MissingComponentProducesInsufficientEvidenceWithSnapshotRevision()
    {
        var fixture = CreateFixture();
        fixture.Reader.Set(MetricInputKeys.ActualProductionTime, Aggregate(450m, MetricInputFactUnits.Seconds));

        var result = await fixture.Evaluator.EvaluateAsync(
            Key(fixture, BuiltInOperationalMetricDefinitions.AvailabilityId),
            CancellationToken.None);

        Assert.Equal(OperationalMetricEvaluationStatus.InsufficientEvidence, result.Status);
        Assert.Null(result.Value);
        Assert.Equal(OperationalMetricEvaluationReasonCode.MissingOperand, result.ReasonCode);
        Assert.Equal("PlannedOperatingTime", result.ReasonOperandName);
        Assert.Equal(fixture.Revision, result.SourceRevision);
        Assert.Single(result.OperandEvidence);
    }

    [Fact]
    public async Task MissingPerformanceReferenceTimeUsesStableReason()
    {
        var fixture = CreateFixture();
        fixture.Reader.Set(MetricInputKeys.ActualProductionTime, Aggregate(300m, MetricInputFactUnits.Seconds));

        var result = await fixture.Evaluator.EvaluateAsync(
            Key(fixture, BuiltInOperationalMetricDefinitions.PerformanceId),
            CancellationToken.None);

        Assert.Equal(OperationalMetricEvaluationStatus.InsufficientEvidence, result.Status);
        Assert.Equal(OperationalMetricEvaluationReasonCode.MissingReferenceTime, result.ReasonCode);
        Assert.Equal("IdealProductionDuration", result.ReasonOperandName);
    }

    [Fact]
    public async Task ZeroDenominatorProducesUnavailableWithoutDivision()
    {
        var fixture = CreateFixture();
        fixture.Reader.Set(MetricInputKeys.ActualProductionTime, Aggregate(10m, MetricInputFactUnits.Seconds));
        fixture.Reader.Set(MetricInputKeys.PlannedOperatingTime, Aggregate(0m, MetricInputFactUnits.Seconds));

        var result = await fixture.Evaluator.EvaluateAsync(
            Key(fixture, BuiltInOperationalMetricDefinitions.AvailabilityId),
            CancellationToken.None);

        Assert.Equal(OperationalMetricEvaluationStatus.Unavailable, result.Status);
        Assert.Equal(OperationalMetricEvaluationReasonCode.ZeroDenominator, result.ReasonCode);
        Assert.Equal("PlannedOperatingTime", result.ReasonOperandName);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task LogicalRatioIsNotRoundedToDurableScale()
    {
        var fixture = CreateFixture();
        fixture.Reader.Set(MetricInputKeys.ActualProductionTime, Aggregate(1m, MetricInputFactUnits.Seconds));
        fixture.Reader.Set(MetricInputKeys.PlannedOperatingTime, Aggregate(3m, MetricInputFactUnits.Seconds));

        var result = await fixture.Evaluator.EvaluateAsync(
            Key(fixture, BuiltInOperationalMetricDefinitions.AvailabilityId),
            CancellationToken.None);

        Assert.Equal(1m / 3m, result.Value);
        Assert.NotEqual(0.33333333m, result.Value);
    }

    [Fact]
    public async Task DomainViolationFailsInvalidProcessingState()
    {
        var fixture = CreateFixture();
        fixture.Reader.Set(MetricInputKeys.ActualProductionTime, Aggregate(120m, MetricInputFactUnits.Seconds));
        fixture.Reader.Set(MetricInputKeys.PlannedOperatingTime, Aggregate(100m, MetricInputFactUnits.Seconds));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Evaluator.EvaluateAsync(
                Key(fixture, BuiltInOperationalMetricDefinitions.AvailabilityId),
                CancellationToken.None));
    }

    [Fact]
    public async Task IncompatibleDurableComponentUnitFailsProcessing()
    {
        var fixture = CreateFixture();
        fixture.Reader.Set(MetricInputKeys.ActualProductionTime, Aggregate(10m, MetricInputFactUnits.Count));
        fixture.Reader.Set(MetricInputKeys.PlannedOperatingTime, Aggregate(20m, MetricInputFactUnits.Seconds));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Evaluator.EvaluateAsync(
                Key(fixture, BuiltInOperationalMetricDefinitions.AvailabilityId),
                CancellationToken.None));
    }

    private static EvaluationFixture CreateFixture()
    {
        var machineId = MachineId.New();
        var productionDayId = new ProductionDayId(new SiteId("site-a"), new DateOnly(2026, 8, 28));
        var processorId = new MetricAggregationProcessorId("fc-026-test");
        var revision = new MetricAggregationCheckpoint(
            processorId,
            MetricInputStreamId.ForMachine(machineId),
            new MetricInputPosition(42));
        var reader = new FakeOperationalMetricComponentSnapshotReader(revision);
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var evaluator = new OperationalMetricEvaluator(catalog, reader, processorId);

        return new EvaluationFixture(machineId, productionDayId, revision, reader, evaluator);
    }

    private static OperationalMetricEvaluationKey Key(
        EvaluationFixture fixture,
        OperationalMetricDefinitionId definitionId) => new(
            fixture.MachineId,
            new OperationalMetricPeriodId.ProductionDay(fixture.ProductionDayId),
            definitionId,
            OperationalMetricEvaluationContextKey.Unpartitioned);

    private static MetricAggregateValue Aggregate(decimal value, string unit) =>
        new(value, unit, 2, FirstTimestamp, LastTimestamp);

    private sealed record EvaluationFixture(
        MachineId MachineId,
        ProductionDayId ProductionDayId,
        MetricAggregationCheckpoint Revision,
        FakeOperationalMetricComponentSnapshotReader Reader,
        OperationalMetricEvaluator Evaluator);

    private sealed class FakeOperationalMetricComponentSnapshotReader : IOperationalMetricComponentSnapshotReader
    {
        private readonly MetricAggregationCheckpoint _revision;
        private readonly Dictionary<string, MetricAggregateValue> _aggregates = new(StringComparer.Ordinal);

        public FakeOperationalMetricComponentSnapshotReader(MetricAggregationCheckpoint revision)
        {
            _revision = revision;
        }

        public int ReadCount { get; private set; }

        public OperationalMetricComponentSnapshotRequest? LastRequest { get; private set; }

        public void Set(string componentKey, MetricAggregateValue value) =>
            _aggregates[componentKey] = value;

        public ValueTask<OperationalMetricComponentSnapshot> ReadAsync(
            OperationalMetricComponentSnapshotRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            LastRequest = request;

            var components = new List<OperationalMetricComponent>();
            foreach (var operand in request.Operands)
            {
                var source = Assert.IsType<OperationalMetricOperandSource.Component>(operand.Source);
                if (!_aggregates.TryGetValue(source.ComponentKey, out var aggregate))
                {
                    continue;
                }

                components.Add(new OperationalMetricComponent(
                    operand.OperandName,
                    new OperationalMetricAggregateSourceIdentity(
                        request.ProcessorId,
                        request.EvaluationKey.MachineId,
                        request.EvaluationKey.PeriodId,
                        source.ComponentKey),
                    operand.RequiredDimension,
                    aggregate));
            }

            return ValueTask.FromResult(
                new OperationalMetricComponentSnapshot(request.EvaluationKey, _revision, components));
        }
    }
}
