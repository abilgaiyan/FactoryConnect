using System.Collections.ObjectModel;

namespace FactoryConnect.Abstractions;

/// <summary>An immutable view of approved versions visible at one publication revision.</summary>
public sealed class ProductionStandardAuthorityCut
{
    internal ProductionStandardAuthorityCut(long revision, IEnumerable<ProductionStandardVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);

        var snapshot = versions.ToArray();
        foreach (var version in snapshot)
        {
            ArgumentNullException.ThrowIfNull(version);
            version.Validate();
            if (version.PublishedRevision > revision)
            {
                throw new ArgumentException("A cut cannot contain a later publication.", nameof(versions));
            }
        }

        if (snapshot.Select(static version => version.VersionId).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
        {
            throw new ArgumentException("Standard version identities must be unique within a cut.", nameof(versions));
        }

        Revision = revision;
        Versions = new ReadOnlyCollection<ProductionStandardVersion>(snapshot);
    }

    public long Revision { get; }

    public IReadOnlyList<ProductionStandardVersion> Versions { get; }
}
