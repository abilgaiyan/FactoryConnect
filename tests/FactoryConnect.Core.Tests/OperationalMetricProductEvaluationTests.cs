using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricProductEvaluationTests
{
    private static readonly DateTimeOffset FirstTimestamp = new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastTimestamp = new(2026, 8, 28, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OeeUsesOneCoherentSnapshotAndPreservesAuthoredDependencyOrder()
    {
        var fixture = CreateFixture();
        fixture.Reader.Set(MetricInputKeys.ActualProductionTime, 1m);
        fixture.Reader.Set(MetricInputKeys.PlannedOperatingTime, 3m);
        fixture.Reader.Set(MetricInputKeys.ProductionReferenceTime, 0.5m);
        fixture.Reader.Set(MetricInputKeys.GoodQuantity, 2m);
        fixture.Reader.Set(MetricInputKeys.ProducedQuantity, 3m);

        var result = await fixture.Evaluator.EvaluateAsync(
            Key(fixture, BuiltInOperationalMetricDefinitions.OeeId),
            CancellationToken.None);

        Assert.Equal(OperationalMetricEvaluationStatus.Calculated, result.Status);
        Assert.Equal((1m / 3m) * (0.5m / 1m) * (2m / 3m), result.Value);
        Assert.Equal(1, fixture.Reader.ReadCount);
        Assert.Empty(result.OperandEvidence);
        Assert.Equal(
            ["Availability", "Performance", "Quality"],
            result.DependencyEvidence.Select(evidence => evidence.OperandName));
        Assert.Equal(
            [
                BuiltInOperationalMetricDefinitions.AvailabilityId,
                BuiltInOperationalMetricDefinitions.PerformanceId,
                BuiltInOperationalMetricDefinitions.QualityId,
            ],
            result.DependencyEvidence.Select(evidence => evidence.DefinitionId));
        Assert.All(result.DependencyEvidence, evidence =>
        {
            Assert.Equal(fixture.Revision, evidence.Evaluation.SourceRevision);
            Assert.Equal(fixture.MachineId, evidence.Evaluation.Key.MachineId);
            Assert.Equal(result.Key.PeriodId, evidence.Evaluation.Key.PeriodId);
            Assert.Equal(result.Key.ContextKey, evidence.Evaluation.Key.ContextKey);
        });
        Assert.Equal(3, result.Evidence.Count);
    }

    [Fact]
    public async Task OeeDoesNotRoundLeafRatiosBeforeProductComposition()
    {
        var fixture = CreateFixture();
        fixture.Reader.Set(MetricInputKeys.ActualProductionTime, 1m);
        fixture.Reader.Set(MetricInputKeys.PlannedOperatingTime, 3m);
        fixture.Reader.Set(MetricInputKeys.ProductionReferenceTime, 1m);
        fixture.Reader.Set(MetricInputKeys.GoodQuantity, 1m);
        fixture.Reader.Set(MetricInputKeys.ProducedQuantity, 3m);

        var result = await fixture.Evaluator.EvaluateAsync(
            Key(fixture, BuiltInOperationalMetricDefinitions.OeeId),
            CancellationToken.None);

        var expected = (1m / 3m) * 1m * (1m / 3m);
        var prematurelyRounded = 0.33333333m * 1m * 0.33333333m;
        Assert.Equal(expected, result.Value);
        Assert.NotEqual(prematurelyRounded, result.Value);
    }

    [Fact]
    public async Task UnavailableDependencyPropagatesAfterCollectingCompleteEvidence()
    {
        var fixture = CreateFixture();
        fixture.Reader.Set(MetricInputKeys.ActualProductionTime, 1m);
        fixture.Reader.Set(MetricInputKeys.PlannedOperatingTime, 0m);
        fixture.Reader.Set(MetricInputKeys.ProductionReferenceTime, 1m);
        fixture.Reader.Set(MetricInputKeys.GoodQuantity, 1m);
        fixture.Reader.Set(MetricInputKeys.ProducedQuantity, 1m);

        var result = await fixture.Evaluator.EvaluateAsync(
            Key(fixture, BuiltInOperationalMetricDefinitions.OeeId),
            CancellationToken.None);

        Assert.Equal(OperationalMetricEvaluationStatus.Unavailable, result.Status);
        Assert.Equal(OperationalMetricEvaluationReasonCode.DependencyUnavailable, result.ReasonCode);
        Assert.Equal("Availability", result.ReasonOperandName);
        Assert.Equal(
            ["Availability", "Performance", "Quality"],
            result.DependencyEvidence.Select(evidence => evidence.OperandName));
        Assert.Equal(
            OperationalMetricEvaluationReasonCode.ZeroDenominator,
            result.DependencyEvidence[0].Evaluation.ReasonCode);
        Assert.Equal(OperationalMetricEvaluationStatus.Calculated, result.DependencyEvidence[1].Evaluation.Status);
        Assert.Equal(OperationalMetricEvaluationStatus.Calculated, result.DependencyEvidence[2].Evaluation.Status);
    }

    [Fact]
    public async Task InsufficientDependencyPropagatesAfterCollectingCompleteEvidence()
    {
        var fixture = CreateFixture();
        fixture.Reader.Set(MetricInputKeys.ActualProductionTime, 1m);
        fixture.Reader.Set(MetricInputKeys.PlannedOperatingTime, 2m);
        fixture.Reader.Set(MetricInputKeys.GoodQuantity, 1m);
        fixture.Reader.Set(MetricInputKeys.ProducedQuantity, 1m);

        var result = await fixture.Evaluator.EvaluateAsync(
            Key(fixture, BuiltInOperationalMetricDefinitions.OeeId),
            CancellationToken.None);

        Assert.Equal(OperationalMetricEvaluationStatus.InsufficientEvidence, result.Status);
        Assert.Equal(OperationalMetricEvaluationReasonCode.DependencyInsufficientEvidence, result.ReasonCode);
        Assert.Equal("Performance", result.ReasonOperandName);
        Assert.Equal(
            ["Availability", "Performance", "Quality"],
            result.DependencyEvidence.Select(evidence => evidence.OperandName));
        Assert.Equal(
            OperationalMetricEvaluationReasonCode.MissingReferenceTime,
            result.DependencyEvidence[1].Evaluation.ReasonCode);
        Assert.Equal(OperationalMetricEvaluationStatus.Calculated, result.DependencyEvidence[2].Evaluation.Status);
    }

    [Fact]
    public async Task UnavailableEarlierDependencyCannotHideInvalidLaterDependency()
    {
        var fixture = CreateFixture();
        fixture.Reader.Set(MetricInputKeys.ActualProductionTime, 1m);
        fixture.Reader.Set(MetricInputKeys.PlannedOperatingTime, 0m);
        fixture.Reader.Set(MetricInputKeys.ProductionReferenceTime, 1m);
        fixture.Reader.Set(MetricInputKeys.GoodQuantity, 2m);
        fixture.Reader.Set(MetricInputKeys.ProducedQuantity, 1m);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Evaluator.EvaluateAsync(
                Key(fixture, BuiltInOperationalMetricDefinitions.OeeId),
                CancellationToken.None));
    }

    [Fact]
    public async Task AvailabilityV2RegistrationCannotRetargetOeeV1()
    {
        var availabilityV1 = BuiltInOperationalMetricDefinitions.All.Single(
            definition => definition.Id == BuiltInOperationalMetricDefinitions.AvailabilityId);
        var availabilityV2 = availabilityV1 with
        {
            Id = new OperationalMetricDefinitionId(CanonicalMetricKeys.Availability, "2.0"),
            DisplayName = "Availability v2",
            Formula = new OperationalMetricFormula.Ratio("PlannedOperatingTime", "ActualProductionTime"),
            DomainConstraints = new OperationalMetricDomainConstraints { MinimumInclusive = 0m },
        };
        var definitions = BuiltInOperationalMetricDefinitions.All.Concat([availabilityV2]).ToArray();
        var fixture = CreateFixture(definitions);
        fixture.Reader.Set(MetricInputKeys.ActualProductionTime, 1m);
        fixture.Reader.Set(MetricInputKeys.PlannedOperatingTime, 2m);
        fixture.Reader.Set(MetricInputKeys.ProductionReferenceTime, 1m);
        fixture.Reader.Set(MetricInputKeys.GoodQuantity, 1m);
        fixture.Reader.Set(MetricInputKeys.ProducedQuantity, 1m);

        var result = await fixture.Evaluator.EvaluateAsync(
            Key(fixture, BuiltInOperationalMetricDefinitions.OeeId),
            CancellationToken.None);

        Assert.Equal(0.5m, result.Value);
        Assert.Equal(
            BuiltInOperationalMetricDefinitions.AvailabilityId,
            result.DependencyEvidence[0].DefinitionId);
        Assert.DoesNotContain(
            result.DependencyEvidence,
            evidence => evidence.DefinitionId == availabilityV2.Id);
    }

    [Fact]
    public async Task ShiftOeeUsesShiftIdentityForEveryDependency()
    {
        var fixture = CreateFixture();
        fixture.Reader.Set(MetricInputKeys.ActualProductionTime, 1m);
        fixture.Reader.Set(MetricInputKeys.PlannedOperatingTime, 2m);
        fixture.Reader.Set(MetricInputKeys.ProductionReferenceTime, 1m);
        fixture.Reader.Set(MetricInputKeys.GoodQuantity, 1m);
        fixture.Reader.Set(MetricInputKeys.ProducedQuantity, 1m);
        var occurrence = new ShiftOccurrenceId(
            new SiteId("site-a"),
            new ShiftScheduleAssignmentId("schedule-a"),
            new ShiftId("shift-a"),
            FirstTimestamp,
            FirstTimestamp.AddHours(8));
        var key = new OperationalMetricEvaluationKey(
            fixture.MachineId,
            new OperationalMetricPeriodId.Shift(occurrence),
            BuiltInOperationalMetricDefinitions.OeeId,
            OperationalMetricEvaluationContextKey.Unpartitioned);

        var result = await fixture.Evaluator.EvaluateAsync(key, CancellationToken.None);

        Assert.Equal(0.5m, result.Value);
        Assert.All(result.DependencyEvidence, evidence => Assert.Equal(key.PeriodId, evidence.Evaluation.Key.PeriodId));
        Assert.Equal(1, fixture.Reader.ReadCount);
    }

    private static ProductFixture CreateFixture(IEnumerable<OperationalMetricDefinition>? definitions = null)
    {
        var machineId = MachineId.New();
        var productionDayId = new ProductionDayId(new SiteId("site-a"), new DateOnly(2026, 8, 28));
        var processorId = new MetricAggregationProcessorId("aggregate-m01");
        var revision = new MetricAggregationCheckpoint(
            processorId,
            MetricInputStreamId.ForMachine(machineId),
            new MetricInputPosition(50));
        var reader = new ProductSnapshotReader(revision);
        var catalog = new OperationalMetricDefinitionCatalog(
            definitions ?? BuiltInOperationalMetricDefinitions.All);
        var evaluator = new OperationalMetricEvaluator(catalog, reader, processorId);
        return new ProductFixture(machineId, productionDayId, revision, reader, evaluator);
    }

    private static OperationalMetricEvaluationKey Key(
        ProductFixture fixture,
        OperationalMetricDefinitionId definitionId) => new(
            fixture.MachineId,
            new OperationalMetricPeriodId.ProductionDay(fixture.ProductionDayId),
            definitionId,
            OperationalMetricEvaluationContextKey.Unpartitioned);

    private sealed record ProductFixture(
        MachineId MachineId,
        ProductionDayId ProductionDayId,
        MetricAggregationCheckpoint Revision,
        ProductSnapshotReader Reader,
        OperationalMetricEvaluator Evaluator);

    private sealed class ProductSnapshotReader : IOperationalMetricComponentSnapshotReader
    {
        private readonly MetricAggregationCheckpoint _revision;
        private readonly Dictionary<string, decimal> _values = new(StringComparer.Ordinal);

        public ProductSnapshotReader(MetricAggregationCheckpoint revision)
        {
            _revision = revision;
        }

        public int ReadCount { get; private set; }

        public void Set(string componentKey, decimal value) => _values[componentKey] = value;

        public ValueTask<OperationalMetricComponentSnapshot> ReadAsync(
            OperationalMetricComponentSnapshotRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;

            var components = new List<OperationalMetricComponent>();
            foreach (var operand in request.Operands)
            {
                var source = Assert.IsType<OperationalMetricOperandSource.Component>(operand.Source);
                if (!_values.TryGetValue(source.ComponentKey, out var value))
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
                    new MetricAggregateValue(
                        value,
                        operand.RequiredUnit,
                        1,
                        FirstTimestamp,
                        LastTimestamp)));
            }

            return ValueTask.FromResult(
                new OperationalMetricComponentSnapshot(request.EvaluationKey, _revision, components));
        }
    }
}
