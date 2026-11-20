namespace FactoryConnect.Persistence.SqlServer;

/// <summary>
/// Server-side, pre-publication reconciliation. Qualified input is an explicit
/// prerequisite, not a claim that this component can independently authenticate.
/// No SQL writes or OEE calculation occur here.
/// </summary>
public sealed record QualifiedProductionInput(
    Guid EvidenceId, string PopulationKey, string AuthorityReference,
    Guid MachineId, string AssignmentId, string ShiftId,
    DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc,
    string QuantityUnit, string? ProductionOrder, string? PartNumber,
    string? Operation, long Produced, long Good, long Rejected,
    bool IsQualified);

public sealed record ProductionReconciliationScope(
    Guid MachineId, string AssignmentId, string ShiftId,
    DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc,
    string QuantityUnit, string? ProductionOrder, string? PartNumber,
    string? Operation);

public sealed record ProductionReconciliationResult(
    bool IsCandidate, string? HeldReason, long Produced, long Good, long Rejected,
    IReadOnlyList<Guid> EvidenceIds, IReadOnlyList<string> AuthorityReferences)
{
    public static ProductionReconciliationResult Held(string reason) =>
        new(false, reason, 0, 0, 0, Array.Empty<Guid>(), Array.Empty<string>());
}

/// <summary>
/// A candidate is not a publication authorization: provenance, completeness,
/// qualification signatures and publication authority require separate checks.
/// </summary>
public static class QualifiedProductionReconciler
{
    public static ProductionReconciliationResult Reconcile(
        ProductionReconciliationScope scope,
        IReadOnlyList<QualifiedProductionInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(inputs);
        if (scope.MachineId == Guid.Empty ||
            string.IsNullOrWhiteSpace(scope.AssignmentId) ||
            string.IsNullOrWhiteSpace(scope.ShiftId) ||
            string.IsNullOrWhiteSpace(scope.QuantityUnit) ||
            scope.StartsAtUtc.Offset != TimeSpan.Zero ||
            scope.EndsAtUtc.Offset != TimeSpan.Zero ||
            scope.EndsAtUtc <= scope.StartsAtUtc ||
            scope.EndsAtUtc - scope.StartsAtUtc > TimeSpan.FromDays(1))
            return ProductionReconciliationResult.Held("InvalidShiftScope");
        if (inputs.Count == 0)
            return ProductionReconciliationResult.Held("NoQualifiedContributions");

        var evidence = new HashSet<Guid>();
        var populations = new HashSet<string>(StringComparer.Ordinal);
        var authorities = new HashSet<string>(StringComparer.Ordinal);
        long produced = 0, good = 0, rejected = 0;
        foreach (var input in inputs)
        {
            if (!input.IsQualified || string.IsNullOrWhiteSpace(input.AuthorityReference))
                return ProductionReconciliationResult.Held("QualificationMissing");
            if (input.MachineId != scope.MachineId ||
                !StringComparer.Ordinal.Equals(input.AssignmentId, scope.AssignmentId) ||
                !StringComparer.Ordinal.Equals(input.ShiftId, scope.ShiftId) ||
                input.StartsAtUtc.Offset != TimeSpan.Zero ||
                input.EndsAtUtc.Offset != TimeSpan.Zero ||
                input.StartsAtUtc != scope.StartsAtUtc ||
                input.EndsAtUtc != scope.EndsAtUtc ||
                !StringComparer.Ordinal.Equals(input.QuantityUnit, scope.QuantityUnit))
                return ProductionReconciliationResult.Held("ScopeMismatch");
            if (!StringComparer.Ordinal.Equals(input.ProductionOrder, scope.ProductionOrder) ||
                !StringComparer.Ordinal.Equals(input.PartNumber, scope.PartNumber) ||
                !StringComparer.Ordinal.Equals(input.Operation, scope.Operation))
                return ProductionReconciliationResult.Held("ProductionContextMismatch");
            if (input.EvidenceId == Guid.Empty || string.IsNullOrWhiteSpace(input.PopulationKey) ||
                input.PopulationKey.Length > 200 || input.AuthorityReference.Length > 200)
                return ProductionReconciliationResult.Held("ProvenanceMissing");
            if (!evidence.Add(input.EvidenceId))
                return ProductionReconciliationResult.Held("DuplicateEvidence");
            if (!populations.Add(input.PopulationKey))
                return ProductionReconciliationResult.Held("PopulationOverlap");
            if (input.Produced < 0 || input.Good < 0 || input.Rejected < 0 ||
                input.Good > input.Produced ||
                input.Rejected != input.Produced - input.Good)
                return ProductionReconciliationResult.Held("QuantityInconsistent");
            try
            {
                produced = checked(produced + input.Produced);
                good = checked(good + input.Good);
                rejected = checked(rejected + input.Rejected);
            }
            catch (OverflowException)
            {
                return ProductionReconciliationResult.Held("QuantityOverflow");
            }
            authorities.Add(input.AuthorityReference);
        }
        return new ProductionReconciliationResult(true, null, produced, good, rejected,
            evidence.OrderBy(id => id).ToArray(),
            authorities.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }
}
