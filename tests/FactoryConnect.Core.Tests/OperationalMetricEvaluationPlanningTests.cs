using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricEvaluationPlanningTests
{
    [Fact]
    public void OeePlanUsesExactVersionDependenciesAndCanonicalComponents()
    {
        var availabilityV2 = RatioDefinition(
            new OperationalMetricDefinitionId(CanonicalMetricKeys.Availability, "2.0"),
            "v2-numerator",
            MetricInputKeys.ActualProductionTime,
            "v2-denominator",
            MetricInputKeys.PlannedOperatingTime,
            MetricDimension.Duration,
            MetricInputFactUnits.Seconds);
        var catalog = new OperationalMetricDefinitionCatalog(
            BuiltInOperationalMetricDefinitions.All.Append(availabilityV2));
        var planner = new OperationalMetricEvaluationPlanner(catalog);

        var plan = planner.CreatePlan(OeeKey());

        Assert.Equal(BuiltInOperationalMetricDefinitions.OeeId, plan.RootDefinition.Id);
        Assert.Equal(
            [
                BuiltInOperationalMetricDefinitions.AvailabilityId,
                BuiltInOperationalMetricDefinitions.PerformanceId,
                BuiltInOperationalMetricDefinitions.QualityId,
                BuiltInOperationalMetricDefinitions.OeeId,
            ],
            plan.DependencyOrder.Select(definition => definition.Id));
        Assert.DoesNotContain(plan.DependencyOrder, definition => definition.Id == availabilityV2.Id);
        Assert.Equal(
            [
                MetricInputKeys.ActualProductionTime,
                MetricInputKeys.GoodQuantity,
                MetricInputKeys.ProductionReferenceTime,
                MetricInputKeys.PlannedOperatingTime,
                MetricInputKeys.ProducedQuantity,
            ],
            plan.ComponentRequirements.Select(requirement => requirement.ComponentKey));
        Assert.Equal(5, plan.ComponentRequirements.Count);
    }

    [Fact]
    public void PlanningIsStableRegardlessOfRegistrationOrder()
    {
        var definitions = BuiltInOperationalMetricDefinitions.All.ToArray();
        var forward = new OperationalMetricEvaluationPlanner(new OperationalMetricDefinitionCatalog(definitions));
        var reverse = new OperationalMetricEvaluationPlanner(new OperationalMetricDefinitionCatalog(definitions.Reverse()));

        var first = forward.CreatePlan(OeeKey());
        var second = reverse.CreatePlan(OeeKey());

        Assert.Equal(
            first.DependencyOrder.Select(definition => definition.Id),
            second.DependencyOrder.Select(definition => definition.Id));
        Assert.Equal(first.ComponentRequirements, second.ComponentRequirements);
    }

    [Fact]
    public void SameComponentWithIncompatibleTransitiveRequirementsFailsPlanning()
    {
        const string shared = "shared-component";
        var durationId = new OperationalMetricDefinitionId("duration-ratio", "1.0");
        var quantityId = new OperationalMetricDefinitionId("quantity-ratio", "1.0");
        var rootId = new OperationalMetricDefinitionId("root-product", "1.0");
        var duration = RatioDefinition(
            durationId,
            "left",
            shared,
            "right",
            "duration-other",
            MetricDimension.Duration,
            MetricInputFactUnits.Seconds);
        var quantity = RatioDefinition(
            quantityId,
            "left",
            shared,
            "right",
            "quantity-other",
            MetricDimension.Quantity,
            MetricInputFactUnits.Count);
        var root = ProductDefinition(rootId, durationId, quantityId);
        var planner = new OperationalMetricEvaluationPlanner(
            new OperationalMetricDefinitionCatalog([root, quantity, duration]));
        var key = EvaluationKey(rootId);

        Assert.Throws<InvalidDataException>(() => planner.CreatePlan(key));
    }

    [Fact]
    public async Task SessionCreationReadsExactlyOneCanonicalSnapshot()
    {
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var plan = new OperationalMetricEvaluationPlanner(catalog).CreatePlan(OeeKey());
        var processor = new MetricAggregationProcessorId("aggregate-m01");
        var reader = new RecordingSnapshotReader(processor);
        var factory = new OperationalMetricEvaluationSessionFactory(reader, processor);

        Assert.Equal(0, reader.ReadCount);

        var session = await factory.CreateAsync(plan, CancellationToken.None);

        Assert.Equal(1, reader.ReadCount);
        Assert.Same(plan, session.Plan);
        Assert.Equal(plan.RootKey, session.Snapshot.EvaluationKey);
        Assert.Equal(
            plan.ComponentRequirements.Select(requirement => requirement.ComponentKey),
            reader.LastRequest!.Operands.Select(operand =>
                Assert.IsType<OperationalMetricOperandSource.Component>(operand.Source).ComponentKey));
    }

    private static OperationalMetricEvaluationKey OeeKey() =>
        EvaluationKey(BuiltInOperationalMetricDefinitions.OeeId);

    private static OperationalMetricEvaluationKey EvaluationKey(OperationalMetricDefinitionId definitionId) => new(
        new MachineId(new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
        new OperationalMetricPeriodId.ProductionDay(
            new ProductionDayId(new SiteId("site-a"), new DateOnly(2026, 8, 28))),
        definitionId,
        OperationalMetricEvaluationContextKey.Unpartitioned);

    private static OperationalMetricDefinition RatioDefinition(
        OperationalMetricDefinitionId id,
        string numeratorName,
        string numeratorKey,
        string denominatorName,
        string denominatorKey,
        MetricDimension dimension,
        string unit) => new()
    {
        Id = id,
        SupportedScopes = BothScopes(),
        Operands =
        [
            new OperationalMetricOperandDefinition
            {
                OperandName = numeratorName,
                Source = new OperationalMetricOperandSource.Component(numeratorKey),
                RequiredDimension = dimension,
                RequiredUnit = unit,
            },
            new OperationalMetricOperandDefinition
            {
                OperandName = denominatorName,
                Source = new OperationalMetricOperandSource.Component(denominatorKey),
                RequiredDimension = dimension,
                RequiredUnit = unit,
            },
        ],
        ResultUnit = OperationalMetricUnits.Ratio,
        Formula = new OperationalMetricFormula.Ratio(numeratorName, denominatorName),
        DomainConstraints = new OperationalMetricDomainConstraints { MinimumInclusive = 0m },
        PrecisionPolicy = Precision(),
    };

    private static OperationalMetricDefinition ProductDefinition(
        OperationalMetricDefinitionId id,
        OperationalMetricDefinitionId left,
        OperationalMetricDefinitionId right) => new()
    {
        Id = id,
        SupportedScopes = BothScopes(),
        Operands =
        [
            Evaluated("left", left),
            Evaluated("right", right),
        ],
        ResultUnit = OperationalMetricUnits.Ratio,
        Formula = new OperationalMetricFormula.Product(["left", "right"]),
        DomainConstraints = new OperationalMetricDomainConstraints { MinimumInclusive = 0m },
        PrecisionPolicy = Precision(),
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

    private sealed class RecordingSnapshotReader : IOperationalMetricComponentSnapshotReader
    {
        private readonly MetricAggregationProcessorId _processorId;

        public RecordingSnapshotReader(MetricAggregationProcessorId processorId)
        {
            _processorId = processorId;
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
            var revision = new MetricAggregationCheckpoint(
                _processorId,
                MetricInputStreamId.ForMachine(request.EvaluationKey.MachineId),
                new MetricInputPosition(1));
            return ValueTask.FromResult(
                new OperationalMetricComponentSnapshot(request.EvaluationKey, revision, []));
        }
    }
}
