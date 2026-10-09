namespace FactoryConnect.Abstractions;

public abstract record AssessmentAuthorityClaimContent
{
    private protected AssessmentAuthorityClaimContent() { }
    public abstract AssessmentAuthorityClaimKind Kind { get; }
    public sealed record Schedule : AssessmentAuthorityClaimContent
    {
        public Schedule(OperationalMetricCoverageScheduleReference schedule, string resolverVersion,
            bool complete, IEnumerable<OperationalMetricCoverageInterval> expectedIntervals)
        {
            ArgumentNullException.ThrowIfNull(schedule); ArgumentException.ThrowIfNullOrWhiteSpace(resolverVersion);
            ScheduleReference = schedule; ResolverVersion = resolverVersion; Complete = complete;
            ExpectedIntervals = new(expectedIntervals);
        }
        public override AssessmentAuthorityClaimKind Kind => AssessmentAuthorityClaimKind.ScheduleCompleteness;
        public OperationalMetricCoverageScheduleReference ScheduleReference { get; }
        public string ResolverVersion { get; }
        public bool Complete { get; }
        public AuthorityValues<OperationalMetricCoverageInterval> ExpectedIntervals { get; }
    }
    public sealed record Fragment : AssessmentAuthorityClaimContent
    {
        public Fragment(AuthorityDomainId domain, string identity, string revision,
            OperationalMetricCoverageInterval interval, MachineState state, bool? planned,
            IEnumerable<MetricInputPosition> contributionPositions,
            IEnumerable<OperationalMetricCoverageEvidenceReference> provenance)
        {
            ArgumentNullException.ThrowIfNull(domain); ArgumentException.ThrowIfNullOrWhiteSpace(identity);
            ArgumentException.ThrowIfNullOrWhiteSpace(revision); ArgumentNullException.ThrowIfNull(interval);
            if (!Enum.IsDefined(state)) { throw new ArgumentOutOfRangeException(nameof(state)); }
            Domain = domain; Identity = identity; Revision = revision; Interval = interval; State = state; Planned = planned;
            ContributionPositions = new(contributionPositions); Provenance = new(provenance);
        }
        public override AssessmentAuthorityClaimKind Kind => AssessmentAuthorityClaimKind.FragmentAccounting;
        public AuthorityDomainId Domain { get; }
        public string Identity { get; }
        public string Revision { get; }
        public OperationalMetricCoverageInterval Interval { get; }
        public MachineState State { get; }
        public bool? Planned { get; }
        public AuthorityValues<MetricInputPosition> ContributionPositions { get; }
        public AuthorityValues<OperationalMetricCoverageEvidenceReference> Provenance { get; }
    }
    public sealed record Gap : AssessmentAuthorityClaimContent
    {
        public Gap(OperationalMetricCoverageInterval interval, IEnumerable<OperationalMetricCoverageEvidenceReference> evidence)
        { ArgumentNullException.ThrowIfNull(interval); Interval = interval; Evidence = new(evidence); }
        public override AssessmentAuthorityClaimKind Kind => AssessmentAuthorityClaimKind.Gap;
        public OperationalMetricCoverageInterval Interval { get; }
        public AuthorityValues<OperationalMetricCoverageEvidenceReference> Evidence { get; }
    }
    public sealed record Completion : AssessmentAuthorityClaimContent
    {
        public Completion(DateTimeOffset boundaryUtc, IEnumerable<AuthorityClaimReference> accountedClaims)
        { AuthorityUtc.Require(boundaryUtc); BoundaryUtc = boundaryUtc; AccountedClaims = new(accountedClaims); }
        public override AssessmentAuthorityClaimKind Kind => AssessmentAuthorityClaimKind.Completion;
        public DateTimeOffset BoundaryUtc { get; }
        public AuthorityValues<AuthorityClaimReference> AccountedClaims { get; }
    }
}

public sealed record AssessmentAuthorityClaim : AssessmentAuthorityValue
{
    public AssessmentAuthorityClaim(AuthorityClaimReference reference, AuthoritySubjectId issuer,
        AssessmentAuthorityReference authorization, DateTimeOffset issuedAtUtc, AssessmentAuthorityScope scope,
        OperationalMetricCoverageInterval period, MetricAggregationCheckpoint aggregationRevision,
        AssessmentAuthorityClaimContent content)
        : base(reference)
    {
        ArgumentNullException.ThrowIfNull(issuer); AuthorityUtc.RequireAuthorization(authorization); AuthorityUtc.Require(issuedAtUtc);
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(aggregationRevision); ArgumentNullException.ThrowIfNull(content);
        Issuer = issuer; Authorization = authorization; IssuedAtUtc = issuedAtUtc; Scope = scope; Period = period;
        AggregationRevision = aggregationRevision; Content = content;
    }
    public AuthoritySubjectId Issuer { get; }
    public AssessmentAuthorityReference Authorization { get; }
    public DateTimeOffset IssuedAtUtc { get; }
    public AssessmentAuthorityScope Scope { get; }
    public OperationalMetricCoverageInterval Period { get; }
    public MetricAggregationCheckpoint AggregationRevision { get; }
    public AssessmentAuthorityClaimContent Content { get; }
}

public static class AuthorityUtc
{
    public static void Require(DateTimeOffset value)
    { if (value.Offset != TimeSpan.Zero) { throw new ArgumentException("Authority time must be UTC.", nameof(value)); } }
    public static void RequireAuthorization(AssessmentAuthorityReference value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is not AuthorityAuthorizationReference and not AuthorityDesignationReference)
        { throw new ArgumentException("Issuer authority must reference an authorization or designated anchor.", nameof(value)); }
    }
}
