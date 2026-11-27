namespace FactoryConnect.Persistence.SqlServer;

public enum SupervisorCycleReviewDecision { Accepted, Rejected }

public sealed record SupervisorCycleReview(Guid ReviewId, Guid CycleRevisionId,
    SupervisorCycleReviewDecision Decision, string ReviewedBy, DateTimeOffset ReviewedAtUtc);

/// <summary>Selects an accepted revision for one exact context from a supplied review snapshot.
/// Does not publish standards or infer acceptance from a saved declaration.</summary>
public sealed record SupervisorCycleSelectionResult(SupervisorShiftIdealCycleRevision? Revision, bool InvalidEvidence);

public static class SupervisorAcceptedCycleSelection
{
    public static SupervisorShiftIdealCycleRevision? Select(
        SupervisorShiftIdealCycleRevision context,
        IEnumerable<SupervisorShiftIdealCycleRevision> revisions,
        IEnumerable<SupervisorCycleReview> reviews, DateTimeOffset asOfUtc)
        => Evaluate(context, revisions, reviews, asOfUtc).Revision;

    public static SupervisorCycleSelectionResult Evaluate(
        SupervisorShiftIdealCycleRevision context,
        IEnumerable<SupervisorShiftIdealCycleRevision> revisions,
        IEnumerable<SupervisorCycleReview> reviews, DateTimeOffset asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(revisions);
        ArgumentNullException.ThrowIfNull(reviews);
        if (asOfUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Review cut must be UTC.");
        var scoped = revisions.Where(r => r.MachineId == context.MachineId &&
            r.ShiftScheduleAssignmentId == context.ShiftScheduleAssignmentId &&
            r.ShiftId == context.ShiftId && r.ShiftStartsAtUtc == context.ShiftStartsAtUtc &&
            r.ShiftEndsAtUtc == context.ShiftEndsAtUtc && r.ProductionOrder == context.ProductionOrder &&
            r.PartNumber == context.PartNumber && r.Operation == context.Operation &&
            r.RecordedAtUtc <= asOfUtc).ToArray();
        if (scoped.GroupBy(r => r.CycleRevisionId).Any(g => g.Count() != 1) ||
            scoped.GroupBy(r => r.RevisionNumber).Any(g => g.Count() != 1)) return new(null, true);
        var ids = scoped.Select(r => r.CycleRevisionId).ToHashSet();
        var decisions = reviews.Where(r => ids.Contains(r.CycleRevisionId) && r.ReviewedAtUtc <= asOfUtc).ToArray();
        if (decisions.GroupBy(r => r.CycleRevisionId).Any(g => g.Count() != 1) ||
            decisions.GroupBy(r => r.ReviewId).Any(g => g.Count() != 1) ||
            decisions.Any(r => r.ReviewId == Guid.Empty || string.IsNullOrWhiteSpace(r.ReviewedBy) ||
                r.ReviewedAtUtc.Offset != TimeSpan.Zero || !Enum.IsDefined(r.Decision) ||
                r.ReviewedAtUtc < scoped.Single(c => c.CycleRevisionId == r.CycleRevisionId).RecordedAtUtc))
            return new(null, true);
        var accepted = decisions.Where(r => r.Decision == SupervisorCycleReviewDecision.Accepted)
            .Select(r => r.CycleRevisionId).ToHashSet();
        var head = scoped.Where(r => accepted.Contains(r.CycleRevisionId))
            .OrderByDescending(r => r.RevisionNumber).FirstOrDefault();
        if (head is null) return new(null, false);
        return head.CycleRevisionId != Guid.Empty && head.RevisionNumber > 0 &&
            head.IdealCycleSeconds > 0 && head.IdealCycleSeconds <= 86400m &&
            head.RecordedAtUtc.Offset == TimeSpan.Zero ? new(head, false) : new(null, true);
    }
}
