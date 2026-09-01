using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

public sealed class OperationalMetricUnpartitionedContextFilterTests
{
    [Fact]
    public void UnpartitionedOnlyMatchesOnlyCanonicalUnpartitionedContext()
    {
        var filter = new OperationalMetricContextFilter
        {
            UnpartitionedOnly = true,
        };
        var partitioned = new OperationalMetricEvaluationContextKey
        {
            ProductionOrderId = new ProductionOrderId("order-1"),
        };

        Assert.True(filter.Matches(OperationalMetricEvaluationContextKey.Unpartitioned));
        Assert.False(filter.Matches(partitioned));
    }

    [Fact]
    public void UnpartitionedOnlyCannotBeCombinedWithContextualIdentityFilters()
    {
        var filter = new OperationalMetricContextFilter
        {
            UnpartitionedOnly = true,
            PartId = new PartId("part-1"),
        };

        Assert.Throws<ArgumentException>(filter.Validate);
    }

    [Fact]
    public void ExistingEmptyAndPartialFiltersRetainWildcardSemantics()
    {
        var context = new OperationalMetricEvaluationContextKey
        {
            ProductionOrderId = new ProductionOrderId("order-1"),
            OperationId = new OperationId("operation-1"),
            PartId = new PartId("part-1"),
            OperatorId = new OperatorId("operator-1"),
        };

        Assert.True(new OperationalMetricContextFilter().Matches(context));
        Assert.True(new OperationalMetricContextFilter
        {
            PartId = new PartId("part-1"),
        }.Matches(context));
    }
}
