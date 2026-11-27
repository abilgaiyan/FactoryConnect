using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SupervisorProductionCycleMatcherTests
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
    public void SelectsLatestExactContext()
    {
        var first = Cycle();
        var latest = first with { CycleRevisionId = Guid.NewGuid(), RevisionNumber = 2, IdealCycleSeconds = 420m };
        Assert.Equal(latest, SupervisorProductionCycleMatcher.Match(Entry(), new[] { first, latest }));
    }

    [Fact]
    public void NeverFallsBackToOtherContextOrUnspecifiedCycle()
    {
        var cycle = Cycle();
        Assert.Null(SupervisorProductionCycleMatcher.Match(Entry(), new[] {
            cycle with { PartNumber = "GEAR-002" },
            cycle with { Operation = "OP-20" },
            cycle with { ProductionOrder = null },
            cycle with { MachineId = Guid.NewGuid() },
            cycle with { ShiftScheduleAssignmentId = "other" },
            cycle with { ShiftId = "other" },
            cycle with { ShiftEndsAtUtc = Start.AddHours(7) }
        }));
    }

    [Fact]
    public void AmbiguousHeadIsNotMatched()
    {
        var cycle = Cycle();
        Assert.Null(SupervisorProductionCycleMatcher.Match(Entry(), new[] {
            cycle, cycle with { CycleRevisionId = Guid.NewGuid() }
        }));
    }
}
