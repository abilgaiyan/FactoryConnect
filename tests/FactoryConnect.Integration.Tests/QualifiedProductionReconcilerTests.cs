using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class QualifiedProductionReconcilerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(8);
    private static readonly Guid Machine = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Evidence = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly ProductionReconciliationScope Scope =
        new(Machine, "assignment", "shift", Start, End, "pieces", "order", "part", "operation");
    private static readonly QualifiedProductionInput First =
        new(Evidence, "population-1", "authority-1", Machine, "assignment", "shift",
            Start, End, "pieces", "order", "part", "operation", 10, 8, 2, true);

    [Fact]
    public void DisjointQualifiedContributionsYieldCandidate()
    {
        var second = First with
        {
            EvidenceId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            PopulationKey = "population-2", Produced = 4, Good = 4, Rejected = 0
        };
        var result = QualifiedProductionReconciler.Reconcile(Scope, [First, second]);
        Assert.True(result.IsCandidate);
        Assert.Null(result.HeldReason);
        Assert.Equal(14, result.Produced);
        Assert.Equal(12, result.Good);
        Assert.Equal(2, result.Rejected);
        Assert.Equal(2, result.EvidenceIds.Count);
        Assert.Single(result.AuthorityReferences);
    }

    [Fact]
    public void OverlapAndDuplicateEvidenceAreHeld()
    {
        Assert.Equal("DuplicateEvidence",
            QualifiedProductionReconciler.Reconcile(Scope, [First, First]).HeldReason);
        Assert.Equal("PopulationOverlap", QualifiedProductionReconciler.Reconcile(Scope,
            [First, First with { EvidenceId = Guid.NewGuid() }]).HeldReason);
    }

    [Fact]
    public void UnqualifiedAndCrossContextEvidenceAreHeld()
    {
        Assert.Equal("QualificationMissing", QualifiedProductionReconciler.Reconcile(Scope,
            [First with { IsQualified = false }]).HeldReason);
        Assert.Equal("ProductionContextMismatch", QualifiedProductionReconciler.Reconcile(Scope,
            [First with { PartNumber = "other" }]).HeldReason);
        Assert.Equal("ScopeMismatch", QualifiedProductionReconciler.Reconcile(Scope,
            [First with { ShiftId = "other" }]).HeldReason);
    }

    [Fact]
    public void QuantityMismatchAndOverflowAreHeld()
    {
        Assert.Equal("QuantityInconsistent", QualifiedProductionReconciler.Reconcile(Scope,
            [First with { Good = 9 }]).HeldReason);
        Assert.Equal("QuantityOverflow", QualifiedProductionReconciler.Reconcile(Scope,
            [First with { Produced = long.MaxValue, Good = long.MaxValue, Rejected = 0 },
             First with { EvidenceId = Guid.NewGuid(), PopulationKey = "population-2",
                          Produced = 1, Good = 1, Rejected = 0 }]).HeldReason);
    }

    [Fact]
    public void NonUtcOffsetAndUnboundedProvenanceAreHeld()
    {
        Assert.Equal("ScopeMismatch", QualifiedProductionReconciler.Reconcile(Scope,
            [First with { StartsAtUtc = Start.ToOffset(TimeSpan.FromHours(5.5)) }]).HeldReason);
        Assert.Equal("ProvenanceMissing", QualifiedProductionReconciler.Reconcile(Scope,
            [First with { PopulationKey = new string('x', 201) }]).HeldReason);
    }

    [Fact]
    public void EmptyInputIsNotAcceptedAsZeroProduction()
    {
        var result = QualifiedProductionReconciler.Reconcile(Scope, []);
        Assert.False(result.IsCandidate);
        Assert.Equal("NoQualifiedContributions", result.HeldReason);
    }
}
