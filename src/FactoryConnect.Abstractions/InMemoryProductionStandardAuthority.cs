namespace FactoryConnect.Abstractions;

/// <summary>Append-only approved standard publication and repeatable authority-cut reads.</summary>
public sealed class InMemoryProductionStandardAuthority
{
    private readonly object _sync = new();
    private readonly List<ProductionStandardVersion> _versions = [];

    public long Publish(ProductionStandardVersion approvedVersion)
    {
        ArgumentNullException.ThrowIfNull(approvedVersion);
        approvedVersion.Validate();
        lock (_sync)
        {
            if (_versions.Any(version => string.Equals(version.VersionId, approvedVersion.VersionId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("An approved standard version cannot be replaced.");
            }

            var next = checked(_versions.Count + 1L);
            if (approvedVersion.PublishedRevision != next)
            {
                throw new InvalidOperationException("Published revision must be the next authority revision.");
            }

            _versions.Add(approvedVersion);
            return next;
        }
    }

    public ProductionStandardAuthorityCut ReadCut(long revision)
    {
        lock (_sync)
        {
            if (revision < 0 || revision > _versions.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(revision));
            }

            return new ProductionStandardAuthorityCut(
                revision,
                _versions.Where(version => version.PublishedRevision <= revision));
        }
    }

    public ProductionStandardAuthorityCut ReadCurrentCut()
    {
        lock (_sync)
        {
            return new ProductionStandardAuthorityCut(_versions.Count, _versions);
        }
    }
}
