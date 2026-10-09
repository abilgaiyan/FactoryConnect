namespace FactoryConnect.Abstractions;

/// <summary>Authority identities are exact ordinal strings; no normalization or ordering is inferred.</summary>
public sealed record AuthorityDomainId
{
    public AuthorityDomainId(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); Value = value; }
    public string Value { get; }
}

public sealed record AuthoritySubjectId
{
    public AuthoritySubjectId(AuthorityDomainId domain, string identity)
    {
        ArgumentNullException.ThrowIfNull(domain); ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        Domain = domain; Identity = identity;
    }
    public AuthorityDomainId Domain { get; }
    public string Identity { get; }
}

public abstract record AssessmentAuthorityReference
{
    private protected AssessmentAuthorityReference(AuthorityDomainId domain, string identity, string revision)
    {
        ArgumentNullException.ThrowIfNull(domain); ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision); Domain = domain; Identity = identity; RevisionValue = revision;
    }
    public AuthorityDomainId Domain { get; }
    public string Identity { get; }
    public string RevisionValue { get; }
}

public sealed record AuthorityClaimRevision
{
    public AuthorityClaimRevision(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); Value = value; }
    public string Value { get; }
}
public sealed record AuthorityClaimReference : AssessmentAuthorityReference
{
    public AuthorityClaimReference(AuthorityDomainId domain, string identity, AuthorityClaimRevision revision)
        : base(domain, identity, (revision ?? throw new ArgumentNullException(nameof(revision))).Value) { Revision = revision; }
    public AuthorityClaimRevision Revision { get; }
}

public sealed record AuthorityAuthorizationRevision
{
    public AuthorityAuthorizationRevision(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); Value = value; }
    public string Value { get; }
}
public sealed record AuthorityAuthorizationReference : AssessmentAuthorityReference
{
    public AuthorityAuthorizationReference(AuthorityDomainId domain, string identity, AuthorityAuthorizationRevision revision)
        : base(domain, identity, (revision ?? throw new ArgumentNullException(nameof(revision))).Value) { Revision = revision; }
    public AuthorityAuthorizationRevision Revision { get; }
}

public sealed record AuthorityDesignationRevision
{
    public AuthorityDesignationRevision(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); Value = value; }
    public string Value { get; }
}
public sealed record AuthorityDesignationReference : AssessmentAuthorityReference
{
    public AuthorityDesignationReference(AuthorityDomainId domain, string identity, AuthorityDesignationRevision revision)
        : base(domain, identity, (revision ?? throw new ArgumentNullException(nameof(revision))).Value) { Revision = revision; }
    public AuthorityDesignationRevision Revision { get; }
}

public sealed record AuthorityRevocationRevision
{
    public AuthorityRevocationRevision(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); Value = value; }
    public string Value { get; }
}
public sealed record AuthorityRevocationReference : AssessmentAuthorityReference
{
    public AuthorityRevocationReference(AuthorityDomainId domain, string identity, AuthorityRevocationRevision revision)
        : base(domain, identity, (revision ?? throw new ArgumentNullException(nameof(revision))).Value) { Revision = revision; }
    public AuthorityRevocationRevision Revision { get; }
}

public sealed record AuthorityCompletenessRevision
{
    public AuthorityCompletenessRevision(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); Value = value; }
    public string Value { get; }
}
public sealed record AuthorityCompletenessReference : AssessmentAuthorityReference
{
    public AuthorityCompletenessReference(AuthorityDomainId domain, string identity, AuthorityCompletenessRevision revision)
        : base(domain, identity, (revision ?? throw new ArgumentNullException(nameof(revision))).Value) { Revision = revision; }
    public AuthorityCompletenessRevision Revision { get; }
}

public sealed record AuthorityPolicyRevision
{
    public AuthorityPolicyRevision(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); Value = value; }
    public string Value { get; }
}
public sealed record AuthorityPolicyReference : AssessmentAuthorityReference
{
    public AuthorityPolicyReference(AuthorityDomainId domain, string identity, AuthorityPolicyRevision revision)
        : base(domain, identity, (revision ?? throw new ArgumentNullException(nameof(revision))).Value) { Revision = revision; }
    public AuthorityPolicyRevision Revision { get; }
}

/// <summary>Defensively copied, ordered values with exact sequence equality.</summary>
public sealed class AuthorityValues<T> : IReadOnlyList<T>, IEquatable<AuthorityValues<T>> where T : notnull
{
    private readonly T[] _items;
    public AuthorityValues(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items); _items = items.ToArray();
        if (_items.Any(static item => item is null)) { throw new ArgumentException("Null authority value.", nameof(items)); }
    }
    public int Count => _items.Length;
    public T this[int index] => _items[index];
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public bool Equals(AuthorityValues<T>? other) => other is not null && _items.SequenceEqual(other._items);
    public override bool Equals(object? obj) => obj is AuthorityValues<T> other && Equals(other);
    public override int GetHashCode() { var hash = new HashCode(); foreach (var item in _items) { hash.Add(item); } return hash.ToHashCode(); }
}
