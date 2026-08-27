namespace FactoryConnect.Abstractions;

public sealed record MetricAggregateContributionSet
{
    public MetricAggregateContributionSet(
        IReadOnlyList<ShiftMetricAggregateContribution> shiftContributions,
        IReadOnlyList<ProductionDayMetricAggregateContribution> productionDayContributions)
    {
        ArgumentNullException.ThrowIfNull(shiftContributions);
        ArgumentNullException.ThrowIfNull(productionDayContributions);

        if (shiftContributions.Any(static contribution => contribution is null))
        {
            throw new ArgumentException(
                "Shift contribution collection must not contain null items.",
                nameof(shiftContributions));
        }

        if (productionDayContributions.Any(static contribution => contribution is null))
        {
            throw new ArgumentException(
                "Production-day contribution collection must not contain null items.",
                nameof(productionDayContributions));
        }

        ShiftContributions = shiftContributions.ToArray();
        ProductionDayContributions = productionDayContributions.ToArray();
    }

    public IReadOnlyList<ShiftMetricAggregateContribution> ShiftContributions { get; }

    public IReadOnlyList<ProductionDayMetricAggregateContribution> ProductionDayContributions { get; }
}
