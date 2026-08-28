using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class BuiltInOperationalMetricDefinitionsImmutabilityTests
{
    [Fact]
    public void BuiltInDefinitionCollectionsCannotBeMutated()
    {
        var definitions = Assert.IsAssignableFrom<IList<OperationalMetricDefinition>>(
            BuiltInOperationalMetricDefinitions.All);
        Assert.True(definitions.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => definitions.Clear());

        foreach (var definition in BuiltInOperationalMetricDefinitions.All)
        {
            var operands = Assert.IsAssignableFrom<IList<OperationalMetricOperandDefinition>>(
                definition.Operands);
            Assert.True(operands.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => operands.Clear());
        }

        var oee = BuiltInOperationalMetricDefinitions.All.Single(
            definition => definition.Id == BuiltInOperationalMetricDefinitions.OeeId);
        var product = Assert.IsType<OperationalMetricFormula.Product>(oee.Formula);
        var factors = Assert.IsAssignableFrom<IList<string>>(product.FactorOperands);

        Assert.True(factors.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => factors.Clear());
        Assert.Equal(["Availability", "Performance", "Quality"], product.FactorOperands);
    }

    [Fact]
    public void BuiltInCatalogStillPreservesExactDependencyGraph()
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
}
