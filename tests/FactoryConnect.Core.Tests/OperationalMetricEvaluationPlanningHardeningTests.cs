using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricEvaluationPlanningHardeningTests
{
    [Fact]
    public void PlannerRejectsDirectDependencyCycleFromMalformedCatalog()
    {
        var id = new OperationalMetricDefinitionId("cycle-a", "1.0");
        var definition = MalformedDependencyDefinition(id, id);
        var planner = new OperationalMetricEvaluationPlanner(new MalformedCatalog([definition]));

        Assert.Throws<InvalidDataException>(() => planner.CreatePlan(EvaluationKey(id)));
    }

    [Fact]
    public void PlannerRejectsMultiNodeDependencyCycleFromMalformedCatalog()
    {
        var aId = new OperationalMetricDefinitionId("cycle-a", "1.0");
        var bId = new OperationalMetricDefinitionId("cycle-b", "1.0");
        var a = MalformedDependencyDefinition(aId, bId);
        var b = MalformedDependencyDefinition(bId, aId);
        var planner = new OperationalMetricEvaluationPlanner(new MalformedCatalog([a, b]));

        Assert.Throws<InvalidDataException>(() => planner.CreatePlan(EvaluationKey(aId)));
    }

    [Fact]
    public void ProductDependenciesPreserveAuthoredFactorOrder()
    {
        var firstId = new OperationalMetricDefinitionId("z-first", "1.0");
        var secondId = new OperationalMetricDefinitionId("a-second", "1.0");
        var thirdId = new OperationalMetricDefinitionId("m-third", "1.0");
        var rootId = new OperationalMetricDefinitionId("root", "1.0");

        var first = RatioDefinition(firstId, "first-a", "first-b");
        var second = RatioDefinition(secondId, "second-a", "second-b");
        var third = RatioDefinition(thirdId, "third-a", "third-b");
        var root = new OperationalMetricDefinition
        {
            Id = rootId,
            SupportedScopes = BothScopes(),
            Operands =
            [
                Evaluated("first", firstId),
                Evaluated("second", secondId),
                Evaluated("third", thirdId),
            ],
            ResultUnit = OperationalMetricUnits.Ratio,
            Formula = new OperationalMetricFormula.Product(["third", "first", "second"]),
            DomainConstraints = new OperationalMetricDomainConstraints { MinimumInclusive = 0m },
            PrecisionPolicy = Precision(),
        };
        var planner = new OperationalMetricEvaluationPlanner(
            new OperationalMetricDefinitionCatalog([root, first, second, third]));

        var plan = planner.CreatePlan(EvaluationKey(rootId));

        Assert.Equal(
            [thirdId, firstId, secondId, rootId],
            plan.DependencyOrder.Select(definition => definition.Id));
    }

    [Fact]
    public async Task SessionFactoryRejectsSnapshotFromWrongAggregationProcessor()
    {
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var plan = new OperationalMetricEvaluationPlanner(catalog).CreatePlan(
            EvaluationKey(BuiltInOperationalMetricDefinitions.AvailabilityId));
        var expectedProcessor = new MetricAggregationProcessorId("aggregate-expected");
        var wrongProcessor = new MetricAggregationProcessorId("aggregate-wrong");
        var factory = new OperationalMetricEvaluationSessionFactory(
            new WrongProcessorSnapshotReader(wrongProcessor),
            expectedProcessor);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await factory.CreateAsync(plan, CancellationToken.None));
    }

    [Fact]
    public void SessionOwnsMemoizationAndActiveEvaluationState()
    {
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var plan = new OperationalMetricEvaluationPlanner(catalog).CreatePlan(
            EvaluationKey(BuiltInOperationalMetricDefinitions.AvailabilityId));
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var revision = new MetricAggregationCheckpoint(
            processor,
            MetricInputStreamId.ForMachine(plan.RootKey.MachineId),
            new MetricInputPosition(1));
        var session = new OperationalMetricEvaluationSession(
            plan,
            new OperationalMetricComponentSnapshot(plan.RootKey, revision, []));
        var definitionId = BuiltInOperationalMetricDefinitions.AvailabilityId;

        Assert.False(session.TryGetEvaluation(definitionId, out _));

        session.BeginEvaluation(definitionId);
        Assert.Throws<InvalidDataException>(() => session.BeginEvaluation(definitionId));
        session.AbandonEvaluation(definitionId);

        session.BeginEvaluation(definitionId);
        var evaluation = new OperationalMetricEvaluation(
            plan.RootKey,
            OperationalMetricEvaluationStatus.Calculated,
            0.5m,
            OperationalMetricUnits.Ratio,
            null,
            null,
            revision,
            []);
        session.CompleteEvaluation(definitionId, evaluation);

        Assert.True(session.TryGetEvaluation(definitionId, out var cached));
        Assert.Same(evaluation, cached);
    }

    private static OperationalMetricEvaluationKey EvaluationKey(OperationalMetricDefinitionId definitionId) => new(
        new MachineId(new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
        new OperationalMetricPeriodId.ProductionDay(
            new ProductionDayId(new SiteId("site-a"), new DateOnly(2026, 8, 28))),
        definitionId,
        OperationalMetricEvaluationContextKey.Unpartitioned);

    private static OperationalMetricDefinition MalformedDependencyDefinition(
        OperationalMetricDefinitionId id,
        OperationalMetricDefinitionId dependencyId) => new()
    {
        Id = id,
        SupportedScopes = BothScopes(),
        Operands = [Evaluated("dependency", dependencyId)],
        ResultUnit = OperationalMetricUnits.Ratio,
        Formula = new OperationalMetricFormula.Product(["dependency"]),
        DomainConstraints = new OperationalMetricDomainConstraints { MinimumInclusive = 0m },
        PrecisionPolicy = Precision(),
    };

    private static OperationalMetricDefinition RatioDefinition(
        OperationalMetricDefinitionId id,
        string numeratorKey,
        string denominatorKey) => new()
    {
        Id = id,
        SupportedScopes = BothScopes(),
        Operands =
        [
            Component("numerator", numeratorKey),
            Component("denominator", denominatorKey),
        ],
        ResultUnit = OperationalMetricUnits.Ratio,
        Formula = new OperationalMetricFormula.Ratio("numerator", "denominator"),
        DomainConstraints = new OperationalMetricDomainConstraints { MinimumInclusive = 0m },
        PrecisionPolicy = Precision(),
    };

    private static OperationalMetricOperandDefinition Component(string name, string key) => new()
    {
        OperandName = name,
        Source = new OperationalMetricOperandSource.Component(key),
        RequiredDimension = MetricDimension.Duration,
        RequiredUnit = MetricInputFactUnits.Seconds,
    };

    private static OperationalMetricOperandDefinition Evaluated(
        string name,
        OperationalMetricDefinitionId id) => new()
    {
        OperandName = name,
        Source = new OperationalMetricOperandSource.EvaluatedMetric(id),
        RequiredDimension = MetricDimension.Ratio,
        RequiredUnit = OperationalMetricUnits.Ratio,
    };

    private static OperationalMetricScopeSet BothScopes() => new()
    {
        SupportsShift = true,
        SupportsProductionDay = true,
    };

    private static OperationalMetricPrecisionPolicy Precision() => new()
    {
        DurableDecimalScale = 8,
        RoundingMode = MidpointRounding.ToEven,
    };

    private sealed class MalformedCatalog : IOperationalMetricDefinitionCatalog
    {
        private readonly Dictionary<OperationalMetricDefinitionId, OperationalMetricDefinition> _definitions;

        public MalformedCatalog(IEnumerable<OperationalMetricDefinition> definitions)
        {
            _definitions = definitions.ToDictionary(definition => definition.Id);
        }

        public OperationalMetricDefinition GetRequired(OperationalMetricDefinitionId definitionId) =>
            _definitions.TryGetValue(definitionId, out var definition)
                ? definition
                : throw new KeyNotFoundException();

        public IReadOnlyList<OperationalMetricDefinition> GetEvaluationOrder(OperationalMetricEvaluationScope scope) =>
            throw new NotSupportedException();
    }

    private sealed class WrongProcessorSnapshotReader : IOperationalMetricComponentSnapshotReader
    {
        private readonly MetricAggregationProcessorId _processorId;

        public WrongProcessorSnapshotReader(MetricAggregationProcessorId processorId)
        {
            _processorId = processorId;
        }

        public ValueTask<OperationalMetricComponentSnapshot> ReadAsync(
            OperationalMetricComponentSnapshotRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = new MetricAggregationCheckpoint(
                _processorId,
                MetricInputStreamId.ForMachine(request.EvaluationKey.MachineId),
                new MetricInputPosition(1));
            return ValueTask.FromResult(
                new OperationalMetricComponentSnapshot(request.EvaluationKey, revision, []));
        }
    }
}
