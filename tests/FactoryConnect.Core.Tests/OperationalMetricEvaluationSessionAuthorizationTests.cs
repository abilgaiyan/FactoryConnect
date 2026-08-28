using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricEvaluationSessionAuthorizationTests
{
    [Fact]
    public void BeginningUnplannedDefinitionFails()
    {
        var session = CreateOeeSession(out _);
        var unplanned = new OperationalMetricDefinitionId("unrelated", "9.0");

        Assert.Throws<InvalidOperationException>(() => session.BeginEvaluation(unplanned));
    }

    [Fact]
    public void CompletingUnplannedDefinitionFailsEvenWhenIdentityAndRevisionMatch()
    {
        var session = CreateOeeSession(out var revision);
        var unplanned = new OperationalMetricDefinitionId("unrelated", "9.0");
        var evaluation = EvaluationFor(session.Plan.RootKey, unplanned, revision);

        Assert.Throws<InvalidOperationException>(() => session.CompleteEvaluation(unplanned, evaluation));
        Assert.Throws<InvalidOperationException>(() => session.TryGetEvaluation(unplanned, out _));
    }

    [Fact]
    public void EveryPlannedDependencyMayBeginAndComplete()
    {
        var session = CreateOeeSession(out var revision);

        foreach (var definition in session.Plan.DependencyOrder)
        {
            session.BeginEvaluation(definition.Id);
            var evaluation = EvaluationFor(session.Plan.RootKey, definition.Id, revision);
            session.CompleteEvaluation(definition.Id, evaluation);

            Assert.True(session.TryGetEvaluation(definition.Id, out var cached));
            Assert.Same(evaluation, cached);
        }
    }

    [Fact]
    public void PlannedExactVersionDoesNotAuthorizeAnotherVersion()
    {
        var session = CreateOeeSession(out _);
        Assert.Contains(
            session.Plan.DependencyOrder,
            definition => definition.Id == BuiltInOperationalMetricDefinitions.AvailabilityId);

        var availabilityV2 = new OperationalMetricDefinitionId(CanonicalMetricKeys.Availability, "2.0");

        Assert.Throws<InvalidOperationException>(() => session.BeginEvaluation(availabilityV2));
    }

    [Fact]
    public void PlanRequiresExactRootDefinitionExactlyOnce()
    {
        var root = BuiltInOperationalMetricDefinitions.All.Single(
            definition => definition.Id == BuiltInOperationalMetricDefinitions.OeeId);
        var key = RootKey();

        Assert.Throws<ArgumentException>(() =>
            new OperationalMetricEvaluationPlan(key, root, [], []));

        Assert.Throws<ArgumentException>(() =>
            new OperationalMetricEvaluationPlan(key, root, [root, root], []));
    }

    private static OperationalMetricEvaluationSession CreateOeeSession(
        out MetricAggregationCheckpoint revision)
    {
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var plan = new OperationalMetricEvaluationPlanner(catalog).CreatePlan(RootKey());
        revision = new MetricAggregationCheckpoint(
            new MetricAggregationProcessorId("aggregate-m01"),
            MetricInputStreamId.ForMachine(plan.RootKey.MachineId),
            new MetricInputPosition(20));
        var snapshot = new OperationalMetricComponentSnapshot(plan.RootKey, revision, []);
        return new OperationalMetricEvaluationSession(plan, snapshot);
    }

    private static OperationalMetricEvaluationKey RootKey() => new(
        new MachineId(new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
        new OperationalMetricPeriodId.ProductionDay(
            new ProductionDayId(new SiteId("site-a"), new DateOnly(2026, 8, 28))),
        BuiltInOperationalMetricDefinitions.OeeId,
        OperationalMetricEvaluationContextKey.Unpartitioned);

    private static OperationalMetricEvaluation EvaluationFor(
        OperationalMetricEvaluationKey rootKey,
        OperationalMetricDefinitionId definitionId,
        MetricAggregationCheckpoint revision) => new(
            new OperationalMetricEvaluationKey(
                rootKey.MachineId,
                rootKey.PeriodId,
                definitionId,
                rootKey.ContextKey),
            OperationalMetricEvaluationStatus.Calculated,
            0.5m,
            OperationalMetricUnits.Ratio,
            null,
            null,
            revision,
            []);
}
