using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricEvaluationSessionRatioTests
{
    private static readonly DateTimeOffset FirstTimestamp = new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastTimestamp = new(2026, 8, 28, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RepeatedDefinitionEvaluationReturnsMemoizedInstance()
    {
        var definition = AvailabilityDefinition();
        var session = CreateSession(definition, 30m, 60m);

        var first = OperationalMetricEvaluator.EvaluateDefinition(session, definition.Id);
        var second = OperationalMetricEvaluator.EvaluateDefinition(session, definition.Id);

        Assert.Same(first, second);
        Assert.Equal(0.5m, first.Value);
        Assert.True(session.TryGetEvaluation(definition.Id, out var cached));
        Assert.Same(first, cached);
    }

    [Fact]
    public void FailedDefinitionEvaluationAbandonsActiveStateForRetry()
    {
        var definition = AvailabilityDefinition();
        var session = CreateSession(definition, 120m, 100m);

        Assert.Throws<InvalidDataException>(() =>
            OperationalMetricEvaluator.EvaluateDefinition(session, definition.Id));

        session.BeginEvaluation(definition.Id);
        session.AbandonEvaluation(definition.Id);
        Assert.False(session.TryGetEvaluation(definition.Id, out _));
    }

    [Fact]
    public void RecursiveEvaluationUsesOnlyCanonicalPlanDefinition()
    {
        var canonical = AvailabilityDefinition();
        var session = CreateSession(canonical, 30m, 60m);
        var conflicting = canonical with
        {
            Formula = new OperationalMetricFormula.Ratio(
                "PlannedOperatingTime",
                "ActualProductionTime"),
        };

        Assert.Equal(canonical.Id, conflicting.Id);
        Assert.NotEqual(canonical.Formula, conflicting.Formula);

        var result = OperationalMetricEvaluator.EvaluateDefinition(session, conflicting.Id);

        Assert.Equal(0.5m, result.Value);
        Assert.Same(canonical, session.Plan.GetRequiredDefinition(canonical.Id));
    }

    [Fact]
    public void RecursiveEvaluationRejectsUnplannedExactIdAndDifferentVersion()
    {
        var definition = AvailabilityDefinition();
        var session = CreateSession(definition, 30m, 60m);
        var unrelated = new OperationalMetricDefinitionId("unrelated", "9.0");
        var availabilityV2 = new OperationalMetricDefinitionId(definition.Id.MetricKey, "2.0");

        Assert.Throws<InvalidOperationException>(() =>
            OperationalMetricEvaluator.EvaluateDefinition(session, unrelated));
        Assert.Throws<InvalidOperationException>(() =>
            OperationalMetricEvaluator.EvaluateDefinition(session, availabilityV2));
    }

    [Fact]
    public async Task PublicEvaluationRequestsCanonicalComponentKeysAndPreservesLocalEvidenceNames()
    {
        var definition = AvailabilityDefinition();
        var catalog = new OperationalMetricDefinitionCatalog([definition]);
        var machineId = MachineId.New();
        var processorId = new MetricAggregationProcessorId("aggregate-m01");
        var key = EvaluationKey(machineId, definition.Id);
        var revision = new MetricAggregationCheckpoint(
            processorId,
            MetricInputStreamId.ForMachine(machineId),
            new MetricInputPosition(7));
        var reader = new RecordingReader(revision, 30m, 60m);
        var evaluator = new OperationalMetricEvaluator(catalog, reader, processorId);

        var result = await evaluator.EvaluateAsync(key, CancellationToken.None);

        Assert.Equal(1, reader.ReadCount);
        Assert.Equal(
            [MetricInputKeys.ActualProductionTime, MetricInputKeys.PlannedOperatingTime],
            reader.LastRequest!.Operands.Select(operand => operand.OperandName));
        Assert.Equal(
            ["ActualProductionTime", "PlannedOperatingTime"],
            result.OperandEvidence.Select(evidence => evidence.OperandName));
        Assert.All(result.OperandEvidence, evidence => Assert.Equal(revision, evidence.SourceRevision));
    }

    private static OperationalMetricDefinition AvailabilityDefinition() =>
        BuiltInOperationalMetricDefinitions.All.Single(definition =>
            definition.Id == BuiltInOperationalMetricDefinitions.AvailabilityId);

    private static OperationalMetricEvaluationSession CreateSession(
        OperationalMetricDefinition definition,
        decimal numerator,
        decimal denominator)
    {
        var machineId = MachineId.New();
        var key = EvaluationKey(machineId, definition.Id);
        var processorId = new MetricAggregationProcessorId("aggregate-m01");
        var revision = new MetricAggregationCheckpoint(
            processorId,
            MetricInputStreamId.ForMachine(machineId),
            new MetricInputPosition(10));
        var plan = new OperationalMetricEvaluationPlan(
            key,
            definition,
            [definition],
            [
                new OperationalMetricComponentRequirement(
                    MetricInputKeys.ActualProductionTime,
                    MetricDimension.Duration,
                    MetricInputFactUnits.Seconds),
                new OperationalMetricComponentRequirement(
                    MetricInputKeys.PlannedOperatingTime,
                    MetricDimension.Duration,
                    MetricInputFactUnits.Seconds),
            ]);
        var snapshot = new OperationalMetricComponentSnapshot(
            key,
            revision,
            [
                Component(
                    key,
                    processorId,
                    MetricInputKeys.ActualProductionTime,
                    numerator),
                Component(
                    key,
                    processorId,
                    MetricInputKeys.PlannedOperatingTime,
                    denominator),
            ]);
        return new OperationalMetricEvaluationSession(plan, snapshot);
    }

    private static OperationalMetricComponent Component(
        OperationalMetricEvaluationKey key,
        MetricAggregationProcessorId processorId,
        string componentKey,
        decimal value) => new(
            componentKey,
            new OperationalMetricAggregateSourceIdentity(
                processorId,
                key.MachineId,
                key.PeriodId,
                componentKey),
            MetricDimension.Duration,
            new MetricAggregateValue(
                value,
                MetricInputFactUnits.Seconds,
                1,
                FirstTimestamp,
                LastTimestamp));

    private static OperationalMetricEvaluationKey EvaluationKey(
        MachineId machineId,
        OperationalMetricDefinitionId definitionId) => new(
            machineId,
            new OperationalMetricPeriodId.ProductionDay(
                new ProductionDayId(new SiteId("site-a"), new DateOnly(2026, 8, 28))),
            definitionId,
            OperationalMetricEvaluationContextKey.Unpartitioned);

    private sealed class RecordingReader : IOperationalMetricComponentSnapshotReader
    {
        private readonly MetricAggregationCheckpoint _revision;
        private readonly decimal _numerator;
        private readonly decimal _denominator;

        public RecordingReader(
            MetricAggregationCheckpoint revision,
            decimal numerator,
            decimal denominator)
        {
            _revision = revision;
            _numerator = numerator;
            _denominator = denominator;
        }

        public int ReadCount { get; private set; }

        public OperationalMetricComponentSnapshotRequest? LastRequest { get; private set; }

        public ValueTask<OperationalMetricComponentSnapshot> ReadAsync(
            OperationalMetricComponentSnapshotRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            LastRequest = request;

            var values = new Dictionary<string, decimal>(StringComparer.Ordinal)
            {
                [MetricInputKeys.ActualProductionTime] = _numerator,
                [MetricInputKeys.PlannedOperatingTime] = _denominator,
            };
            var components = request.Operands.Select(operand =>
            {
                var source = Assert.IsType<OperationalMetricOperandSource.Component>(operand.Source);
                return new OperationalMetricComponent(
                    operand.OperandName,
                    new OperationalMetricAggregateSourceIdentity(
                        request.ProcessorId,
                        request.EvaluationKey.MachineId,
                        request.EvaluationKey.PeriodId,
                        source.ComponentKey),
                    operand.RequiredDimension,
                    new MetricAggregateValue(
                        values[source.ComponentKey],
                        operand.RequiredUnit,
                        1,
                        FirstTimestamp,
                        LastTimestamp));
            });

            return ValueTask.FromResult(
                new OperationalMetricComponentSnapshot(request.EvaluationKey, _revision, components));
        }
    }
}
