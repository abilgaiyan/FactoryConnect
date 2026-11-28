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

    private static SupervisorAcceptedIdealTimeResult Calculate(AcceptedShiftProductionRead? production,
        SupervisorShiftIdealCycleRevision[] cycles) =>
        SupervisorAcceptedIdealTime.Calculate(production, cycles,
            cycles.Select(c => new SupervisorCycleReview(Guid.NewGuid(), c.CycleRevisionId,
                SupervisorCycleReviewDecision.Accepted, "reviewer", Start.AddHours(1))), null, Start.AddHours(9));

    [Fact]
    public void UsesAcceptedQuantityAndRetainsBothIdentities()
    {
        var production = Production();
        var cycle = Cycle();
        var latest = cycle with { CycleRevisionId = Guid.NewGuid(), RevisionNumber = 2, IdealCycleSeconds = 240m };
        var result = Calculate(production, new[] { cycle, latest });
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
        Assert.Null(Calculate(null, new[] { cycle }).IdealProductionSeconds);
        Assert.Null(Calculate(production with { QuantityUnit = "kg" }, new[] { cycle }).IdealProductionSeconds);
        Assert.Null(Calculate(production with { Coverage = "Partial" }, new[] { cycle }).IdealProductionSeconds);
        Assert.Null(Calculate(production with { PartNumber = null }, new[] { cycle }).IdealProductionSeconds);
        Assert.Null(Calculate(production, new[] { cycle with { Operation = "OP-20" } }).IdealProductionSeconds);
        Assert.Null(Calculate(production, new[] { cycle, cycle with { CycleRevisionId = Guid.NewGuid() } }).IdealProductionSeconds);
    }
    [Fact]
    public void PendingCorrectionDoesNotOverrideAcceptedOrCapturedDefault()
    {
        var production = Production();
        var cycle = Cycle();
        var pending = cycle with { CycleRevisionId = Guid.NewGuid(), RevisionNumber = 2, IdealCycleSeconds = 240m };
        var review = new SupervisorCycleReview(Guid.NewGuid(), cycle.CycleRevisionId,
            SupervisorCycleReviewDecision.Accepted, "reviewer", Start.AddHours(1));
        var snapshot = new SupervisorCycleDefaultSnapshot(Guid.NewGuid(), Machine, "assignment", "shift",
            Start, Start.AddHours(8), "WO-1", "GEAR-001", "OP-10", 600m, 540m, Start);
        var result = SupervisorAcceptedIdealTime.Calculate(production, new[] { cycle, pending },
            new[] { review }, snapshot, Start.AddHours(9));
        Assert.Equal(54000m, result.IdealProductionSeconds);
        Assert.Equal(cycle.CycleRevisionId, result.CycleRevisionId);
        var fallback = SupervisorAcceptedIdealTime.Calculate(production, new[] { pending },
            Array.Empty<SupervisorCycleReview>(), snapshot, Start.AddHours(9));
        Assert.Equal(64800m, fallback.IdealProductionSeconds);
        Assert.Equal(snapshot.SnapshotId, fallback.DefaultSnapshotId);
        Assert.Equal("operation-default", fallback.CycleSource);
    }
}
