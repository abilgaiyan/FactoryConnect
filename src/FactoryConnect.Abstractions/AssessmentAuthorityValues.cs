namespace FactoryConnect.Abstractions;

public abstract record AssessmentAuthorityValue
{
    private protected AssessmentAuthorityValue(AssessmentAuthorityReference reference)
    { ArgumentNullException.ThrowIfNull(reference); Reference = reference; }
    public AssessmentAuthorityReference Reference { get; }
}

/// <summary>A retained business designation. Its presence alone does not make it a trusted anchor.</summary>
public sealed record AssessmentAuthorityDesignation : AssessmentAuthorityValue
{
    public AssessmentAuthorityDesignation(AuthorityDesignationReference reference, AuthoritySubjectId anchor,
        AuthoritySubjectId revocationSource, string businessDesignationEvidence,
        OperationalMetricCoverageInterval issuanceWindow, IEnumerable<AssessmentAuthorityGrant> grants)
        : base(reference)
    {
        ArgumentNullException.ThrowIfNull(anchor); ArgumentNullException.ThrowIfNull(revocationSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(businessDesignationEvidence); ArgumentNullException.ThrowIfNull(issuanceWindow);
        Anchor = anchor; RevocationSource = revocationSource; BusinessDesignationEvidence = businessDesignationEvidence;
        IssuanceWindow = issuanceWindow; Grants = new(grants);
    }
    public AuthoritySubjectId Anchor { get; }
    public AuthoritySubjectId RevocationSource { get; }
    public string BusinessDesignationEvidence { get; }
    public OperationalMetricCoverageInterval IssuanceWindow { get; }
    public AuthorityValues<AssessmentAuthorityGrant> Grants { get; }
}

public sealed record AssessmentAuthorityAuthorization : AssessmentAuthorityValue
{
    public AssessmentAuthorityAuthorization(AuthorityAuthorizationReference reference, AuthoritySubjectId issuer,
        AuthoritySubjectId subject, AuthorityDesignationReference anchor, AuthorityAuthorizationReference? parent,
        DateTimeOffset issuedAtUtc, OperationalMetricCoverageInterval issuanceWindow,
        IEnumerable<AssessmentAuthorityGrant> grants) : base(reference)
    {
        ArgumentNullException.ThrowIfNull(issuer); ArgumentNullException.ThrowIfNull(subject); ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(issuanceWindow); AuthorityUtc.Require(issuedAtUtc);
        Issuer = issuer; Subject = subject; Anchor = anchor; Parent = parent; IssuedAtUtc = issuedAtUtc;
        IssuanceWindow = issuanceWindow; Grants = new(grants);
    }
    public AuthoritySubjectId Issuer { get; }
    public AuthoritySubjectId Subject { get; }
    public AuthorityDesignationReference Anchor { get; }
    public AuthorityAuthorizationReference? Parent { get; }
    public DateTimeOffset IssuedAtUtc { get; }
    public OperationalMetricCoverageInterval IssuanceWindow { get; }
    public AuthorityValues<AssessmentAuthorityGrant> Grants { get; }
}

public enum AssessmentAuthorityRevocationEffect { Prospective = 1, Retrospective }
public sealed record AssessmentAuthorityRevocation : AssessmentAuthorityValue
{
    public AssessmentAuthorityRevocation(AuthorityRevocationReference reference, AuthoritySubjectId issuer,
        AssessmentAuthorityReference authorization, IEnumerable<AuthorityAuthorizationReference> targets,
        DateTimeOffset issuedAtUtc, DateTimeOffset effectiveAtUtc, AssessmentAuthorityRevocationEffect effect,
        AssessmentAuthorityScope scope, OperationalMetricCoverageInterval applicablePeriod) : base(reference)
    {
        ArgumentNullException.ThrowIfNull(issuer); AuthorityUtc.RequireAuthorization(authorization);
        AuthorityUtc.Require(issuedAtUtc); AuthorityUtc.Require(effectiveAtUtc);
        if (!Enum.IsDefined(effect)) { throw new ArgumentOutOfRangeException(nameof(effect)); }
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(applicablePeriod);
        Issuer = issuer; Authorization = authorization; Targets = new(targets); IssuedAtUtc = issuedAtUtc;
        EffectiveAtUtc = effectiveAtUtc; Effect = effect; Scope = scope; ApplicablePeriod = applicablePeriod;
    }
    public AuthoritySubjectId Issuer { get; }
    public AssessmentAuthorityReference Authorization { get; }
    public AuthorityValues<AuthorityAuthorizationReference> Targets { get; }
    public DateTimeOffset IssuedAtUtc { get; }
    public DateTimeOffset EffectiveAtUtc { get; }
    public AssessmentAuthorityRevocationEffect Effect { get; }
    public AssessmentAuthorityScope Scope { get; }
    public OperationalMetricCoverageInterval ApplicablePeriod { get; }
}

public sealed record AssessmentAuthorityRevocationCompleteness : AssessmentAuthorityValue
{
    public AssessmentAuthorityRevocationCompleteness(AuthorityCompletenessReference reference, AuthoritySubjectId source,
        IEnumerable<AuthorityAuthorizationReference> coveredLinks, DateTimeOffset completeThroughUtc,
        AuthoritySubjectId issuer, AssessmentAuthorityReference authorization, DateTimeOffset issuedAtUtc,
        AssessmentAuthorityScope scope, OperationalMetricCoverageInterval applicablePeriod,
        IEnumerable<AuthorityRevocationReference> decisions) : base(reference)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(issuer);
        AuthorityUtc.RequireAuthorization(authorization); AuthorityUtc.Require(completeThroughUtc); AuthorityUtc.Require(issuedAtUtc);
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(applicablePeriod);
        Source = source; CoveredLinks = new(coveredLinks); CompleteThroughUtc = completeThroughUtc;
        Issuer = issuer; Authorization = authorization; IssuedAtUtc = issuedAtUtc; Scope = scope;
        ApplicablePeriod = applicablePeriod; Decisions = new(decisions);
    }
    public AuthoritySubjectId Source { get; }
    public AuthorityValues<AuthorityAuthorizationReference> CoveredLinks { get; }
    public DateTimeOffset CompleteThroughUtc { get; }
    public AuthoritySubjectId Issuer { get; }
    public AssessmentAuthorityReference Authorization { get; }
    public DateTimeOffset IssuedAtUtc { get; }
    public AssessmentAuthorityScope Scope { get; }
    public OperationalMetricCoverageInterval ApplicablePeriod { get; }
    public AuthorityValues<AuthorityRevocationReference> Decisions { get; }
}

/// <summary>V1 requires complete revocation information through the retained verification time;
/// applies authorized retrospective decisions; unresolved conflicting claims prevent admission.</summary>
public sealed record AssessmentAuthorityPolicy : AssessmentAuthorityValue
{
    public const string V1Implementation = "FactoryConnect.AssessmentAuthorityVerifier/V1";
    public AssessmentAuthorityPolicy(AuthorityPolicyReference reference, string implementation, int representationVersion,
        IEnumerable<AssessmentAuthorityDesignation> trustedDesignations) : base(reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(implementation);
        Implementation = implementation; RepresentationVersion = representationVersion; TrustedDesignations = new(trustedDesignations);
    }
    public string Implementation { get; }
    public int RepresentationVersion { get; }
    public AuthorityValues<AssessmentAuthorityDesignation> TrustedDesignations { get; }
}
