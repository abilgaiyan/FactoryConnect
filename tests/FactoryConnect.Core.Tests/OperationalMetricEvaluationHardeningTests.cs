using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricEvaluationHardeningTests
{
    private static readonly DateTimeOffset FirstTimestamp = new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastTimestamp = FirstTimestamp.AddMinutes(1);

    [Fact]
    public void SnapshotRejectsRevisionFromAnotherMachineStream()
    {
        var machine = MachineId.New();
        var otherMachine = MachineId.New();
        var processor = new MetricAggregationProcessorId("aggregate-test");
        var key = EvaluationKey(machine);
        var revision = new MetricAggregationCheckpoint(
            processor,
            MetricInputStreamId.ForMachine(otherMachine),
            new MetricInputPosition(1));

        Assert.Throws<ArgumentException>(() =>
            new OperationalMetricComponentSnapshot(key, revision, []));
    }

    [Fact]
    public void SnapshotPermitsDifferentStreamKeyForSameMachine()
    {
        var machine = MachineId.New();
        var processor = new MetricAggregationProcessorId("aggregate-test");
        var key = EvaluationKey(machine);
        var revision = new MetricAggregationCheckpoint(
            processor,
            new MetricInputStreamId(machine, "alternate-metric-inputs"),
            new MetricInputPosition(1));

        var snapshot = new OperationalMetricComponentSnapshot(key, revision, []);

        Assert.Equal(revision, snapshot.Revision);
    }

    [Fact]
    public async Task EvaluatorRejectsSnapshotFromWrongProcessor()
    {
        var machine = MachineId.New();
        var requestedProcessor = new MetricAggregationProcessorId("aggregate-a");
        var returnedProcessor = new MetricAggregationProcessorId("aggregate-b");
        var key = EvaluationKey(machine);
        var snapshot = new OperationalMetricComponentSnapshot(
            key,
            Revision(returnedProcessor, machine),
            []);
        var evaluator = CreateEvaluator(requestedProcessor, snapshot);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await evaluator.EvaluateAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task EvaluatorRejectsSnapshotForWrongEvaluationKey()
    {
        var machine = MachineId.New();
        var otherMachine = MachineId.New();
        var processor = new MetricAggregationProcessorId("aggregate-test");
        var requestedKey = EvaluationKey(machine);
        var returnedKey = EvaluationKey(otherMachine);
        var snapshot = new OperationalMetricComponentSnapshot(
            returnedKey,
            Revision(processor, otherMachine),
            []);
        var evaluator = CreateEvaluator(processor, snapshot);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await evaluator.EvaluateAsync(requestedKey, CancellationToken.None));
    }

    [Fact]
    public async Task MissingNumeratorCannotHideInvalidDenominatorUnit()
    {
        var machine = MachineId.New();
        var processor = new MetricAggregationProcessorId("aggregate-test");
        var key = EvaluationKey(machine);
        var revision = Revision(processor, machine);
        var denominator = Component(
            key,
            processor,
            "PlannedOperatingTime",
            MetricInputKeys.PlannedOperatingTime,
            MetricDimension.Duration,
            100m,
            MetricInputFactUnits.Count);
        var evaluator = CreateEvaluator(
            processor,
            new OperationalMetricComponentSnapshot(key, revision, [denominator]));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await evaluator.EvaluateAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task MissingDenominatorCannotHideInvalidNumeratorSource()
    {
        var machine = MachineId.New();
        var processor = new MetricAggregationProcessorId("aggregate-test");
        var key = EvaluationKey(machine);
        var revision = Revision(processor, machine);
        var numerator = Component(
            key,
            processor,
            "ActualProductionTime",
            "wrong-component-key",
            MetricDimension.Duration,
            50m,
            MetricInputFactUnits.Seconds);
        var evaluator = CreateEvaluator(
            processor,
            new OperationalMetricComponentSnapshot(key, revision, [numerator]));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await evaluator.EvaluateAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task UnexpectedSnapshotOperandFailsProcessing()
    {
        var machine = MachineId.New();
        var processor = new MetricAggregationProcessorId("aggregate-test");
        var key = EvaluationKey(machine);
        var revision = Revision(processor, machine);
        var unexpected = Component(
            key,
            processor,
            "UnexpectedOperand",
            MetricInputKeys.ActualProductionTime,
            MetricDimension.Duration,
            50m,
            MetricInputFactUnits.Seconds);
        var evaluator = CreateEvaluator(
            processor,
            new OperationalMetricComponentSnapshot(key, revision, [unexpected]));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await evaluator.EvaluateAsync(key, CancellationToken.None));
    }

    private static OperationalMetricEvaluator CreateEvaluator(
        MetricAggregationProcessorId processor,
        OperationalMetricComponentSnapshot snapshot) =>
        new(
            new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All),
            new FixedSnapshotReader(snapshot),
            processor);

    private static OperationalMetricEvaluationKey EvaluationKey(MachineId machine) =>
        new(
            machine,
            new OperationalMetricPeriodId.ProductionDay(
                new ProductionDayId(new SiteId("site-a"), new DateOnly(2026, 8, 28))),
            BuiltInOperationalMetricDefinitions.AvailabilityId,
            OperationalMetricEvaluationContextKey.Unpartitioned);

    private static MetricAggregationCheckpoint Revision(
        MetricAggregationProcessorId processor,
        MachineId machine) =>
        new(
            processor,
            MetricInputStreamId.ForMachine(machine),
            new MetricInputPosition(1));

    private static OperationalMetricComponent Component(
        OperationalMetricEvaluationKey key,
        MetricAggregationProcessorId processor,
        string operandName,
        string componentKey,
        MetricDimension dimension,
        decimal value,
        string unit) =>
        new(
            operandName,
            new OperationalMetricAggregateSourceIdentity(
                processor,
                key.MachineId,
                key.PeriodId,
                componentKey),
            dimension,
            new MetricAggregateValue(
                value,
                unit,
                1,
                FirstTimestamp,
                LastTimestamp));

    private sealed class FixedSnapshotReader : IOperationalMetricComponentSnapshotReader
    {
        private readonly OperationalMetricComponentSnapshot _snapshot;

        public FixedSnapshotReader(OperationalMetricComponentSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public ValueTask<OperationalMetricComponentSnapshot> ReadAsync(
            OperationalMetricComponentSnapshotRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_snapshot);
        }
    }
}
