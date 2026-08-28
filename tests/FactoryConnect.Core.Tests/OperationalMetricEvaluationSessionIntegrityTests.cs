using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricEvaluationSessionIntegrityTests
{
    [Fact]
    public void CompletionRejectsWrongMachine()
    {
        var session = CreateSession(out var definitionId, out var revision);
        session.BeginEvaluation(definitionId);

        var wrongMachine = new MachineId(new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var expected = ExpectedKey(definitionId);
        var key = new OperationalMetricEvaluationKey(
            wrongMachine,
            expected.PeriodId,
            definitionId,
            OperationalMetricEvaluationContextKey.Unpartitioned);
        var wrongRevision = new MetricAggregationCheckpoint(
            revision.ProcessorId,
            MetricInputStreamId.ForMachine(wrongMachine),
            revision.Position);

        Assert.Throws<InvalidDataException>(() =>
            session.CompleteEvaluation(definitionId, Evaluation(key, wrongRevision)));
    }

    [Fact]
    public void CompletionRejectsWrongPeriod()
    {
        var session = CreateSession(out var definitionId, out var revision);
        session.BeginEvaluation(definitionId);

        var key = new OperationalMetricEvaluationKey(
            ExpectedKey(definitionId).MachineId,
            new OperationalMetricPeriodId.ProductionDay(
                new ProductionDayId(new SiteId("site-a"), new DateOnly(2026, 8, 29))),
            definitionId,
            OperationalMetricEvaluationContextKey.Unpartitioned);

        Assert.Throws<InvalidDataException>(() =>
            session.CompleteEvaluation(definitionId, Evaluation(key, revision)));
    }

    [Fact]
    public void CompletionRejectsWrongContext()
    {
        var session = CreateSession(out var definitionId, out var revision);
        session.BeginEvaluation(definitionId);

        var context = new OperationalMetricEvaluationContextKey
        {
            ProductionOrderId = new ProductionOrderId("po-1"),
        };
        var expected = ExpectedKey(definitionId);
        var key = new OperationalMetricEvaluationKey(
            expected.MachineId,
            expected.PeriodId,
            definitionId,
            context);

        Assert.Throws<InvalidDataException>(() =>
            session.CompleteEvaluation(definitionId, Evaluation(key, revision)));
    }

    [Fact]
    public void CompletionRejectsWrongDefinitionVersion()
    {
        var session = CreateSession(out var definitionId, out var revision);
        session.BeginEvaluation(definitionId);

        var wrongId = new OperationalMetricDefinitionId(definitionId.MetricKey, "2.0");
        var key = ExpectedKey(wrongId);

        Assert.Throws<InvalidDataException>(() =>
            session.CompleteEvaluation(definitionId, Evaluation(key, revision)));
    }

    [Fact]
    public void CompletionRejectsWrongSourceRevision()
    {
        var session = CreateSession(out var definitionId, out var revision);
        session.BeginEvaluation(definitionId);

        var wrongRevision = new MetricAggregationCheckpoint(
            revision.ProcessorId,
            revision.StreamId,
            new MetricInputPosition(revision.Position.Value + 1));

        Assert.Throws<InvalidDataException>(() =>
            session.CompleteEvaluation(definitionId, Evaluation(ExpectedKey(definitionId), wrongRevision)));
    }

    [Fact]
    public void CompletedEvaluationCannotBeBegunAgainOrReplaced()
    {
        var session = CreateSession(out var definitionId, out var revision);
        var evaluation = Evaluation(ExpectedKey(definitionId), revision);
        session.BeginEvaluation(definitionId);
        session.CompleteEvaluation(definitionId, evaluation);

        Assert.Throws<InvalidOperationException>(() => session.BeginEvaluation(definitionId));
        Assert.Throws<InvalidOperationException>(() => session.CompleteEvaluation(definitionId, evaluation));
        Assert.True(session.TryGetEvaluation(definitionId, out var cached));
        Assert.Same(evaluation, cached);
    }

    private static OperationalMetricEvaluationSession CreateSession(
        out OperationalMetricDefinitionId definitionId,
        out MetricAggregationCheckpoint revision)
    {
        definitionId = BuiltInOperationalMetricDefinitions.AvailabilityId;
        var localDefinitionId = definitionId;
        var key = ExpectedKey(localDefinitionId);
        revision = new MetricAggregationCheckpoint(
            new MetricAggregationProcessorId("aggregate-m01"),
            MetricInputStreamId.ForMachine(key.MachineId),
            new MetricInputPosition(10));
        var definition = BuiltInOperationalMetricDefinitions.All.Single(candidate => candidate.Id == localDefinitionId);
        var plan = new OperationalMetricEvaluationPlan(key, definition, [definition], []);
        var snapshot = new OperationalMetricComponentSnapshot(key, revision, []);
        return new OperationalMetricEvaluationSession(plan, snapshot);
    }

    private static OperationalMetricEvaluationKey ExpectedKey(OperationalMetricDefinitionId definitionId) => new(
        new MachineId(new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
        new OperationalMetricPeriodId.ProductionDay(
            new ProductionDayId(new SiteId("site-a"), new DateOnly(2026, 8, 28))),
        definitionId,
        OperationalMetricEvaluationContextKey.Unpartitioned);

    private static OperationalMetricEvaluation Evaluation(
        OperationalMetricEvaluationKey key,
        MetricAggregationCheckpoint revision) => new(
            key,
            OperationalMetricEvaluationStatus.Calculated,
            0.5m,
            OperationalMetricUnits.Ratio,
            null,
            null,
            revision,
            []);
}
