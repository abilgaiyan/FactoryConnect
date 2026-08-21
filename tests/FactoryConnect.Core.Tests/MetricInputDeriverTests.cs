using FactoryConnect.Abstractions;
using FactoryConnect.Core.Metrics;
using Xunit;

namespace FactoryConnect.Core.Tests;

public sealed class MetricInputDeriverTests
{
    [Fact]
    public void DeriveBuildsInputsFromActivityScheduleAndProduction()
    {
        var companyId = new CompanyId("COMP-1");
        var siteId = new SiteId("SITE-1");
        var machineId = MachineId.New();
        var shiftId = new ShiftId("SHIFT-1");
        var date = new DateOnly(2026, 8, 21);
        var start = new DateTimeOffset(2026, 8, 21, 6, 0, 0, TimeSpan.Zero);

        var request = CreateRequest(
            companyId,
            siteId,
            machineId,
            shiftId,
            date,
            [
                new MachineActivityPeriod(machineId, MachineState.Running, start, start.AddHours(2)),
                new MachineActivityPeriod(machineId, MachineState.Idle, start.AddHours(2), start.AddHours(3)),
                new MachineActivityPeriod(machineId, MachineState.Running, start.AddHours(3), start.AddHours(6)),
            ],
            [
                CreateEntry(companyId, siteId, machineId, shiftId, date, 60, 2),
                CreateEntry(companyId, siteId, machineId, shiftId, date, 40, 3),
            ]);

        var result = MetricInputDeriver.Derive(request);

        Assert.Equal(5m, result.Inputs[MetricInputKeys.ActualProductionTime]);
        Assert.Equal(8m, result.Inputs[MetricInputKeys.PlannedOperatingTime]);
        Assert.Equal(100m, result.Inputs[MetricInputKeys.ProducedQuantity]);
        Assert.Equal(95m, result.Inputs[MetricInputKeys.GoodQuantity]);
        Assert.False(result.Inputs.ContainsKey(MetricInputKeys.ProductionReferenceTime));
    }

    [Fact]
    public void DeriveUsesOnlyRunningPeriodsForActualProductionTime()
    {
        var request = CreateRequestWithSingleMachine(
            [MachineState.Running, MachineState.Idle, MachineState.Fault]);

        var result = MetricInputDeriver.Derive(request);

        Assert.Equal(1m, result.Inputs[MetricInputKeys.ActualProductionTime]);
    }

    [Fact]
    public void DeriveRejectsProductionEntriesOutsideRequestedScope()
    {
        var companyId = new CompanyId("COMP-1");
        var siteId = new SiteId("SITE-1");
        var machineId = MachineId.New();
        var shiftId = new ShiftId("SHIFT-1");
        var date = new DateOnly(2026, 8, 21);
        var request = CreateRequest(
            companyId,
            siteId,
            machineId,
            shiftId,
            date,
            [],
            [CreateEntry(companyId, siteId, MachineId.New(), shiftId, date, 10, 0)]);

        Assert.Throws<ArgumentException>(
            () => MetricInputDeriver.Derive(request));
    }

    [Fact]
    public void DeriveReturnsZeroQuantitiesWhenNoProductionEntriesExist()
    {
        var request = CreateRequestWithSingleMachine([]);

        var result = MetricInputDeriver.Derive(request);

        Assert.Equal(0m, result.Inputs[MetricInputKeys.ProducedQuantity]);
        Assert.Equal(0m, result.Inputs[MetricInputKeys.GoodQuantity]);
    }

    private static MetricInputDerivationRequest CreateRequestWithSingleMachine(
        IReadOnlyCollection<MachineState> states)
    {
        var companyId = new CompanyId("COMP-1");
        var siteId = new SiteId("SITE-1");
        var machineId = MachineId.New();
        var shiftId = new ShiftId("SHIFT-1");
        var date = new DateOnly(2026, 8, 21);
        var start = new DateTimeOffset(2026, 8, 21, 6, 0, 0, TimeSpan.Zero);
        var periods = states
            .Select((state, index) =>
                new MachineActivityPeriod(
                    machineId,
                    state,
                    start.AddHours(index),
                    start.AddHours(index + 1)))
            .ToArray();

        return CreateRequest(
            companyId,
            siteId,
            machineId,
            shiftId,
            date,
            periods,
            []);
    }

    private static MetricInputDerivationRequest CreateRequest(
        CompanyId companyId,
        SiteId siteId,
        MachineId machineId,
        ShiftId shiftId,
        DateOnly date,
        IReadOnlyCollection<MachineActivityPeriod> periods,
        IReadOnlyCollection<ProductionEntry> entries) =>
        new()
        {
            CompanyId = companyId,
            SiteId = siteId,
            MachineId = machineId,
            ShiftId = shiftId,
            ProductionDate = date,
            ActivityPeriods = periods,
            Schedule = new ProductionSchedule
            {
                CompanyId = companyId,
                SiteId = siteId,
                MachineId = machineId,
                ShiftId = shiftId,
                ProductionDate = date,
                PlannedOperatingTime = TimeSpan.FromHours(8),
            },
            ProductionEntries = entries,
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
