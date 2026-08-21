using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class MetricCalculationEngineTests
{
    [Fact]
    public void CalculateAvailabilityUsesAptOverPotStrategy()
    {
        var result = CreateEngine().Calculate(
            Context(
                CanonicalMetricKeys.Availability,
                (MetricInputKeys.ActualProductionTime, 360m),
                (MetricInputKeys.PlannedOperatingTime, 480m)),
            Policy(
                CanonicalMetricKeys.Availability,
                MetricStrategyKeys.AptOverPot));

        Assert.True(result.IsAvailable);
        Assert.Equal(0.75m, result.Value);
    }

    [Fact]
    public void CalculatePerformanceUsesReferenceTimeOverAptStrategy()
    {
        var result = CreateEngine().Calculate(
            Context(
                CanonicalMetricKeys.Performance,
                (MetricInputKeys.ProductionReferenceTime, 300m),
                (MetricInputKeys.ActualProductionTime, 360m)),
            Policy(
                CanonicalMetricKeys.Performance,
                MetricStrategyKeys.ReferenceTimeOverApt));

        Assert.True(result.IsAvailable);
        Assert.Equal(300m / 360m, result.Value);
    }

    [Fact]
    public void CalculateQualityUsesGoodOverProducedStrategy()
    {
        var result = CreateEngine().Calculate(
            Context(
                CanonicalMetricKeys.Quality,
                (MetricInputKeys.GoodQuantity, 98m),
                (MetricInputKeys.ProducedQuantity, 100m)),
            Policy(
                CanonicalMetricKeys.Quality,
                MetricStrategyKeys.GoodOverProduced));

        Assert.True(result.IsAvailable);
        Assert.Equal(0.98m, result.Value);
    }

    [Fact]
    public void CalculateOeeComposesAvailabilityPerformanceAndQuality()
    {
        var result = CreateEngine().Calculate(
            Context(
                CanonicalMetricKeys.Oee,
                (MetricInputKeys.Availability, 0.75m),
                (MetricInputKeys.Performance, 0.80m),
                (MetricInputKeys.Quality, 0.95m)),
            Policy(
                CanonicalMetricKeys.Oee,
                MetricStrategyKeys.AvailabilityPerformanceQuality));

        Assert.True(result.IsAvailable);
        Assert.Equal(0.57m, result.Value);
    }

    [Fact]
    public void CalculateReturnsUnavailableWhenRequiredInputIsMissing()
    {
        var result = CreateEngine().Calculate(
            Context(
                CanonicalMetricKeys.Availability,
                (MetricInputKeys.ActualProductionTime, 360m)),
            Policy(
                CanonicalMetricKeys.Availability,
                MetricStrategyKeys.AptOverPot));

        Assert.False(result.IsAvailable);
        Assert.Null(result.Value);
        Assert.Contains(MetricInputKeys.PlannedOperatingTime, result.Reason);
    }

    [Fact]
    public void CalculateReturnsUnavailableWhenDenominatorIsZero()
    {
        var result = CreateEngine().Calculate(
            Context(
                CanonicalMetricKeys.Quality,
                (MetricInputKeys.GoodQuantity, 0m),
                (MetricInputKeys.ProducedQuantity, 0m)),
            Policy(
                CanonicalMetricKeys.Quality,
                MetricStrategyKeys.GoodOverProduced));

        Assert.False(result.IsAvailable);
        Assert.Null(result.Value);
    }

    [Fact]
    public void CalculateReturnsUnavailableWhenStrategyIsNotRegistered()
    {
        var result = CreateEngine().Calculate(
            Context(CanonicalMetricKeys.Availability),
            Policy(CanonicalMetricKeys.Availability, "custom-strategy"));

        Assert.False(result.IsAvailable);
        Assert.Contains("custom-strategy", result.Reason);
    }

    private static MetricCalculationEngine CreateEngine() =>
        new(
        [
            new AptOverPotMetricStrategy(),
            new ReferenceTimeOverAptMetricStrategy(),
            new GoodOverProducedMetricStrategy(),
            new AvailabilityPerformanceQualityMetricStrategy(),
        ]);

    private static MetricCalculationContext Context(
        string metricKey,
        params (string Key, decimal Value)[] inputs) =>
        new()
        {
            MetricKey = metricKey,
            Inputs = inputs.ToDictionary(
                input => input.Key,
                input => input.Value,
                StringComparer.OrdinalIgnoreCase),
        };

    private static MetricPolicyDefinition Policy(
        string metricKey,
        string strategyKey) =>
        new()
        {
            MetricKey = metricKey,
            StrategyKey = strategyKey,
        };
}
