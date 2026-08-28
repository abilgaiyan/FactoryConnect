using System.Collections.ObjectModel;
using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public static class BuiltInOperationalMetricDefinitions
{
    public static OperationalMetricDefinitionId AvailabilityId { get; } =
        new(CanonicalMetricKeys.Availability, "1.0");

    public static OperationalMetricDefinitionId UtilizationId { get; } =
        new(CanonicalMetricKeys.EquipmentLoadingRate, "1.0");

    public static OperationalMetricDefinitionId PerformanceId { get; } =
        new(CanonicalMetricKeys.Performance, "1.0");

    public static OperationalMetricDefinitionId QualityId { get; } =
        new(CanonicalMetricKeys.Quality, "1.0");

    public static OperationalMetricDefinitionId OeeId { get; } =
        new(CanonicalMetricKeys.Oee, "1.0");

    public static IReadOnlyList<OperationalMetricDefinition> All { get; } =
        new ReadOnlyCollection<OperationalMetricDefinition>(
        [
            CreateAvailability(),
            CreateUtilization(),
            CreatePerformance(),
            CreateQuality(),
            CreateOee(),
        ]);

    private static OperationalMetricDefinition CreateAvailability() => new()
    {
        Id = AvailabilityId,
        DisplayName = "Availability",
        SupportedScopes = BothScopes(),
        Operands = ReadOnlyOperands(
            Component("ActualProductionTime", MetricInputKeys.ActualProductionTime, MetricDimension.Duration, MetricInputFactUnits.Seconds),
            Component("PlannedOperatingTime", MetricInputKeys.PlannedOperatingTime, MetricDimension.Duration, MetricInputFactUnits.Seconds)),
        ResultUnit = OperationalMetricUnits.Ratio,
        Formula = new OperationalMetricFormula.Ratio("ActualProductionTime", "PlannedOperatingTime"),
        DomainConstraints = BoundedRatio(),
        PrecisionPolicy = DefaultPrecision(),
    };

    private static OperationalMetricDefinition CreateUtilization() => new()
    {
        Id = UtilizationId,
        DisplayName = "Utilization",
        SupportedScopes = BothScopes(),
        Operands = ReadOnlyOperands(
            Component("ActualProductionTime", MetricInputKeys.ActualProductionTime, MetricDimension.Duration, MetricInputFactUnits.Seconds),
            Component("AvailableDuration", MetricInputKeys.MachinePowerOnTime, MetricDimension.Duration, MetricInputFactUnits.Seconds)),
        ResultUnit = OperationalMetricUnits.Ratio,
        Formula = new OperationalMetricFormula.Ratio("ActualProductionTime", "AvailableDuration"),
        DomainConstraints = NonNegative(),
        PrecisionPolicy = DefaultPrecision(),
    };

    private static OperationalMetricDefinition CreatePerformance() => new()
    {
        Id = PerformanceId,
        DisplayName = "Performance",
        SupportedScopes = BothScopes(),
        Operands = ReadOnlyOperands(
            Component("IdealProductionDuration", MetricInputKeys.ProductionReferenceTime, MetricDimension.Duration, MetricInputFactUnits.Seconds),
            Component("ActualProductionTime", MetricInputKeys.ActualProductionTime, MetricDimension.Duration, MetricInputFactUnits.Seconds)),
        ResultUnit = OperationalMetricUnits.Ratio,
        Formula = new OperationalMetricFormula.Ratio("IdealProductionDuration", "ActualProductionTime"),
        DomainConstraints = NonNegative(),
        PrecisionPolicy = DefaultPrecision(),
    };

    private static OperationalMetricDefinition CreateQuality() => new()
    {
        Id = QualityId,
        DisplayName = "Quality",
        SupportedScopes = BothScopes(),
        Operands = ReadOnlyOperands(
            Component("GoodQuantity", MetricInputKeys.GoodQuantity, MetricDimension.Quantity, MetricInputFactUnits.Count),
            Component("TotalQuantity", MetricInputKeys.ProducedQuantity, MetricDimension.Quantity, MetricInputFactUnits.Count)),
        ResultUnit = OperationalMetricUnits.Ratio,
        Formula = new OperationalMetricFormula.Ratio("GoodQuantity", "TotalQuantity"),
        DomainConstraints = BoundedRatio(),
        PrecisionPolicy = DefaultPrecision(),
    };

    private static OperationalMetricDefinition CreateOee() => new()
    {
        Id = OeeId,
        DisplayName = "OEE",
        SupportedScopes = BothScopes(),
        Operands = ReadOnlyOperands(
            Evaluated("Availability", AvailabilityId),
            Evaluated("Performance", PerformanceId),
            Evaluated("Quality", QualityId)),
        ResultUnit = OperationalMetricUnits.Ratio,
        Formula = new OperationalMetricFormula.Product(
            new ReadOnlyCollection<string>(["Availability", "Performance", "Quality"])),
        DomainConstraints = BoundedRatio(),
        PrecisionPolicy = DefaultPrecision(),
    };

    private static OperationalMetricOperandDefinition Component(
        string operandName,
        string componentKey,
        MetricDimension dimension,
        string unit) => new()
    {
        OperandName = operandName,
        Source = new OperationalMetricOperandSource.Component(componentKey),
        RequiredDimension = dimension,
        RequiredUnit = unit,
    };

    private static OperationalMetricOperandDefinition Evaluated(
        string operandName,
        OperationalMetricDefinitionId definitionId) => new()
    {
        OperandName = operandName,
        Source = new OperationalMetricOperandSource.EvaluatedMetric(definitionId),
        RequiredDimension = MetricDimension.Ratio,
        RequiredUnit = OperationalMetricUnits.Ratio,
    };

    private static ReadOnlyCollection<OperationalMetricOperandDefinition> ReadOnlyOperands(
        params OperationalMetricOperandDefinition[] operands) =>
        new(operands);

    private static OperationalMetricScopeSet BothScopes() => new()
    {
        SupportsShift = true,
        SupportsProductionDay = true,
    };

    private static OperationalMetricDomainConstraints BoundedRatio() => new()
    {
        MinimumInclusive = 0m,
        MaximumInclusive = 1m,
    };

    private static OperationalMetricDomainConstraints NonNegative() => new()
    {
        MinimumInclusive = 0m,
    };

    private static OperationalMetricPrecisionPolicy DefaultPrecision() => new()
    {
        DurableDecimalScale = 8,
        RoundingMode = MidpointRounding.ToEven,
    };
}
