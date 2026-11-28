namespace FactoryConnect.Persistence.SqlServer;

public sealed record SupervisorAcceptedIdealTimeResult(
    Guid? PublicationId, Guid? CycleRevisionId, decimal? IdealProductionSeconds, string Status, string? CycleSource = null, Guid? DefaultSnapshotId = null);

/// <summary>Diagnostic only: a planning cycle is not qualified reference-time authority.</summary>
public static class SupervisorAcceptedIdealTime
{
    public static SupervisorAcceptedIdealTimeResult Calculate(
        AcceptedShiftProductionRead? production,
        IEnumerable<SupervisorShiftIdealCycleRevision> revisions,
        IEnumerable<SupervisorCycleReview> reviews, SupervisorCycleDefaultSnapshot? snapshot, DateTimeOffset asOfUtc)
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

        var context = new SupervisorShiftIdealCycleRevision(Guid.Empty, production.MachineId,
            production.ShiftScheduleAssignmentId, production.ShiftId, production.ShiftStartsAtUtc,
            production.ShiftEndsAtUtc, 0, 0m, production.ProductionOrder, production.PartNumber,
            production.Operation, null, "", production.PublishedAtUtc);
        var effective = SupervisorEffectiveCycleSelection.Select(context, revisions, reviews, snapshot, asOfUtc);
        if (effective.Seconds is not decimal seconds)
            return new(production.PublicationId, null, null, "cycle-unmatched", effective.Source);
        return new(production.PublicationId,
            effective.Source == "accepted-override" ? effective.EvidenceId : null,
            production.AcceptedProducedQuantity * seconds, "accepted-quantity-preview", effective.Source,
            effective.Source == "accepted-override" ? null : effective.EvidenceId);
    }
}
