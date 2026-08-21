using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;

namespace FactoryConnect.Core.Tests;

public sealed class MachineShiftMetricEvaluatorTests
{
    [Fact]
    public void EvaluateComposesMachineShiftMetricsFromDerivedAndAdditionalInputs()
    {
        var request = CreateRequest(
            additionalInputs: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                [MetricInputKeys.ProductionReferenceTime] = 4m,
                [MetricInputKeys.MachinePowerOnTime] = 6m,
            },
            policies:
            [
                Policy(CanonicalMetricKeys.Availability, MetricStrategyKeys.AptOverPot),
                Policy(CanonicalMetricKeys.Performance, MetricStrategyKeys.ReferenceTimeOverApt),
                Policy(CanonicalMetricKeys.Quality, MetricStrategyKeys.GoodOverProduced),
                Policy(CanonicalMetricKeys.Oee, MetricStrategyKeys.AvailabilityPerformanceQuality),
                Policy(CanonicalMetricKeys.EquipmentLoadingRate, MetricStrategyKeys.AptOverPot),
                Policy(CanonicalMetricKeys.EffectiveLoadingRate, MetricStrategyKeys.AptOverMachinePowerOnTime),
            ]);

        var result = CreateEvaluator().Evaluate(request);

        Assert.Equal(request.CompanyId, result.CompanyId);
        Assert.Equal(request.SiteId, result.SiteId);
        Assert.Equal(request.MachineId, result.MachineId);
        Assert.Equal(request.ShiftId, result.ShiftId);
        Assert.Equal(request.ProductionDate, result.ProductionDate);

        Assert.Equal(5m, result.Inputs[MetricInputKeys.ActualProductionTime]);
        Assert.Equal(8m, result.Inputs[MetricInputKeys.PlannedOperatingTime]);
        Assert.Equal(100m, result.Inputs[MetricInputKeys.ProducedQuantity]);
        Assert.Equal(95m, result.Inputs[MetricInputKeys.GoodQuantity]);
        Assert.Equal(4m, result.Inputs[MetricInputKeys.ProductionReferenceTime]);
        Assert.Equal(6m, result.Inputs[MetricInputKeys.MachinePowerOnTime]);

        Assert.Equal(5m / 8m, result.Metrics[CanonicalMetricKeys.Availability].Value);
        Assert.Equal(4m / 5m, result.Metrics[CanonicalMetricKeys.Performance].Value);
        Assert.Equal(95m / 100m, result.Metrics[CanonicalMetricKeys.Quality].Value);
        Assert.Equal((5m / 8m) * (4m / 5m) * (95m / 100m), result.Metrics[CanonicalMetricKeys.Oee].Value);
        Assert.Equal(5m / 8m, result.Metrics[CanonicalMetricKeys.EquipmentLoadingRate].Value);
        Assert.Equal(5m / 6m, result.Metrics[CanonicalMetricKeys.EffectiveLoadingRate].Value);
    }

    [Fact]
    public void EvaluateMakesSuccessfulMetricOutputsAvailableToLaterMetrics()
    {
        var request = CreateRequest(
            additionalInputs: new Dictionary<string, decimal>
            {
                [MetricInputKeys.ProductionReferenceTime] = 4m,
            },
            policies:
            [
                Policy(CanonicalMetricKeys.Availability, MetricStrategyKeys.AptOverPot),
                Policy(CanonicalMetricKeys.Performance, MetricStrategyKeys.ReferenceTimeOverApt),
                Policy(CanonicalMetricKeys.Quality, MetricStrategyKeys.GoodOverProduced),
                Policy(CanonicalMetricKeys.Oee, MetricStrategyKeys.AvailabilityPerformanceQuality),
            ]);

        var result = CreateEvaluator().Evaluate(request);

        Assert.True(result.Metrics[CanonicalMetricKeys.Oee].IsAvailable);
        Assert.Equal((5m / 8m) * (4m / 5m) * (95m / 100m), result.Metrics[CanonicalMetricKeys.Oee].Value);
    }

    [Fact]
    public void EvaluatePreservesUnavailableMetricsWhenRequiredInputIsMissing()
    {
        var request = CreateRequest(
            policies:
            [
                Policy(CanonicalMetricKeys.Availability, MetricStrategyKeys.AptOverPot),
                Policy(CanonicalMetricKeys.Performance, MetricStrategyKeys.ReferenceTimeOverApt),
                Policy(CanonicalMetricKeys.Quality, MetricStrategyKeys.GoodOverProduced),
                Policy(CanonicalMetricKeys.Oee, MetricStrategyKeys.AvailabilityPerformanceQuality),
                Policy(CanonicalMetricKeys.EffectiveLoadingRate, MetricStrategyKeys.AptOverMachinePowerOnTime),
            ]);

        var result = CreateEvaluator().Evaluate(request);

        Assert.True(result.Metrics[CanonicalMetricKeys.Availability].IsAvailable);
        Assert.False(result.Metrics[CanonicalMetricKeys.Performance].IsAvailable);
        Assert.Contains(MetricInputKeys.ProductionReferenceTime, result.Metrics[CanonicalMetricKeys.Performance].Reason);
        Assert.True(result.Metrics[CanonicalMetricKeys.Quality].IsAvailable);
        Assert.False(result.Metrics[CanonicalMetricKeys.Oee].IsAvailable);
        Assert.Contains(MetricInputKeys.Performance, result.Metrics[CanonicalMetricKeys.Oee].Reason);
        Assert.False(result.Metrics[CanonicalMetricKeys.EffectiveLoadingRate].IsAvailable);
        Assert.Contains(MetricInputKeys.MachinePowerOnTime, result.Metrics[CanonicalMetricKeys.EffectiveLoadingRate].Reason);
    }

    [Fact]
    public void EvaluateRejectsAdditionalInputThatWouldReplaceDerivedFact()
    {
        var request = CreateRequest(
            additionalInputs: new Dictionary<string, decimal>
            {
                [MetricInputKeys.ActualProductionTime] = 99m,
            });

        var exception = Assert.Throws<ArgumentException>(
            () => CreateEvaluator().Evaluate(request));

        Assert.Contains(MetricInputKeys.ActualProductionTime, exception.Message);
    }

    [Fact]
    public void EvaluateRejectsDuplicateMetricPolicies()
    {
        var request = CreateRequest(
            policies:
            [
                Policy(CanonicalMetricKeys.Availability, MetricStrategyKeys.AptOverPot),
                Policy(CanonicalMetricKeys.Availability, MetricStrategyKeys.AptOverPot),
            ]);

        var exception = Assert.Throws<ArgumentException>(
            () => CreateEvaluator().Evaluate(request));

        Assert.Contains(CanonicalMetricKeys.Availability, exception.Message);
    }

    private static MachineShiftMetricEvaluator CreateEvaluator() =>
        new(
            new MetricCalculationEngine(
            [
                new AptOverPotMetricStrategy(),
                new AptOverMachinePowerOnTimeMetricStrategy(),
                new ReferenceTimeOverAptMetricStrategy(),
                new GoodOverProducedMetricStrategy(),
                new AvailabilityPerformanceQualityMetricStrategy(),
            ]));

    private static MachineShiftMetricEvaluationRequest CreateRequest(
        IReadOnlyDictionary<string, decimal>? additionalInputs = null,
        IReadOnlyList<MetricPolicyDefinition>? policies = null)
    {
        var companyId = new CompanyId("COMP-1");
        var siteId = new SiteId("SITE-1");
        var machineId = MachineId.New();
        var shiftId = new ShiftId("SHIFT-1");
        var date = new DateOnly(2026, 8, 21);
        var start = new DateTimeOffset(2026, 8, 21, 6, 0, 0, TimeSpan.Zero);

        return new MachineShiftMetricEvaluationRequest
        {
            CompanyId = companyId,
            SiteId = siteId,
            MachineId = machineId,
            ShiftId = shiftId,
            ProductionDate = date,
            ActivityPeriods =
            [
                new MachineActivityPeriod(machineId, MachineState.Running, start, start.AddHours(2)),
                new MachineActivityPeriod(machineId, MachineState.Idle, start.AddHours(2), start.AddHours(3)),
                new MachineActivityPeriod(machineId, MachineState.Running, start.AddHours(3), start.AddHours(6)),
            ],
            Schedule = new ProductionSchedule
            {
                CompanyId = companyId,
                SiteId = siteId,
                MachineId = machineId,
                ShiftId = shiftId,
                ProductionDate = date,
                PlannedOperatingTime = TimeSpan.FromHours(8),
            },
            ProductionEntries =
            [
                CreateEntry(companyId, siteId, machineId, shiftId, date, 60, 2),
                CreateEntry(companyId, siteId, machineId, shiftId, date, 40, 3),
            ],
            AdditionalInputs = additionalInputs ??
                new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase),
            MetricPolicies = policies ?? [],
        };
    }

    private static MetricPolicyDefinition Policy(
        string metricKey,
        string strategyKey) =>
        new()
        {
            MetricKey = metricKey,
            StrategyKey = strategyKey,
        };

    private static ProductionEntry CreateEntry(
        CompanyId companyId,
        SiteId siteId,
        MachineId machineId,
        ShiftId shiftId,
        DateOnly date,
        int producedQuantity,
        int rejectedQuantity) =>
        new()
        {
            CompanyId = companyId,
            SiteId = siteId,
            MachineId = machineId,
            ShiftId = shiftId,
            PartId = new PartId("PART-1"),
            ProductionDate = date,
            ProducedQuantity = producedQuantity,
            InProcessRejectedQuantity = rejectedQuantity,
            RecordedAt = DateTimeOffset.UtcNow,
        };
}
