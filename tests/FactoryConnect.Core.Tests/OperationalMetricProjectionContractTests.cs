using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricProjectionContractTests
{
    [Fact]
    public void CalculatedProjectionRoundsOnlyAtDurableBoundary()
    {
        var fixture = CreateFixture();
        var logicalValue = 1m / 3m;
        var evaluation = Evaluation(
            fixture,
            OperationalMetricEvaluationStatus.Calculated,
            logicalValue,
            null,
            null);

        var projection = fixture.Factory.Create(evaluation);

        Assert.Equal(logicalValue, evaluation.Value);
        Assert.NotEqual(0.33333333m, evaluation.Value);
        Assert.Equal(0.33333333m, projection.Value);
        Assert.Equal(evaluation.Key, projection.Key);
        Assert.Equal(evaluation.SourceRevision, projection.SourceRevision);
    }

    [Fact]
    public void NonCalculatedProjectionPreservesReasonWithoutValue()
    {
        var fixture = CreateFixture();
        var evaluation = Evaluation(
            fixture,
            OperationalMetricEvaluationStatus.InsufficientEvidence,
            null,
            OperationalMetricEvaluationReasonCode.MissingOperand,
            "PlannedOperatingTime");

        var projection = fixture.Factory.Create(evaluation);

        Assert.Null(projection.Value);
        Assert.Equal(evaluation.Status, projection.Status);
        Assert.Equal(evaluation.ReasonCode, projection.ReasonCode);
        Assert.Equal(evaluation.ReasonOperandName, projection.ReasonOperandName);
    }

    [Fact]
    public void CommitRequiresStrictSourceRevisionAdvancement()
    {
        var fixture = CreateFixture();
        var expected = new OperationalMetricProjectionCheckpoint(
            fixture.ProjectionProcessorId,
            fixture.SourceRevision);
        var proposedRevision = new MetricAggregationCheckpoint(
            fixture.SourceRevision.ProcessorId,
            fixture.SourceRevision.StreamId,
            new MetricInputPosition(fixture.SourceRevision.Position.Value + 1));
        var proposed = new OperationalMetricProjectionCheckpoint(
            fixture.ProjectionProcessorId,
            proposedRevision);

        var commit = new OperationalMetricProjectionCommit(
            fixture.ProjectionProcessorId,
            expected,
            proposed,
            []);

        Assert.Empty(commit.Projections);
        Assert.Equal(expected, commit.ExpectedCheckpoint);
        Assert.Equal(proposed, commit.ProposedCheckpoint);

        Assert.Throws<ArgumentException>(() =>
            new OperationalMetricProjectionCommit(
                fixture.ProjectionProcessorId,
                expected,
                expected,
                []));
    }

    [Fact]
    public void CommitRejectsProjectionFromDifferentSourceRevision()
    {
        var fixture = CreateFixture();
        var evaluation = Evaluation(
            fixture,
            OperationalMetricEvaluationStatus.Calculated,
            0.5m,
            null,
            null);
        var projection = fixture.Factory.Create(evaluation);
        var proposedRevision = new MetricAggregationCheckpoint(
            fixture.SourceRevision.ProcessorId,
            fixture.SourceRevision.StreamId,
            new MetricInputPosition(fixture.SourceRevision.Position.Value + 1));
        var proposed = new OperationalMetricProjectionCheckpoint(
            fixture.ProjectionProcessorId,
            proposedRevision);

        Assert.Throws<ArgumentException>(() =>
            new OperationalMetricProjectionCommit(
                fixture.ProjectionProcessorId,
                null,
                proposed,
                [projection]));
    }

    [Fact]
    public void CommitRejectsDuplicateEvaluationKeys()
    {
        var fixture = CreateFixture();
        var evaluation = Evaluation(
            fixture,
            OperationalMetricEvaluationStatus.Calculated,
            0.5m,
            null,
            null);
        var projection = fixture.Factory.Create(evaluation);
        var checkpoint = new OperationalMetricProjectionCheckpoint(
            fixture.ProjectionProcessorId,
            fixture.SourceRevision);

        Assert.Throws<ArgumentException>(() =>
            new OperationalMetricProjectionCommit(
                fixture.ProjectionProcessorId,
                null,
                checkpoint,
                [projection, projection]));
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
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var factory = new OperationalMetricProjectionFactory(catalog, projectionProcessorId);

        return new ProjectionFixture(
            machineId,
            sourceRevision,
            projectionProcessorId,
            factory);
    }

    private static OperationalMetricEvaluation Evaluation(
        ProjectionFixture fixture,
        OperationalMetricEvaluationStatus status,
        decimal? value,
        OperationalMetricEvaluationReasonCode? reasonCode,
        string? reasonOperandName) => new(
            new OperationalMetricEvaluationKey(
                fixture.MachineId,
                new OperationalMetricPeriodId.ProductionDay(
                    new ProductionDayId(new SiteId("site-a"), new DateOnly(2026, 8, 29))),
                BuiltInOperationalMetricDefinitions.AvailabilityId,
                OperationalMetricEvaluationContextKey.Unpartitioned),
            status,
            value,
            OperationalMetricUnits.Ratio,
            reasonCode,
            reasonOperandName,
            fixture.SourceRevision,
            []);

    private sealed record ProjectionFixture(
        MachineId MachineId,
        MetricAggregationCheckpoint SourceRevision,
        OperationalMetricProjectionProcessorId ProjectionProcessorId,
        OperationalMetricProjectionFactory Factory);
}
