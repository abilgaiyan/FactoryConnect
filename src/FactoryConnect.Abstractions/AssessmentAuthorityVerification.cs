namespace FactoryConnect.Abstractions;

public sealed record AuthorityVerificationRequest
{
    public AuthorityVerificationRequest(AuthorityClaimReference claim, MetricAggregationCheckpoint aggregationRevision,
        AuthorityPolicyReference policy, DateTimeOffset verificationTimeUtc,
        IEnumerable<AssessmentAuthorityReference> selections)
    {
        ArgumentNullException.ThrowIfNull(claim); ArgumentNullException.ThrowIfNull(aggregationRevision);
        ArgumentNullException.ThrowIfNull(policy); AuthorityUtc.Require(verificationTimeUtc);
        Claim = claim; AggregationRevision = aggregationRevision; Policy = policy; VerificationTimeUtc = verificationTimeUtc;
        Selections = new(selections);
    }
    public AuthorityClaimReference Claim { get; }
    public MetricAggregationCheckpoint AggregationRevision { get; }
    public AuthorityPolicyReference Policy { get; }
    public DateTimeOffset VerificationTimeUtc { get; }
    public AuthorityValues<AssessmentAuthorityReference> Selections { get; }
}

public enum AuthorityOperationErrorKind { MalformedRequest = 1, CorruptContent, ReferenceMismatch, UnsupportedRepresentation }
public enum AuthorityDiagnosticCode
{
    InvalidSelection = 1, DuplicateReference, ConflictingImmutableValues, LookupReferenceMismatch,
    ValueReferenceMismatch, MissingLookup, UnexpectedLookup, UnsupportedPolicy, UnsupportedValue,
    DependencyCycle, InvalidClaimContent, InvalidTemporalBounds, InvalidCompletenessManifest,
}
public sealed record AuthorityOperationError
{
    public AuthorityOperationError(AuthorityOperationErrorKind kind, AuthorityDiagnosticCode code,
        AssessmentAuthorityReference? reference = null)
    {
        if (!Enum.IsDefined(kind)) { throw new ArgumentOutOfRangeException(nameof(kind)); }
        if (!Enum.IsDefined(code)) { throw new ArgumentOutOfRangeException(nameof(code)); }
        Kind = kind; Code = code; Reference = reference;
    }
    public AuthorityOperationErrorKind Kind { get; }
    public AuthorityDiagnosticCode Code { get; }
    public AssessmentAuthorityReference? Reference { get; }
}

/// <summary>Absence is represented only by a successful exact lookup of RequestedReference.</summary>
public abstract record AuthorityExactLookup
{
    private protected AuthorityExactLookup(AssessmentAuthorityReference requestedReference)
    { ArgumentNullException.ThrowIfNull(requestedReference); RequestedReference = requestedReference; }
    public AssessmentAuthorityReference RequestedReference { get; }
    public sealed record Found : AuthorityExactLookup
    {
        public Found(AssessmentAuthorityReference requestedReference, AssessmentAuthorityValue value) : base(requestedReference)
        { ArgumentNullException.ThrowIfNull(value); Value = value; }
        public AssessmentAuthorityValue Value { get; }
    }
    public sealed record Absent : AuthorityExactLookup
    { public Absent(AssessmentAuthorityReference requestedReference) : base(requestedReference) { } }
    public sealed record Unavailable : AuthorityExactLookup
    {
        public Unavailable(AssessmentAuthorityReference requestedReference, AuthorityUnavailableReason reason) : base(requestedReference)
        { if (!Enum.IsDefined(reason)) { throw new ArgumentOutOfRangeException(nameof(reason)); } Reason = reason; }
        public AuthorityUnavailableReason Reason { get; }
    }
    public sealed record Failed : AuthorityExactLookup
    {
        public Failed(AssessmentAuthorityReference requestedReference, AuthorityOperationError error) : base(requestedReference)
        { ArgumentNullException.ThrowIfNull(error); Error = error; }
        public AuthorityOperationError Error { get; }
    }
}
public enum AuthorityUnavailableReason { RetrievalUnavailable = 1, VerificationUnavailable }
public interface IAssessmentAuthorityExactReader
{
    ValueTask<AuthorityExactLookup> ReadExactAsync(AssessmentAuthorityReference reference, CancellationToken cancellationToken);
}

/// <summary>Immutable retained input. Construction does not establish integrity or authority.</summary>
public sealed record ResolvedAuthorityVerificationInputs
{
    public ResolvedAuthorityVerificationInputs(AuthorityVerificationRequest request, IEnumerable<AuthorityExactLookup> lookups)
    { ArgumentNullException.ThrowIfNull(request); Request = request; Lookups = new(lookups); }
    public AuthorityVerificationRequest Request { get; }
    public AuthorityValues<AuthorityExactLookup> Lookups { get; }
}

public abstract record AuthorityInputResolution
{
    private protected AuthorityInputResolution(AuthorityVerificationRequest request)
    { ArgumentNullException.ThrowIfNull(request); Request = request; }
    public AuthorityVerificationRequest Request { get; }
    public sealed record Resolved : AuthorityInputResolution
    {
        public Resolved(ResolvedAuthorityVerificationInputs inputs) : base((inputs ?? throw new ArgumentNullException(nameof(inputs))).Request)
        { Inputs = inputs; }
        public ResolvedAuthorityVerificationInputs Inputs { get; }
    }
    public sealed record Unavailable : AuthorityInputResolution
    {
        public Unavailable(AuthorityVerificationRequest request, AssessmentAuthorityReference reference, AuthorityUnavailableReason reason) : base(request)
        { ArgumentNullException.ThrowIfNull(reference); if (!Enum.IsDefined(reason)) { throw new ArgumentOutOfRangeException(nameof(reason)); } Reference = reference; Reason = reason; }
        public AssessmentAuthorityReference Reference { get; }
        public AuthorityUnavailableReason Reason { get; }
    }
    public sealed record Failed : AuthorityInputResolution
    {
        public Failed(AuthorityVerificationRequest request, AuthorityOperationError error) : base(request)
        { ArgumentNullException.ThrowIfNull(error); Error = error; }
        public AuthorityOperationError Error { get; }
    }
}
public interface IAssessmentAuthorityInputResolver
{
    ValueTask<AuthorityInputResolution> ResolveExactAsync(AuthorityVerificationRequest request, CancellationToken cancellationToken);
}

public enum AuthorityRejectedReason
{
    ClaimKindDenied = 1, ScopeDenied, PeriodDenied, IssuanceOutsideWindow, HistoricalIssuanceDenied,
    Revoked, IssuerMismatch, PermissionExpansion, DelegationDenied,
}
public enum AuthorityNotEstablishedReason
{
    MissingDesignation = 1, MissingDependency, CompletenessInsufficient, ConflictUnresolved, DependencyAuthorityNotEstablished,
}

/// <summary>Admission is exact claim authorization, not an assessment classification or factory authority designation.</summary>
public sealed record AuthorityAdmission
{
    internal AuthorityAdmission(AssessmentAuthorityClaim claim, ResolvedAuthorityVerificationInputs inputs, AssessmentAuthorityPolicy policy)
    { Claim = claim; Inputs = inputs; Policy = policy; }
    public AssessmentAuthorityClaim Claim { get; }
    public MetricAggregationCheckpoint AggregationRevision => Inputs.Request.AggregationRevision;
    public ResolvedAuthorityVerificationInputs Inputs { get; }
    public AssessmentAuthorityPolicy Policy { get; }
    public bool Matches(AssessmentAuthorityClaim claim, MetricAggregationCheckpoint aggregationRevision,
        ResolvedAuthorityVerificationInputs inputs, AssessmentAuthorityPolicy policy) =>
        Claim == claim && AggregationRevision == aggregationRevision && Inputs == inputs && Policy == policy;
}

public abstract record AuthorityVerificationResult
{
    private protected AuthorityVerificationResult(ResolvedAuthorityVerificationInputs inputs)
    { ArgumentNullException.ThrowIfNull(inputs); Inputs = inputs; }
    public ResolvedAuthorityVerificationInputs Inputs { get; }
    public sealed record Authorized : AuthorityVerificationResult
    {
        internal Authorized(AuthorityAdmission admission) : base(admission.Inputs) { Admission = admission; }
        public AuthorityAdmission Admission { get; }
    }
    public sealed record Rejected : AuthorityVerificationResult
    {
        public Rejected(ResolvedAuthorityVerificationInputs inputs, AuthorityRejectedReason reason, AssessmentAuthorityReference reference) : base(inputs)
        { ArgumentNullException.ThrowIfNull(reference); if (!Enum.IsDefined(reason)) { throw new ArgumentOutOfRangeException(nameof(reason)); } Reason = reason; Reference = reference; }
        public AuthorityRejectedReason Reason { get; }
        public AssessmentAuthorityReference Reference { get; }
    }
    public sealed record NotEstablished : AuthorityVerificationResult
    {
        public NotEstablished(ResolvedAuthorityVerificationInputs inputs, AuthorityNotEstablishedReason reason, AssessmentAuthorityReference reference) : base(inputs)
        { ArgumentNullException.ThrowIfNull(reference); if (!Enum.IsDefined(reason)) { throw new ArgumentOutOfRangeException(nameof(reason)); } Reason = reason; Reference = reference; }
        public AuthorityNotEstablishedReason Reason { get; }
        public AssessmentAuthorityReference Reference { get; }
    }
    public sealed record Unavailable : AuthorityVerificationResult
    {
        public Unavailable(ResolvedAuthorityVerificationInputs inputs, AuthorityUnavailableReason reason) : base(inputs)
        { if (!Enum.IsDefined(reason)) { throw new ArgumentOutOfRangeException(nameof(reason)); } Reason = reason; }
        public AuthorityUnavailableReason Reason { get; }
    }
    public sealed record Failed : AuthorityVerificationResult
    {
        public Failed(ResolvedAuthorityVerificationInputs inputs, AuthorityOperationError error) : base(inputs)
        { ArgumentNullException.ThrowIfNull(error); Error = error; }
        public AuthorityOperationError Error { get; }
    }
}
public interface IAssessmentAuthorityVerifier
{
    AuthorityVerificationResult Verify(ResolvedAuthorityVerificationInputs inputs, CancellationToken cancellationToken);
}
