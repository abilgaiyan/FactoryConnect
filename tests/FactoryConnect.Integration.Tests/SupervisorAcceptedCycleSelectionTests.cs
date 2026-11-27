using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SupervisorAcceptedCycleSelectionTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
    private static SupervisorShiftIdealCycleRevision Cycle() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "assignment", "shift", Start, Start.AddHours(8),
            1, 600m, "WO-1", "GEAR-001", "OP-10", null, "supervisor", Start);
    private static SupervisorCycleReview Review(SupervisorShiftIdealCycleRevision r,
        SupervisorCycleReviewDecision decision = SupervisorCycleReviewDecision.Accepted) =>
        new(Guid.NewGuid(), r.CycleRevisionId, decision, "reviewer", Start.AddHours(1));

    [Fact]
    public void LatestAcceptedWinsOverPendingAndRejected()
    {
        var first = Cycle();
        var second = first with { CycleRevisionId = Guid.NewGuid(), RevisionNumber = 2, IdealCycleSeconds = 480m };
        var pending = second with { CycleRevisionId = Guid.NewGuid(), RevisionNumber = 3 };
        var rejected = second with { CycleRevisionId = Guid.NewGuid(), RevisionNumber = 4 };
        var other = second with { CycleRevisionId = Guid.NewGuid(), RevisionNumber = 5, Operation = "OP-20" };
        var reviews = new[] { Review(first), Review(second), Review(rejected, SupervisorCycleReviewDecision.Rejected), Review(other) };
        Assert.Equal(second, SupervisorAcceptedCycleSelection.Select(first,
            new[] { rejected, pending, first, second, other }, reviews, Start.AddHours(2)));
    }

    [Fact]
    public void HistoricalCutAndConflictingReviewsFailClosed()
    {
        var first = Cycle();
        var second = first with { CycleRevisionId = Guid.NewGuid(), RevisionNumber = 2 };
        var early = Review(first);
        var late = Review(second) with { ReviewedAtUtc = Start.AddHours(3) };
        Assert.Equal(first, SupervisorAcceptedCycleSelection.Select(first, new[] { first, second },
            new[] { early, late }, Start.AddHours(2)));
        Assert.Null(SupervisorAcceptedCycleSelection.Select(first, new[] { first }, Array.Empty<SupervisorCycleReview>(), Start.AddHours(2)));
        Assert.Null(SupervisorAcceptedCycleSelection.Select(first, new[] { first },
            new[] { early, Review(first, SupervisorCycleReviewDecision.Rejected) }, Start.AddHours(2)));
    }
}
