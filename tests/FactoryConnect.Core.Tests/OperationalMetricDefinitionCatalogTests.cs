using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricDefinitionCatalogTests
{
    [Fact]
    public void DefinitionIdentityIncludesVersion()
    {
        var first = new OperationalMetricDefinitionId("availability", "1.0");
        var second = new OperationalMetricDefinitionId("availability", "2.0");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void BuiltInCatalogResolvesExactOeeDependencyVersions()
    {
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var oee = catalog.GetRequired(BuiltInOperationalMetricDefinitions.OeeId);

        var dependencies = oee.Operands
            .Select(operand => operand.Source)
            .OfType<OperationalMetricOperandSource.EvaluatedMetric>()
            .Select(source => source.DefinitionId)
            .ToArray();

        Assert.Equal(
            [
                BuiltInOperationalMetricDefinitions.AvailabilityId,
                BuiltInOperationalMetricDefinitions.PerformanceId,
                BuiltInOperationalMetricDefinitions.QualityId,
            ],
            dependencies);
    }

    [Fact]
    public void RegisteringAnotherAvailabilityVersionDoesNotRetargetOee()
    {
        var availabilityTwo = Clone(
            BuiltInOperationalMetricDefinitions.All.Single(
                definition => definition.Id == BuiltInOperationalMetricDefinitions.AvailabilityId),
            new OperationalMetricDefinitionId(CanonicalMetricKeys.Availability, "2.0"));

        var catalog = new OperationalMetricDefinitionCatalog(
            [.. BuiltInOperationalMetricDefinitions.All, availabilityTwo]);
        var oee = catalog.GetRequired(BuiltInOperationalMetricDefinitions.OeeId);

        var availabilityDependency = Assert.IsType<OperationalMetricOperandSource.EvaluatedMetric>(
            oee.Operands.Single(operand => operand.OperandName == "Availability").Source);

        Assert.Equal(BuiltInOperationalMetricDefinitions.AvailabilityId, availabilityDependency.DefinitionId);
        Assert.NotEqual(availabilityTwo.Id, availabilityDependency.DefinitionId);
    }

    [Fact]
    public void IdenticalDuplicateRegistrationIsIdempotent()
    {
        var definition = BuiltInOperationalMetricDefinitions.All[0];
        var catalog = new OperationalMetricDefinitionCatalog([definition, definition]);

        Assert.Single(catalog.GetEvaluationOrder(OperationalMetricEvaluationScope.Shift));
    }

    [Fact]
    public void SeparatelyConstructedEquivalentProductDefinitionsAreIdempotent()
    {
        var definitions = BuiltInOperationalMetricDefinitions.All.ToList();
        var oee = definitions.Single(definition => definition.Id == BuiltInOperationalMetricDefinitions.OeeId);
        var equivalent = oee with
        {
            Operands = oee.Operands.Select(operand => operand with { }).ToArray(),
            Formula = new OperationalMetricFormula.Product(["Availability", "Performance", "Quality"]),
        };
        definitions.Add(equivalent);

        var catalog = new OperationalMetricDefinitionCatalog(definitions);

        Assert.Same(
            catalog.GetRequired(BuiltInOperationalMetricDefinitions.OeeId),
            catalog.GetEvaluationOrder(OperationalMetricEvaluationScope.Shift)
                .Single(definition => definition.Id == BuiltInOperationalMetricDefinitions.OeeId));
    }

    [Fact]
    public void ChangedProductFactorSequenceIsConflicting()
    {
        var oee = BuiltInOperationalMetricDefinitions.All.Single(
            definition => definition.Id == BuiltInOperationalMetricDefinitions.OeeId);
        var changed = oee with
        {
            Formula = new OperationalMetricFormula.Product(["Performance", "Availability", "Quality"]),
        };

        Assert.Throws<ArgumentException>(() =>
            new OperationalMetricDefinitionCatalog([.. BuiltInOperationalMetricDefinitions.All, changed]));
    }

    [Fact]
    public void OperandDeclarationOrderIsNotSemantic()
    {
        var definition = BuiltInOperationalMetricDefinitions.All[0];
        var reordered = definition with { Operands = definition.Operands.Reverse().ToArray() };

        Assert.True(OperationalMetricDefinitionSemanticComparer.AreEquivalent(definition, reordered));

        _ = new OperationalMetricDefinitionCatalog([definition, reordered]);
    }

    [Fact]
    public void ChangedOperandSemanticsAreConflicting()
    {
        var definition = BuiltInOperationalMetricDefinitions.All[0];
        var changedOperands = definition.Operands
            .Select(operand => operand.OperandName == "ActualProductionTime"
                ? operand with
                {
                    Source = new OperationalMetricOperandSource.Component(MetricInputKeys.MachinePowerOnTime),
                }
                : operand)
            .ToArray();
        var changed = definition with { Operands = changedOperands };

        Assert.Throws<ArgumentException>(() =>
            new OperationalMetricDefinitionCatalog([definition, changed]));
    }

    [Fact]
    public void DisplayNameDifferenceIsSemanticallyEquivalent()
    {
        var definition = BuiltInOperationalMetricDefinitions.All[0];
        var renamed = definition with { DisplayName = "Different descriptive label" };

        Assert.True(OperationalMetricDefinitionSemanticComparer.AreEquivalent(definition, renamed));
    }

    [Fact]
    public void ConflictingDefinitionWithSameIdentityIsRejected()
    {
        var definition = BuiltInOperationalMetricDefinitions.All[0];
        var conflicting = definition with
        {
            DomainConstraints = new OperationalMetricDomainConstraints
            {
                MinimumInclusive = 0m,
                MaximumInclusive = 2m,
            },
        };

        Assert.Throws<ArgumentException>(() =>
            new OperationalMetricDefinitionCatalog([definition, conflicting]));
    }

    [Fact]
    public void CatalogDefensivelyFreezesCallerOwnedCollections()
    {
        var sourceOee = BuiltInOperationalMetricDefinitions.All.Single(
            definition => definition.Id == BuiltInOperationalMetricDefinitions.OeeId);
        var mutableOperands = sourceOee.Operands.Select(operand => operand with { }).ToList();
        var mutableFactors = new List<string> { "Availability", "Performance", "Quality" };
        var mutableOee = sourceOee with
        {
            Operands = mutableOperands,
            Formula = new OperationalMetricFormula.Product(mutableFactors),
        };
        var definitions = BuiltInOperationalMetricDefinitions.All
            .Where(definition => definition.Id != BuiltInOperationalMetricDefinitions.OeeId)
            .Append(mutableOee)
            .ToList();

        var catalog = new OperationalMetricDefinitionCatalog(definitions);
        var expectedOrder = catalog.GetEvaluationOrder(OperationalMetricEvaluationScope.Shift)
            .Select(definition => definition.Id)
            .ToArray();

        mutableOperands.Clear();
        mutableFactors.Clear();
        definitions.Clear();

        var storedOee = catalog.GetRequired(BuiltInOperationalMetricDefinitions.OeeId);
        var storedProduct = Assert.IsType<OperationalMetricFormula.Product>(storedOee.Formula);
        Assert.Equal(3, storedOee.Operands.Count);
        Assert.Equal(["Availability", "Performance", "Quality"], storedProduct.FactorOperands);
        Assert.Equal(
            expectedOrder,
            catalog.GetEvaluationOrder(OperationalMetricEvaluationScope.Shift)
                .Select(definition => definition.Id)
                .ToArray());
    }

    [Fact]
    public void MissingExactDependencyVersionIsRejected()
    {
        var oee = BuiltInOperationalMetricDefinitions.All.Single(
            definition => definition.Id == BuiltInOperationalMetricDefinitions.OeeId);
        var changedOperands = oee.Operands
            .Select(operand => operand.OperandName == "Availability"
                ? operand with
                {
                    Source = new OperationalMetricOperandSource.EvaluatedMetric(
                        new OperationalMetricDefinitionId(CanonicalMetricKeys.Availability, "9.9")),
                }
                : operand)
            .ToArray();
        var changedOee = oee with { Operands = changedOperands };

        var definitions = BuiltInOperationalMetricDefinitions.All
            .Where(definition => definition.Id != BuiltInOperationalMetricDefinitions.OeeId)
            .Append(changedOee)
            .ToArray();

        Assert.Throws<ArgumentException>(() => new OperationalMetricDefinitionCatalog(definitions));
    }

    [Fact]
    public void DependencyCycleIsRejected()
    {
        var firstId = new OperationalMetricDefinitionId("a", "1.0");
        var secondId = new OperationalMetricDefinitionId("b", "1.0");
        var first = DependentProduct(firstId, secondId, secondId);
        var second = DependentProduct(secondId, firstId, firstId);

        Assert.Throws<ArgumentException>(() =>
            new OperationalMetricDefinitionCatalog([first, second]));
    }

    [Fact]
    public void DependencyMustSupportEveryParentScope()
    {
        var dependency = BuiltInOperationalMetricDefinitions.All.Single(
            definition => definition.Id == BuiltInOperationalMetricDefinitions.AvailabilityId) with
        {
            SupportedScopes = new OperationalMetricScopeSet
            {
                SupportsShift = true,
                SupportsProductionDay = false,
            },
        };
        var definitions = BuiltInOperationalMetricDefinitions.All
            .Where(definition => definition.Id != BuiltInOperationalMetricDefinitions.AvailabilityId)
            .Append(dependency)
            .ToArray();

        Assert.Throws<ArgumentException>(() => new OperationalMetricDefinitionCatalog(definitions));
    }

    [Fact]
    public void EvaluationOrderIsDeterministicAndPlacesDependenciesBeforeOee()
    {
        var catalog = new OperationalMetricDefinitionCatalog(
            BuiltInOperationalMetricDefinitions.All.Reverse());

        var order = catalog.GetEvaluationOrder(OperationalMetricEvaluationScope.Shift);
        var ids = order.Select(definition => definition.Id).ToArray();

        Assert.True(Array.IndexOf(ids, BuiltInOperationalMetricDefinitions.AvailabilityId) < Array.IndexOf(ids, BuiltInOperationalMetricDefinitions.OeeId));
        Assert.True(Array.IndexOf(ids, BuiltInOperationalMetricDefinitions.PerformanceId) < Array.IndexOf(ids, BuiltInOperationalMetricDefinitions.OeeId));
        Assert.True(Array.IndexOf(ids, BuiltInOperationalMetricDefinitions.QualityId) < Array.IndexOf(ids, BuiltInOperationalMetricDefinitions.OeeId));

        var secondCatalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        Assert.Equal(
            ids,
            secondCatalog.GetEvaluationOrder(OperationalMetricEvaluationScope.Shift)
                .Select(definition => definition.Id)
                .ToArray());
    }

    [Fact]
    public void RatioWithIncompatibleDimensionsIsRejected()
    {
        var definition = BuiltInOperationalMetricDefinitions.All[0];
        var operands = definition.Operands.ToArray();
        operands[1] = operands[1] with
        {
            RequiredDimension = MetricDimension.Quantity,
            RequiredUnit = MetricInputFactUnits.Count,
        };

        Assert.Throws<ArgumentException>(() =>
            OperationalMetricDefinitionValidator.Validate(definition with { Operands = operands }));
    }

    [Fact]
    public void ProductWithDuplicateFactorsIsRejected()
    {
        var oee = BuiltInOperationalMetricDefinitions.All.Single(
            definition => definition.Id == BuiltInOperationalMetricDefinitions.OeeId);
        var invalid = oee with
        {
            Formula = new OperationalMetricFormula.Product(["Availability", "Availability", "Quality"]),
        };

        Assert.Throws<ArgumentException>(() => OperationalMetricDefinitionValidator.Validate(invalid));
    }

    [Fact]
    public void InvalidPrecisionScaleIsRejected()
    {
        var definition = BuiltInOperationalMetricDefinitions.All[0] with
        {
            PrecisionPolicy = new OperationalMetricPrecisionPolicy
            {
                DurableDecimalScale = 29,
                RoundingMode = MidpointRounding.ToEven,
            },
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => OperationalMetricDefinitionValidator.Validate(definition));
    }

    [Fact]
    public void UndefinedRoundingModeIsRejected()
    {
        var definition = BuiltInOperationalMetricDefinitions.All[0] with
        {
            PrecisionPolicy = new OperationalMetricPrecisionPolicy
            {
                DurableDecimalScale = 8,
                RoundingMode = (MidpointRounding)999,
            },
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => OperationalMetricDefinitionValidator.Validate(definition));
    }

    [Fact]
    public void OperandNamesWithBoundaryWhitespaceAreRejected()
    {
        var definition = BuiltInOperationalMetricDefinitions.All[0];
        var operands = definition.Operands.ToArray();
        operands[0] = operands[0] with { OperandName = $" {operands[0].OperandName}" };

        Assert.Throws<ArgumentException>(() =>
            OperationalMetricDefinitionValidator.Validate(definition with { Operands = operands }));
    }

    [Fact]
    public void InvalidDomainBoundsAreRejected()
    {
        var definition = BuiltInOperationalMetricDefinitions.All[0] with
        {
            DomainConstraints = new OperationalMetricDomainConstraints
            {
                MinimumInclusive = 2m,
                MaximumInclusive = 1m,
            },
        };

        Assert.Throws<ArgumentException>(() => OperationalMetricDefinitionValidator.Validate(definition));
    }

    private static OperationalMetricDefinition Clone(
        OperationalMetricDefinition source,
        OperationalMetricDefinitionId id) => source with { Id = id };

    private static OperationalMetricDefinition DependentProduct(
        OperationalMetricDefinitionId id,
        OperationalMetricDefinitionId firstDependency,
        OperationalMetricDefinitionId secondDependency) => new()
    {
        Id = id,
        SupportedScopes = new OperationalMetricScopeSet
        {
            SupportsShift = true,
            SupportsProductionDay = false,
        },
        Operands =
        [
            Evaluated("First", firstDependency),
            Evaluated("Second", secondDependency),
        ],
        ResultUnit = OperationalMetricUnits.Ratio,
        Formula = new OperationalMetricFormula.Product(["First", "Second"]),
        DomainConstraints = new OperationalMetricDomainConstraints(),
        PrecisionPolicy = new OperationalMetricPrecisionPolicy
        {
            DurableDecimalScale = 8,
            RoundingMode = MidpointRounding.ToEven,
        },
    };

    private static OperationalMetricOperandDefinition Evaluated(
        string name,
        OperationalMetricDefinitionId dependencyId) => new()
    {
        OperandName = name,
        Source = new OperationalMetricOperandSource.EvaluatedMetric(dependencyId),
        RequiredDimension = MetricDimension.Ratio,
        RequiredUnit = OperationalMetricUnits.Ratio,
    };
}
