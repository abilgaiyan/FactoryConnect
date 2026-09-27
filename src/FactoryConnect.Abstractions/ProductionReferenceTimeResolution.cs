namespace FactoryConnect.Abstractions;

public enum ProductionReferenceTimeResolutionStatus
{
    Resolved,
    MissingIdentity,
    MissingStandard,
    AmbiguousStandard,
}

/// <summary>One durable outcome per produced-quantity source, independent of whether a duration fact exists.</summary>
public sealed record ProductionReferenceTimeResolution
{
    public required ProductionQuantityEvidenceId SourceQuantityEvidenceId { get; init; }
    public required CompanyId CompanyId { get; init; }
    public required SiteId SiteId { get; init; }
    public required MachineId MachineId { get; init; }
    public PartId? PartId { get; init; }
    public OperationId? OperationId { get; init; }
    public required ShiftOccurrenceId ShiftOccurrenceId { get; init; }
    public required ProductionDayId ProductionDayId { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required int ProducedUnits { get; init; }
    public required long AuthorityRevision { get; init; }
    public required ProductionReferenceTimeResolutionStatus Status { get; init; }
    public string? SelectedStandardVersionId { get; init; }
    public string? SelectedStandardSourceReference { get; init; }
    public decimal? IdealDurationSeconds { get; init; }
    public IReadOnlyList<string> ConflictingStandardVersionIds { get; init; } = [];

    public void Validate()
    {
        if (SourceQuantityEvidenceId.IsEmpty || CompanyId.IsEmpty || SiteId.IsEmpty ||
            MachineId.IsEmpty || ShiftOccurrenceId is null || ProductionDayId is null ||
            PartId is { IsEmpty: true } || OperationId is { IsEmpty: true } ||
            SiteId != ShiftOccurrenceId.SiteId || SiteId != ProductionDayId.SiteId ||
            OccurredAtUtc.Offset != TimeSpan.Zero ||
            OccurredAtUtc < ShiftOccurrenceId.StartsAtUtc ||
            OccurredAtUtc >= ShiftOccurrenceId.EndsAtUtc ||
            ProducedUnits < 0 || AuthorityRevision < 0 ||
            !Enum.IsDefined(Status))
        {
            throw new ArgumentException("Reference-time resolution has invalid source, period, or authority identity.");
        }

        if (Status == ProductionReferenceTimeResolutionStatus.Resolved)
        {
            if (string.IsNullOrWhiteSpace(SelectedStandardVersionId) ||
                string.IsNullOrWhiteSpace(SelectedStandardSourceReference) ||
                IdealDurationSeconds is null or < 0 ||
                ConflictingStandardVersionIds.Count != 0)
            {
                throw new ArgumentException("A resolved outcome requires one standard and nonnegative ideal seconds.");
            }
        }
        else if (SelectedStandardVersionId is not null ||
            SelectedStandardSourceReference is not null || IdealDurationSeconds is not null ||
            (Status == ProductionReferenceTimeResolutionStatus.AmbiguousStandard) !=
            (ConflictingStandardVersionIds.Count > 1))
        {
            throw new ArgumentException("An unresolved outcome cannot contain derived duration or an invalid conflict set.");
        }
    }
}
