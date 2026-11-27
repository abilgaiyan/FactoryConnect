using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SupervisorAcceptedIdealTimeTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Machine = Guid.NewGuid();
    private static AcceptedShiftProductionRead Production() =>
        new(Guid.NewGuid(), Machine, "assignment", "shift", Start, Start.AddHours(8),
            "WO-1", "GEAR-001", "OP-10", "pieces", 120, "Complete", "test-only", Start.AddHours(8));
    private static SupervisorShiftIdealCycleRevision Cycle() =>
        new(Guid.NewGuid(), Machine, "assignment", "shift", Start, Start.AddHours(8),
            1, 450m, "WO-1", "GEAR-001", "OP-10", null, "supervisor", Start);

    [Fact]
    public void UsesAcceptedQuantityAndRetainsBothIdentities()
    {
        var production = Production();
        var cycle = Cycle();
        var latest = cycle with { CycleRevisionId = Guid.NewGuid(), RevisionNumber = 2, IdealCycleSeconds = 240m };
        var result = SupervisorAcceptedIdealTime.Calculate(production, new[] { cycle, latest });
        Assert.Equal(28800m, result.IdealProductionSeconds);
        Assert.Equal(production.PublicationId, result.PublicationId);
        Assert.Equal(latest.CycleRevisionId, result.CycleRevisionId);
        Assert.Equal("accepted-quantity-preview", result.Status);
    }

    [Fact]
    public void WithholdsMissingAmbiguousOrInapplicableEvidence()
    {
        var production = Production();
        var cycle = Cycle();
        Assert.Null(SupervisorAcceptedIdealTime.Calculate(null, new[] { cycle }).IdealProductionSeconds);
        Assert.Null(SupervisorAcceptedIdealTime.Calculate(production with { QuantityUnit = "kg" }, new[] { cycle }).IdealProductionSeconds);
        Assert.Null(SupervisorAcceptedIdealTime.Calculate(production with { Coverage = "Partial" }, new[] { cycle }).IdealProductionSeconds);
        Assert.Null(SupervisorAcceptedIdealTime.Calculate(production with { PartNumber = null }, new[] { cycle }).IdealProductionSeconds);
        Assert.Null(SupervisorAcceptedIdealTime.Calculate(production, new[] { cycle with { Operation = "OP-20" } }).IdealProductionSeconds);
        Assert.Null(SupervisorAcceptedIdealTime.Calculate(production, new[] { cycle, cycle with { CycleRevisionId = Guid.NewGuid() } }).IdealProductionSeconds);
    }
}
