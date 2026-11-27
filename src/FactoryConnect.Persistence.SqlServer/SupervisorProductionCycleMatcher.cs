namespace FactoryConnect.Persistence.SqlServer;

/// <summary>Diagnostic matching only; does not qualify production or publish reference time.</summary>
public static class SupervisorProductionCycleMatcher
{
    public static SupervisorShiftIdealCycleRevision? Match(
        SupervisorProductionEntryRecord entry,
        IEnumerable<SupervisorShiftIdealCycleRevision> revisions)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(revisions);
        if (string.IsNullOrWhiteSpace(entry.ProductionOrder) ||
            string.IsNullOrWhiteSpace(entry.PartNumber) ||
            string.IsNullOrWhiteSpace(entry.Operation))
            return null;
        var candidates = revisions.Where(r =>
            r.MachineId == entry.MachineId &&
            r.ShiftScheduleAssignmentId == entry.ShiftScheduleAssignmentId &&
            r.ShiftId == entry.ShiftId &&
            r.ShiftStartsAtUtc == entry.ShiftStartsAtUtc &&
            r.ShiftEndsAtUtc == entry.ShiftEndsAtUtc &&
            r.ProductionOrder == entry.ProductionOrder &&
            r.PartNumber == entry.PartNumber &&
            r.Operation == entry.Operation).ToArray();
        if (candidates.Length == 0) return null;
        var head = candidates.Max(r => r.RevisionNumber);
        var matches = candidates.Where(r => r.RevisionNumber == head).ToArray();
        return matches.Length == 1 && head > 0 &&
            matches[0].IdealCycleSeconds > 0 && matches[0].IdealCycleSeconds <= 86400m
            ? matches[0] : null;
    }
}
