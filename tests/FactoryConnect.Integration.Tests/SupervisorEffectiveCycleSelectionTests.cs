using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SupervisorEffectiveCycleSelectionTests
{
    [Fact]
    public void DefaultsAndAcceptedOverrideRespectContextAndReviewCut()
    {
        var start = new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
        var cycle = new SupervisorShiftIdealCycleRevision(Guid.NewGuid(), Guid.NewGuid(), "assignment", "shift",
            start, start.AddHours(8), 1, 420m, "WO-1", "GEAR-001", "OP-10", null, "supervisor", start);
        var snapshot = new SupervisorCycleDefaultSnapshot(Guid.NewGuid(), cycle.MachineId, "assignment", "shift",
            start, start.AddHours(8), "WO-1", "GEAR-001", "OP-10", 600m, 450m, start);
        var review = new SupervisorCycleReview(Guid.NewGuid(), cycle.CycleRevisionId,
            SupervisorCycleReviewDecision.Accepted, "reviewer", start.AddHours(1));
        SupervisorEffectiveCycleResult Resolve(SupervisorCycleDefaultSnapshot? value, params SupervisorCycleReview[] reviews)
            => SupervisorEffectiveCycleSelection.Select(cycle, new[] { cycle }, reviews, value, start.AddHours(2));
        Assert.Equal(450m, Resolve(snapshot).Seconds);
        Assert.Equal(600m, Resolve(snapshot with { OperationSeconds = null }).Seconds);
        Assert.Equal(420m, Resolve(snapshot, review).Seconds);
        Assert.Equal(450m, Resolve(snapshot, review with { Decision = SupervisorCycleReviewDecision.Rejected }).Seconds);
        Assert.Equal(450m, Resolve(snapshot, review with { ReviewedAtUtc = start.AddHours(3) }).Seconds);
        Assert.Null(Resolve(null).Seconds);
        Assert.Null(Resolve(snapshot with { Operation = "OP-20" }).Seconds);
        Assert.Null(Resolve(snapshot, review, review with { ReviewId = Guid.NewGuid() }).Seconds);
    }
}
