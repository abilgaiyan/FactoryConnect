using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

public sealed class ProductionContextTests
{
    [Fact]
    public void ProductionScheduleCapturesPotPerMachineShiftAndDate()
    {
        var schedule = new ProductionSchedule
        {
            CompanyId = new CompanyId("COMP-1"),
            SiteId = new SiteId("SITE-1"),
            MachineId = MachineId.New(),
            ShiftId = new ShiftId("SHIFT-1"),
            ProductionDate = new DateOnly(2026, 8, 21),
            PlannedOperatingTime = TimeSpan.FromHours(7.5),
        };

        Assert.Equal(TimeSpan.FromHours(7.5), schedule.PlannedOperatingTime);
        Assert.Equal("SITE-1", schedule.SiteId.Value);
    }

    [Fact]
    public void ProductionEntryKeepsInProcessRejectionSeparate()
    {
        var entry = new ProductionEntry
        {
            CompanyId = new CompanyId("COMP-1"),
            SiteId = new SiteId("SITE-1"),
            MachineId = MachineId.New(),
            ShiftId = new ShiftId("SHIFT-1"),
            PartId = new PartId("PART-1"),
            ProductionDate = new DateOnly(2026, 8, 21),
            ProducedQuantity = 100,
            InProcessRejectedQuantity = 3,
            RecordedAt = DateTimeOffset.UtcNow,
        };

        Assert.Equal(100, entry.ProducedQuantity);
        Assert.Equal(3, entry.InProcessRejectedQuantity);
        Assert.Equal(97, entry.GoodQuantity);
    }

    [Fact]
    public void MachineOperatorAssignmentSupportsMidShiftChanges()
    {
        var assignment = new MachineOperatorAssignment
        {
            CompanyId = new CompanyId("COMP-1"),
            SiteId = new SiteId("SITE-1"),
            MachineId = MachineId.New(),
            ShiftId = new ShiftId("SHIFT-1"),
            OperatorId = new OperatorId("OP-1"),
            StartsAt = new DateTimeOffset(2026, 8, 21, 6, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero),
        };

        Assert.NotNull(assignment.EndsAt);
        Assert.True(assignment.EndsAt > assignment.StartsAt);
    }

    [Fact]
    public void ShiftDefinitionAllowsOvernightShiftBoundary()
    {
        var shift = new ShiftDefinition
        {
            Id = new ShiftId("SHIFT-3"),
            SiteId = new SiteId("SITE-1"),
            Name = "Night",
            StartsAt = new TimeOnly(22, 0),
            EndsAt = new TimeOnly(6, 0),
        };

        Assert.True(shift.EndsAt < shift.StartsAt);
    }
}
