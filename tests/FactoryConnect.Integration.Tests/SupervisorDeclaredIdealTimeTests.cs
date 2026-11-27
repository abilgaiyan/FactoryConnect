using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SupervisorDeclaredIdealTimeTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Machine = Guid.NewGuid();
    private static SupervisorProductionEntryRecord Entry() =>
        new(Guid.NewGuid(), Machine, "assignment", "shift", Start, Start.AddHours(8),
            "WO-1", "GEAR-001", "OP-10", null, "supervisor", Start, 118, 2);
    private static SupervisorShiftIdealCycleRevision Cycle() =>
        new(Guid.NewGuid(), Machine, "assignment", "shift", Start, Start.AddHours(8),
            1, 450m, "WO-1", "GEAR-001", "OP-10", null, "supervisor", Start);

    [Fact]
    public void IncludesRejectedPiecesAndRetainsRevisionIdentity()
    {
        var cycle = Cycle();
        var result = SupervisorDeclaredIdealTime.Calculate(Entry(), new[] { cycle });
        Assert.Equal(120m, result.DeclaredTotalQuantity);
        Assert.Equal(54000m, result.IdealProductionSeconds);
        Assert.Equal(cycle.CycleRevisionId, result.CycleRevisionId);
        Assert.Equal("declared-only", result.Status);
    }

    [Fact]
    public void MissingQuantityOrCycleWithholdsIdealTime()
    {
        var cycle = Cycle();
        Assert.Null(SupervisorDeclaredIdealTime.Calculate(
            Entry() with { RejectedQuantity = null }, new[] { cycle }).IdealProductionSeconds);
        Assert.Null(SupervisorDeclaredIdealTime.Calculate(
            Entry(), new[] { cycle with { PartNumber = "other" } }).IdealProductionSeconds);
    }

    [Fact]
    public void LargeCountsDoNotOverflowIntegerAddition()
    {
        var result = SupervisorDeclaredIdealTime.Calculate(
            Entry() with { GoodQuantity = long.MaxValue, RejectedQuantity = long.MaxValue },
            new[] { Cycle() });
        Assert.Equal(2m * long.MaxValue, result.DeclaredTotalQuantity);
    }
}
