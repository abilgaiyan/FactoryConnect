using FactoryConnect.Abstractions;

namespace FactoryConnect.Core;

/// <summary>Resolves an approved standard for one attributable produced-quantity contribution.</summary>
public static class ProductionStandardResolver
{
    public static ProductionReferenceTimeResolution Resolve(
        ProductionQuantityEvidence evidence,
        ShiftOccurrenceId shift,
        ProductionDayId day,
        ProductionStandardAuthorityCut cut)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(shift);
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(cut);
        evidence.Validate();
        if (evidence.PartCountIncrement is null)
        {
            throw new ArgumentException("Reference time requires produced-quantity evidence.", nameof(evidence));
        }

        if (evidence.SiteId != shift.SiteId || evidence.SiteId != day.SiteId ||
            evidence.ShiftId != shift.ShiftId || evidence.OccurredAtUtc.Offset != TimeSpan.Zero ||
            evidence.OccurredAtUtc < shift.StartsAtUtc || evidence.OccurredAtUtc >= shift.EndsAtUtc)
        {
            throw new ArgumentException("Quantity evidence does not belong to the requested shift and day.", nameof(evidence));
        }

        var result = new ProductionReferenceTimeResolution
        {
            SourceQuantityEvidenceId = evidence.Id,
            CompanyId = evidence.CompanyId,
            SiteId = evidence.SiteId,
            MachineId = evidence.MachineId,
            PartId = evidence.PartId,
            OperationId = evidence.OperationId,
            ShiftOccurrenceId = shift,
            ProductionDayId = day,
            OccurredAtUtc = evidence.OccurredAtUtc,
            ProducedUnits = evidence.PartCountIncrement.Value,
            AuthorityRevision = cut.Revision,
            Status = ProductionReferenceTimeResolutionStatus.MissingIdentity,
        };

        if (evidence.PartId is null || evidence.OperationId is null)
        {
            result.Validate();
            return result;
        }

        var applicable = cut.Versions.Where(version =>
            version.CompanyId == evidence.CompanyId &&
            version.SiteId == evidence.SiteId &&
            version.PartId == evidence.PartId &&
            version.OperationId == evidence.OperationId &&
            version.Contains(evidence.OccurredAtUtc)).ToArray();
        var machineMatches = applicable.Where(version => version.MachineId == evidence.MachineId).ToArray();
        var candidates = machineMatches.Length != 0
            ? machineMatches
            : applicable.Where(version => version.MachineId is null).ToArray();

        if (candidates.Length == 0)
        {
            result = result with { Status = ProductionReferenceTimeResolutionStatus.MissingStandard };
        }
        else if (candidates.Length > 1)
        {
            result = result with
            {
                Status = ProductionReferenceTimeResolutionStatus.AmbiguousStandard,
                ConflictingStandardVersionIds = candidates.Select(static version => version.VersionId)
                    .OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            };
        }
        else
        {
            var selected = candidates[0];
            result = result with
            {
                Status = ProductionReferenceTimeResolutionStatus.Resolved,
                SelectedStandardVersionId = selected.VersionId,
                SelectedStandardSourceReference = selected.SourceReference,
                IdealDurationSeconds = checked(selected.SecondsPerUnit * evidence.PartCountIncrement.Value),
            };
        }

        result.Validate();
        return result;
    }
}
