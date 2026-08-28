using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricProjectionEvidenceTests
{
    [Fact]
    public void ProjectionPreservesRecursiveDependencyAndComponentEvidence()
    {
        var fixture = CreateFixture();
        var availability = AvailabilityEvaluation(fixture);
        var oee = new OperationalMetricEvaluation(
            new OperationalMetricEvaluationKey(
                fixture.MachineId,
                fixture.PeriodId,
                BuiltInOperationalMetricDefinitions.OeeId,
                OperationalMetricEvaluationContextKey.Unpartitioned),
            OperationalMetricEvaluationStatus.Calculated,
            1m / 9m,
            OperationalMetricUnits.Ratio,
            null,
            null,
            fixture.SourceRevision,
            [],
            [new OperationalMetricDependencyEvidence(
                "Availability",
                BuiltInOperationalMetricDefinitions.AvailabilityId,
                availability)]);

        var projection = fixture.Factory.Create(oee);

        Assert.Equal(0.11111111m, projection.Value);
        var dependency = Assert.Single(projection.DependencyEvidence);
        Assert.Equal("Availability", dependency.OperandName);
        Assert.Equal(BuiltInOperationalMetricDefinitions.AvailabilityId, dependency.DefinitionId);
        Assert.Equal(0.33333333m, dependency.Projection.Value);
        Assert.Equal(fixture.SourceRevision, dependency.Projection.SourceRevision);

        Assert.Collection(
            dependency.Projection.OperandEvidence,
            apt =>
            {
                Assert.Equal("ActualProductionTime", apt.OperandName);
                Assert.Equal(MetricInputKeys.ActualProductionTime, apt.SourceIdentity.ComponentKey);
                Assert.Equal(20m, apt.Value);
                Assert.Equal(MetricDimension.Duration, apt.Dimension);
                Assert.Equal(MetricInputFactUnits.Seconds, apt.Unit);
                Assert.Equal(2, apt.InputCount);
                Assert.Equal(fixture.FirstTimestamp, apt.FirstInputTimestamp);
                Assert.Equal(fixture.LastTimestamp, apt.LastInputTimestamp);
            },
            pot =>
            {
                Assert.Equal("PlannedOperatingTime", pot.OperandName);
                Assert.Equal(MetricInputKeys.PlannedOperatingTime, pot.SourceIdentity.ComponentKey);
                Assert.Equal(60m, pot.Value);
                Assert.Equal(MetricDimension.Duration, pot.Dimension);
                Assert.Equal(MetricInputFactUnits.Seconds, pot.Unit);
                Assert.Equal(3, pot.InputCount);
            });
    }

    [Fact]
    public void StructuralEquivalenceIgnoresCollectionIdentityButDetectsEvidenceChanges()
    {
        var fixture = CreateFixture();
        var evaluation = AvailabilityEvaluation(fixture);

        var first = fixture.Factory.Create(evaluation);
        var second = fixture.Factory.Create(evaluation);

        Assert.True(OperationalMetricProjectionEquivalence.AreEquivalent(first, second));
        Assert.NotSame(first.OperandEvidence, second.OperandEvidence);

        var changedEvidence = first.OperandEvidence
            .Select(evidence => evidence.OperandName == "ActualProductionTime"
                ? new OperationalMetricComponentProjectionEvidence(
                    evidence.OperandName,
                    evidence.SourceIdentity,
                    evidence.SourceRevision,
                    evidence.Dimension,
                    evidence.Value + 1m,
                    evidence.Unit,
                    evidence.InputCount,
                    evidence.FirstInputTimestamp,
                    evidence.LastInputTimestamp)
                : evidence)
            .ToArray();
        var changed = new OperationalMetricProjection(
            first.ProcessorId,
            first.Key,
            first.Status,
            first.Value,
            first.Unit,
            first.ReasonCode,
            first.ReasonOperandName,
            first.SourceRevision,
            changedEvidence,
            first.DependencyEvidence);

        Assert.False(OperationalMetricProjectionEquivalence.AreEquivalent(first, changed));
    }

    [Fact]
    public void StructuralEquivalenceTreatsDependencyEvidenceOrderAsSemantic()
    {
        var fixture = CreateFixture();
        var availability = fixture.Factory.Create(AvailabilityEvaluation(fixture));
        var performance = new OperationalMetricProjection(
            fixture.ProjectionProcessorId,
            new OperationalMetricEvaluationKey(
                fixture.MachineId,
                fixture.PeriodId,
                BuiltInOperationalMetricDefinitions.PerformanceId,
                OperationalMetricEvaluationContextKey.Unpartitioned),
            OperationalMetricEvaluationStatus.Calculated,
            0.5m,
            OperationalMetricUnits.Ratio,
            null,
            null,
            fixture.SourceRevision);

        var key = new OperationalMetricEvaluationKey(
            fixture.MachineId,
            fixture.PeriodId,
            BuiltInOperationalMetricDefinitions.OeeId,
            OperationalMetricEvaluationContextKey.Unpartitioned);
        var first = new OperationalMetricProjection(
            fixture.ProjectionProcessorId,
            key,
            OperationalMetricEvaluationStatus.Calculated,
            0.1m,
            OperationalMetricUnits.Ratio,
            null,
            null,
            fixture.SourceRevision,
            dependencyEvidence:
            [
                new OperationalMetricDependencyProjectionEvidence(
                    "Availability",
                    BuiltInOperationalMetricDefinitions.AvailabilityId,
                    availability),
                new OperationalMetricDependencyProjectionEvidence(
                    "Performance",
                    BuiltInOperationalMetricDefinitions.PerformanceId,
                    performance),
            ]);
        var reordered = new OperationalMetricProjection(
            fixture.ProjectionProcessorId,
            key,
            OperationalMetricEvaluationStatus.Calculated,
            0.1m,
            OperationalMetricUnits.Ratio,
            null,
            null,
            fixture.SourceRevision,
            dependencyEvidence:
            [
                new OperationalMetricDependencyProjectionEvidence(
                    "Performance",
                    BuiltInOperationalMetricDefinitions.PerformanceId,
                    performance),
                new OperationalMetricDependencyProjectionEvidence(
                    "Availability",
                    BuiltInOperationalMetricDefinitions.AvailabilityId,
                    availability),
            ]);

        Assert.False(OperationalMetricProjectionEquivalence.AreEquivalent(first, reordered));
    }

    private static ProjectionFixture CreateFixture()
    {
        var machineId = MachineId.New();
        var sourceProcessorId = new MetricAggregationProcessorId("aggregate-m01");
        var sourceRevision = new MetricAggregationCheckpoint(
            sourceProcessorId,
            MetricInputStreamId.ForMachine(machineId),
            new MetricInputPosition(40));
        var projectionProcessorId = new OperationalMetricProjectionProcessorId("metric-projection-m01");
        var periodId = new OperationalMetricPeriodId.ProductionDay(
            new ProductionDayId(new SiteId("site-a"), new DateOnly(2026, 8, 29)));
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var factory = new OperationalMetricProjectionFactory(catalog, projectionProcessorId);

        return new ProjectionFixture(
            machineId,
            sourceRevision,
            projectionProcessorId,
            periodId,
            new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 29, 0, 10, 0, TimeSpan.Zero),
            factory);
    }

    private static OperationalMetricEvaluation AvailabilityEvaluation(ProjectionFixture fixture)
    {
        var key = new OperationalMetricEvaluationKey(
            fixture.MachineId,
            fixture.PeriodId,
            BuiltInOperationalMetricDefinitions.AvailabilityId,
            OperationalMetricEvaluationContextKey.Unpartitioned);
        return new OperationalMetricEvaluation(
            key,
            OperationalMetricEvaluationStatus.Calculated,
            1m / 3m,
            OperationalMetricUnits.Ratio,
            null,
            null,
            fixture.SourceRevision,
            [
                ComponentEvidence(
                    fixture,
                    "ActualProductionTime",
                    MetricInputKeys.ActualProductionTime,
                    20m,
                    2),
                ComponentEvidence(
                    fixture,
                    "PlannedOperatingTime",
                    MetricInputKeys.PlannedOperatingTime,
                    60m,
                    3),
            ]);
    }

    private static MetricOperandEvidence ComponentEvidence(
        ProjectionFixture fixture,
        string operandName,
        string componentKey,
        decimal value,
        long inputCount) => new(
            operandName,
            new OperationalMetricAggregateSourceIdentity(
                fixture.SourceRevision.ProcessorId,
                fixture.MachineId,
                fixture.PeriodId,
                componentKey),
            fixture.SourceRevision,
            MetricDimension.Duration,
            value,
            MetricInputFactUnits.Seconds,
            inputCount,
            fixture.FirstTimestamp,
            fixture.LastTimestamp);

    private sealed record ProjectionFixture(
        MachineId MachineId,
        MetricAggregationCheckpoint SourceRevision,
        OperationalMetricProjectionProcessorId ProjectionProcessorId,
        OperationalMetricPeriodId PeriodId,
        DateTimeOffset FirstTimestamp,
        DateTimeOffset LastTimestamp,
        OperationalMetricProjectionFactory Factory);
}
