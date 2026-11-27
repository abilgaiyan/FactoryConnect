namespace FactoryConnect.Persistence.SqlServer;

public sealed record SupervisorAcceptedIdealTimeResult(
    Guid? PublicationId, Guid? CycleRevisionId, decimal? IdealProductionSeconds, string Status);

/// <summary>Diagnostic only: a planning cycle is not qualified reference-time authority.</summary>
public static class SupervisorAcceptedIdealTime
{
    public static SupervisorAcceptedIdealTimeResult Calculate(
        AcceptedShiftProductionRead? production,
        IEnumerable<SupervisorShiftIdealCycleRevision> revisions)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        if (production is null)
            return new(null, null, null, "publication-unavailable");
        if (production.PublicationId == Guid.Empty || production.Coverage != "Complete" ||
            production.QuantityUnit != "pieces" || production.AcceptedProducedQuantity < 0 ||
            string.IsNullOrWhiteSpace(production.AuthorityReference) ||
            string.IsNullOrWhiteSpace(production.ProductionOrder) ||
            string.IsNullOrWhiteSpace(production.PartNumber) ||
            string.IsNullOrWhiteSpace(production.Operation))
            return new(production.PublicationId, null, null, "publication-inapplicable");

        var candidates = revisions.Where(r =>
            r.MachineId == production.MachineId &&
            r.ShiftScheduleAssignmentId == production.ShiftScheduleAssignmentId &&
            r.ShiftId == production.ShiftId &&
            r.ShiftStartsAtUtc == production.ShiftStartsAtUtc &&
            r.ShiftEndsAtUtc == production.ShiftEndsAtUtc &&
            r.ProductionOrder == production.ProductionOrder &&
            r.PartNumber == production.PartNumber &&
            r.Operation == production.Operation).ToArray();
        if (candidates.Length == 0)
            return new(production.PublicationId, null, null, "cycle-unmatched");
        var head = candidates.Max(r => r.RevisionNumber);
        var matches = candidates.Where(r => r.RevisionNumber == head).ToArray();
        if (matches.Length != 1 || head <= 0 || matches[0].CycleRevisionId == Guid.Empty ||
            matches[0].IdealCycleSeconds <= 0 || matches[0].IdealCycleSeconds > 86400m)
            return new(production.PublicationId, null, null, "cycle-unmatched");
        return new(production.PublicationId, matches[0].CycleRevisionId,
            production.AcceptedProducedQuantity * matches[0].IdealCycleSeconds,
            "accepted-quantity-preview");
    }
}
