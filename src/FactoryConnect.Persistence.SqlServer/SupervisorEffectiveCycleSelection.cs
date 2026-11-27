namespace FactoryConnect.Persistence.SqlServer;

// A captured default is evidence for one exact production context, not a live master lookup.
public sealed record SupervisorCycleDefaultSnapshot(Guid SnapshotId, Guid MachineId,
    string ShiftScheduleAssignmentId, string ShiftId, DateTimeOffset ShiftStartsAtUtc,
    DateTimeOffset ShiftEndsAtUtc, string? ProductionOrder, string PartNumber, string? Operation,
    decimal PartSeconds, decimal? OperationSeconds, DateTimeOffset CapturedAtUtc);

public sealed record SupervisorEffectiveCycleResult(decimal? Seconds, string Source, Guid? EvidenceId);

public static class SupervisorEffectiveCycleSelection
{
    public static SupervisorEffectiveCycleResult Select(SupervisorShiftIdealCycleRevision context,
        IEnumerable<SupervisorShiftIdealCycleRevision> revisions, IEnumerable<SupervisorCycleReview> reviews,
        SupervisorCycleDefaultSnapshot? snapshot, DateTimeOffset asOfUtc)
    {
        var selection = SupervisorAcceptedCycleSelection.Evaluate(context, revisions, reviews, asOfUtc);
        if (selection.InvalidEvidence) return new(null, "invalid-review-evidence", null);
        if (selection.Revision is { } accepted)
            return new(accepted.IdealCycleSeconds, "accepted-override", accepted.CycleRevisionId);
        if (snapshot is null) return new(null, "default-unavailable", null);
        if (snapshot.SnapshotId == Guid.Empty || snapshot.MachineId != context.MachineId ||
            snapshot.ShiftScheduleAssignmentId != context.ShiftScheduleAssignmentId ||
            snapshot.ShiftId != context.ShiftId || snapshot.ShiftStartsAtUtc != context.ShiftStartsAtUtc ||
            snapshot.ShiftEndsAtUtc != context.ShiftEndsAtUtc || snapshot.ProductionOrder != context.ProductionOrder ||
            snapshot.PartNumber != context.PartNumber || snapshot.Operation != context.Operation ||
            string.IsNullOrWhiteSpace(snapshot.PartNumber) || snapshot.CapturedAtUtc.Offset != TimeSpan.Zero ||
            snapshot.CapturedAtUtc > asOfUtc || !Valid(snapshot.PartSeconds) ||
            (snapshot.OperationSeconds is decimal op && (!Valid(op) || string.IsNullOrWhiteSpace(snapshot.Operation))))
            return new(null, "invalid-default-evidence", null);
        return new(snapshot.OperationSeconds ?? snapshot.PartSeconds,
            snapshot.OperationSeconds.HasValue ? "operation-default" : "part-default", snapshot.SnapshotId);
    }

    private static bool Valid(decimal seconds) => seconds > 0 && seconds <= 86400m && decimal.Round(seconds, 3) == seconds;
}
