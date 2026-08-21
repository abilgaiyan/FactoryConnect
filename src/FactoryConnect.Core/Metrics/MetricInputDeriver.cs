using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Metrics;

public sealed class MetricInputDeriver
{
    public MetricInputDerivationResult Derive(
        MetricInputDerivationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateScope(request);

        var actualProductionTime = request.ActivityPeriods
            .Where(period =>
                period.MachineId == request.MachineId &&
                period.State == MachineState.Running)
            .Aggregate(TimeSpan.Zero, (total, period) => total + period.Duration);

        var producedQuantity = request.ProductionEntries.Sum(
            entry => entry.ProducedQuantity);
        var goodQuantity = request.ProductionEntries.Sum(
            entry => entry.GoodQuantity);

        var inputs = new Dictionary<string, decimal>(
            StringComparer.OrdinalIgnoreCase)
        {
            [MetricInputKeys.ActualProductionTime] = ToHours(actualProductionTime),
            [MetricInputKeys.PlannedOperatingTime] = ToHours(request.Schedule.PlannedOperatingTime),
            [MetricInputKeys.ProducedQuantity] = producedQuantity,
            [MetricInputKeys.GoodQuantity] = goodQuantity,
        };

        return new MetricInputDerivationResult
        {
            Inputs = inputs,
        };
    }

    private static void ValidateScope(
        MetricInputDerivationRequest request)
    {
        var schedule = request.Schedule;

        if (schedule.CompanyId != request.CompanyId ||
            schedule.SiteId != request.SiteId ||
            schedule.MachineId != request.MachineId ||
            schedule.ShiftId != request.ShiftId ||
            schedule.ProductionDate != request.ProductionDate)
        {
            throw new ArgumentException(
                "Production schedule does not match the metric derivation scope.",
                nameof(request));
        }

        if (request.ProductionEntries.Any(entry =>
                entry.CompanyId != request.CompanyId ||
                entry.SiteId != request.SiteId ||
                entry.MachineId != request.MachineId ||
                entry.ShiftId != request.ShiftId ||
                entry.ProductionDate != request.ProductionDate))
        {
            throw new ArgumentException(
                "Production entry does not match the metric derivation scope.",
                nameof(request));
        }
    }

    private static decimal ToHours(TimeSpan duration) =>
        (decimal)duration.TotalHours;
}
