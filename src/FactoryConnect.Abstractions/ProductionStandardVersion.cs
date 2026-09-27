namespace FactoryConnect.Abstractions;

/// <summary>An approved, immutable production standard; publication assigns its version and authority revision.</summary>
public sealed record ProductionStandardVersion
{
    public required string VersionId { get; init; }
    public required CompanyId CompanyId { get; init; }
    public required SiteId SiteId { get; init; }
    public required PartId PartId { get; init; }
    public required OperationId OperationId { get; init; }
    public MachineId? MachineId { get; init; }
    public required decimal SecondsPerUnit { get; init; }
    public required DateTimeOffset EffectiveFromUtc { get; init; }
    public DateTimeOffset? EffectiveToUtc { get; init; }
    public required string SourceReference { get; init; }
    public required long PublishedRevision { get; init; }

    public bool Contains(DateTimeOffset occurredAtUtc) =>
        occurredAtUtc >= EffectiveFromUtc &&
        (EffectiveToUtc is null || occurredAtUtc < EffectiveToUtc.Value);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(VersionId) || string.IsNullOrWhiteSpace(SourceReference) ||
            CompanyId.IsEmpty || SiteId.IsEmpty || PartId.IsEmpty || OperationId.IsEmpty ||
            MachineId is { IsEmpty: true })
        {
            throw new ArgumentException("An approved standard requires an identity, selector, and source reference.");
        }

        if (SecondsPerUnit < 0 || PublishedRevision < 1 ||
            EffectiveFromUtc.Offset != TimeSpan.Zero ||
            EffectiveToUtc is { Offset: var offset } && offset != TimeSpan.Zero ||
            EffectiveToUtc is not null && EffectiveToUtc <= EffectiveFromUtc)
        {
            throw new ArgumentException("Standard value, authority revision, or UTC effective interval is invalid.");
        }
    }
}
