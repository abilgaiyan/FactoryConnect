using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricEvaluatorTests
{
    private static readonly DateTimeOffset FirstTimestamp = new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastTimestamp = new(2026, 8, 28, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AvailabilityUsesProductionDayFc026Components()
    {
        var fixture = CreateFixture();
        fixture.Store.SetProductionDay(
            fixture.MachineId,
            fixture.ProductionDayId,
            MetricInputKeys.ActualProductionTime,
            Aggregate(450m, MetricInputFactUnits.Seconds));
        fixture.Store.SetProductionDay(
            fixture.MachineId,
            fixture.ProductionDayId,
            MetricInputKeys.PlannedOperatingTime,
            Aggregate(600m, MetricInputFactUnits.Seconds));

        var result = await fixture.Evaluator.EvaluateAsync(
            Key(fixture, BuiltInOperationalMetricDefinitions.AvailabilityId),
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationalMetricEvaluationStatus.Calculated, result.Status);
        Assert.Equal(0.75m, result.Value);
        Assert.Equal(OperationalMetricUnits.Ratio, result.Unit);
        Assert.Null(result.ReasonCode);
        Assert.Equal(2, result.OperandEvidence.Count);
        Assert.Equal("ActualProductionTime", result.OperandEvidence[0].OperandName);
        Assert.Equal("PlannedOperatingTime", result.OperandEvidence[1].OperandName);
    }

    [Fact]
    public async Task MissingComponentProducesInsufficientEvidence()
    {
        var fixture = CreateFixture();
        fixture.Store.SetProductionDay(
            fixture.MachineId,
            fixture.ProductionDayId,
            MetricInputKeys.ActualProductionTime,
            Aggregate(450m, MetricInputFactUnits.Seconds));

        var result = await fixture.Evaluator.EvaluateAsync(
            Key(fixture, BuiltInOperationalMetricDefinitions.AvailabilityId),
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationalMetricEvaluationStatus.InsufficientEvidence, result.Status);
        Assert.Null(result.Value);
        Assert.Equal(OperationalMetricEvaluationReasonCode.MissingOperand, result.ReasonCode);
        Assert.Equal("PlannedOperatingTime", result.ReasonOperandName);
        Assert.Single(result.OperandEvidence);
    }

    [Fact]
    public async Task MissingPerformanceReferenceTimeUsesStableReason()
    {
        var fixture = CreateFixture();
        fixture.Store.SetProductionDay(
            fixture.MachineId,
            fixture.ProductionDayId,
            MetricInputKeys.ActualProductionTime,
            Aggregate(300m, MetricInputFactUnits.Seconds));

        var result = await fixture.Evaluator.EvaluateAsync(
            Key(fixture, BuiltInOperationalMetricDefinitions.PerformanceId),
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationalMetricEvaluationStatus.InsufficientEvidence, result.Status);
        Assert.Equal(OperationalMetricEvaluationReasonCode.MissingReferenceTime, result.ReasonCode);
        Assert.Equal("IdealProductionDuration", result.ReasonOperandName);
    }

    [Fact]
    public async Task ZeroDenominatorProducesUnavailableWithoutDivision()
    {
        var fixture = CreateFixture();
        fixture.Store.SetProductionDay(
            fixture.MachineId,
            fixture.ProductionDayId,
            MetricInputKeys.ActualProductionTime,
            Aggregate(10m, MetricInputFactUnits.Seconds));
        fixture.Store.SetProductionDay(
            fixture.MachineId,
            fixture.ProductionDayId,
            MetricInputKeys.PlannedOperatingTime,
            Aggregate(0m, MetricInputFactUnits.Seconds));

        var result = await fixture.Evaluator.EvaluateAsync(
            Key(fixture, BuiltInOperationalMetricDefinitions.AvailabilityId),
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationalMetricEvaluationStatus.Unavailable, result.Status);
        Assert.Equal(OperationalMetricEvaluationReasonCode.ZeroDenominator, result.ReasonCode);
        Assert.Equal("PlannedOperatingTime", result.ReasonOperandName);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task FinalPrecisionIsAppliedOnceToRatioResult()
    {
        var fixture = CreateFixture();
        fixture.Store.SetProductionDay(
            fixture.MachineId,
            fixture.ProductionDayId,
            MetricInputKeys.ActualProductionTime,
            Aggregate(1m, MetricInputFactUnits.Seconds));
        fixture.Store.SetProductionDay(
            fixture.MachineId,
            fixture.ProductionDayId,
            MetricInputKeys.PlannedOperatingTime,
            Aggregate(3m, MetricInputFactUnits.Seconds));

        var result = await fixture.Evaluator.EvaluateAsync(
            Key(fixture, BuiltInOperationalMetricDefinitions.AvailabilityId),
            TestContext.Current.CancellationToken);

        Assert.Equal(0.33333333m, result.Value);
        Assert.Equal(1m, result.OperandEvidence[0].Value);
        Assert.Equal(3m, result.OperandEvidence[1].Value);
    }

    [Fact]
    public async Task DomainViolationIsExplicitAndNeverClamped()
    {
        var fixture = CreateFixture();
        fixture.Store.SetProductionDay(
            fixture.MachineId,
            fixture.ProductionDayId,
            MetricInputKeys.ActualProductionTime,
            Aggregate(120m, MetricInputFactUnits.Seconds));
        fixture.Store.SetProductionDay(
            fixture.MachineId,
            fixture.ProductionDayId,
            MetricInputKeys.PlannedOperatingTime,
            Aggregate(100m, MetricInputFactUnits.Seconds));

        var result = await fixture.Evaluator.EvaluateAsync(
            Key(fixture, BuiltInOperationalMetricDefinitions.AvailabilityId),
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationalMetricEvaluationStatus.Unavailable, result.Status);
        Assert.Equal(OperationalMetricEvaluationReasonCode.DomainViolation, result.ReasonCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task IncompatibleDurableComponentUnitFailsProcessing()
    {
        var fixture = CreateFixture();
        fixture.Store.SetProductionDay(
            fixture.MachineId,
            fixture.ProductionDayId,
            MetricInputKeys.ActualProductionTime,
            Aggregate(10m, MetricInputFactUnits.Count));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Evaluator.EvaluateAsync(
                Key(fixture, BuiltInOperationalMetricDefinitions.AvailabilityId),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProductEvaluationRemainsOutOfFc0272()
    {
        var fixture = CreateFixture();

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await fixture.Evaluator.EvaluateAsync(
                Key(fixture, BuiltInOperationalMetricDefinitions.OeeId),
                TestContext.Current.CancellationToken));
    }

    private static EvaluationFixture CreateFixture()
    {
        var machineId = MachineId.New();
        var productionDayId = new ProductionDayId(new SiteId("site-a"), new DateOnly(2026, 8, 28));
        var store = new FakeMetricAggregationStore();
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var evaluator = new OperationalMetricEvaluator(
            catalog,
            store,
            new MetricAggregationProcessorId("fc-026-test"));

        return new EvaluationFixture(machineId, productionDayId, store, evaluator);
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
        FakeMetricAggregationStore Store,
        OperationalMetricEvaluator Evaluator);

    private sealed class FakeMetricAggregationStore : IMetricAggregationStore
    {
        private readonly Dictionary<ProductionDayMetricAggregateKey, MetricAggregateValue> _productionDay = [];

        public void SetProductionDay(
            MachineId machineId,
            ProductionDayId productionDayId,
            string metricInputKey,
            MetricAggregateValue value) =>
            _productionDay[new ProductionDayMetricAggregateKey(machineId, productionDayId, metricInputKey)] = value;

        public ValueTask<MetricAggregationCheckpoint?> ReadCheckpointAsync(
            MetricAggregationProcessorId processorId,
            MetricInputStreamId streamId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<MetricAggregationCheckpoint?>(null);

        public ValueTask<MetricAggregateValue?> ReadShiftAggregateAsync(
            MetricAggregationProcessorId processorId,
            ShiftMetricAggregateKey key,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<MetricAggregateValue?>(null);

        public ValueTask<MetricAggregateValue?> ReadProductionDayAggregateAsync(
            MetricAggregationProcessorId processorId,
            ProductionDayMetricAggregateKey key,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(_productionDay.TryGetValue(key, out var value) ? value : null);

        public ValueTask CommitAsync(
            MetricAggregationCommit commit,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
