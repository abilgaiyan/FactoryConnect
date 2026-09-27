using System.Collections.ObjectModel;

namespace FactoryConnect.Abstractions;

/// <summary>One immutable publication of an outcome under an independent reference-time revision.</summary>
public sealed record PublishedProductionReferenceTimeOutcome
{
    public PublishedProductionReferenceTimeOutcome(
        ProductionReferenceTimeAuthorityRevision publicationRevision,
        ProductionReferenceTimeResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (publicationRevision.Value == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(publicationRevision),
                "An outcome publication must advance the reference-time authority.");
        }

        resolution.Validate();
        PublicationRevision = publicationRevision;
        Resolution = resolution with
        {
            ConflictingStandardVersionIds = new ReadOnlyCollection<string>(
                resolution.ConflictingStandardVersionIds.ToArray()),
        };
    }

    public ProductionReferenceTimeAuthorityRevision PublicationRevision { get; }

    public ProductionReferenceTimeResolution Resolution { get; }

    public ProductionQuantityEvidenceId SourceQuantityEvidenceId => Resolution.SourceQuantityEvidenceId;

    public long StandardAuthorityRevision => Resolution.AuthorityRevision;
}
