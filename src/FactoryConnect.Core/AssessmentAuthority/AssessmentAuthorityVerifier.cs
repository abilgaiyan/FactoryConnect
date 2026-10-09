using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.AssessmentAuthority;

/// <summary>Pure V1 verification over retained immutable values. No reader, clock, registry lookup or publication.</summary>
public sealed class AssessmentAuthorityVerifier : IAssessmentAuthorityVerifier
{
    public AuthorityVerificationResult Verify(ResolvedAuthorityVerificationInputs inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs); cancellationToken.ThrowIfCancellationRequested();
        var invalid = AuthoritySnapshotValidation.Validate(inputs, cancellationToken);
        if (invalid is not null) { return new AuthorityVerificationResult.Failed(inputs, invalid); }
        return new Verification(inputs, cancellationToken).Run();
    }

    private sealed class Verification
    {
        private readonly ResolvedAuthorityVerificationInputs _inputs;
        private readonly CancellationToken _token;
        private readonly Dictionary<AssessmentAuthorityReference, AssessmentAuthorityValue> _values;
        private readonly HashSet<RevocationUse> _uses = [];
        private readonly Dictionary<AssessmentAuthorityReference, HashSet<AssessmentAuthorityReference>> _proofEdges = [];
        private AuthorityVerificationResult? _failure;
        private AuthorityVerificationResult.Rejected? _revoked;
        private AssessmentAuthorityPolicy _policy = null!;
        private sealed record RevocationUse(AssessmentAuthorityAuthorization Authorization, DateTimeOffset UsedAt,
            AssessmentAuthorityScope Scope, OperationalMetricCoverageInterval Period, bool Primary);

        internal Verification(ResolvedAuthorityVerificationInputs inputs, CancellationToken token)
        {
            _inputs = inputs; _token = token;
            _values = inputs.Lookups.OfType<AuthorityExactLookup.Found>().ToDictionary(static item => item.RequestedReference, static item => item.Value);
        }
        internal AuthorityVerificationResult Run()
        {
            var policy = Find<AssessmentAuthorityPolicy>(_inputs.Request.Policy);
            var claim = Find<AssessmentAuthorityClaim>(_inputs.Request.Claim);
            if (_failure is not null) { return _failure; }
            _policy = policy!;
            foreach (var trusted in _policy.TrustedDesignations)
            {
                if (Find<AssessmentAuthorityDesignation>(trusted.Reference) != trusted)
                { return _failure ?? Failed(AuthorityDiagnosticCode.ValueReferenceMismatch, trusted.Reference); }
            }
            if (claim!.AggregationRevision != _inputs.Request.AggregationRevision
                || claim.Scope is AssessmentAuthorityScope.MachineScope machine && machine.Machine != claim.AggregationRevision.StreamId.MachineId)
            { return Failed(AuthorityDiagnosticCode.ValueReferenceMismatch, claim.Reference); }
            if (_values.Values.OfType<AssessmentAuthorityClaim>().Any(other => other.Reference != claim.Reference
                && other.AggregationRevision == claim.AggregationRevision && other.Scope == claim.Scope
                && other.Period == claim.Period && other.Content.Kind == claim.Content.Kind && SameClaimSubject(other, claim) && other.Content != claim.Content))
            { return new AuthorityVerificationResult.NotEstablished(_inputs, AuthorityNotEstablishedReason.ConflictUnresolved, claim.Reference); }
            VerifyClaim(claim);
            if (_failure is not null) { return _failure; }

            var checkedUses = new HashSet<RevocationUse>();
            // Worklist: delegated completeness/revocation issuers can introduce further authorization uses.
            while (_uses.FirstOrDefault(use => !checkedUses.Contains(use)) is { } use)
            {
                _token.ThrowIfCancellationRequested(); checkedUses.Add(use);
                VerifyRevocations(use);
                if (_failure is not null) { return _failure; }
            }
            if (HasProofCycle()) { return Failed(AuthorityDiagnosticCode.DependencyCycle, claim.Reference); }
            _token.ThrowIfCancellationRequested();
            if (_revoked is not null) { return _revoked; }
            return new AuthorityVerificationResult.Authorized(new AuthorityAdmission(claim, _inputs, _policy));
        }

        private void VerifyClaim(AssessmentAuthorityClaim claim)
        {
            var pending = new Stack<AssessmentAuthorityClaim>(); pending.Push(claim);
            var visited = new HashSet<AssessmentAuthorityReference>();
            while (pending.TryPop(out var current))
            {
                _token.ThrowIfCancellationRequested();
                if (!visited.Add(current.Reference)) { continue; }
                if (current.AggregationRevision != claim.AggregationRevision || !claim.Scope.Contains(current.Scope)
                    || !AuthoritySnapshotValidation.Contains(claim.Period, current.Period))
                { _failure = Failed(AuthorityDiagnosticCode.ValueReferenceMismatch, current.Reference); return; }
                Authorize(current.Issuer, current.Authorization, current.Content.Kind, current.Scope, current.Period, current.IssuedAtUtc);
                if (_failure is not null) { return; }
                if (current.Content is not AssessmentAuthorityClaimContent.Completion completion) { continue; }
                var intervals = new List<OperationalMetricCoverageInterval>();
                foreach (var reference in completion.AccountedClaims)
                {
                    var part = Find<AssessmentAuthorityClaim>(reference);
                    if (_failure is not null) { return; }
                    var interval = part!.Content switch
                    {
                        AssessmentAuthorityClaimContent.Fragment fragment => fragment.Interval,
                        AssessmentAuthorityClaimContent.Gap gap => gap.Interval,
                        _ => null,
                    };
                    if (interval is null || part.Scope != current.Scope || interval.EndsAtUtc > completion.BoundaryUtc)
                    { _failure = Failed(AuthorityDiagnosticCode.InvalidClaimContent, reference); return; }
                    intervals.Add(interval); pending.Push(part);
                }
                var cursor = current.Period.StartsAtUtc;
                foreach (var interval in intervals.OrderBy(static item => item.StartsAtUtc))
                {
                    if (interval.StartsAtUtc != cursor)
                    { _failure = new AuthorityVerificationResult.NotEstablished(_inputs, AuthorityNotEstablishedReason.MissingDependency, current.Reference); return; }
                    cursor = interval.EndsAtUtc;
                }
                if (cursor != current.Period.EndsAtUtc)
                { _failure = new AuthorityVerificationResult.NotEstablished(_inputs, AuthorityNotEstablishedReason.MissingDependency, current.Reference); return; }
            }
        }

        private void Authorize(AuthoritySubjectId issuer, AssessmentAuthorityReference reference,
            AssessmentAuthorityClaimKind kind, AssessmentAuthorityScope scope, OperationalMetricCoverageInterval period,
            DateTimeOffset issuedAt, bool primary = true)
        {
            _token.ThrowIfCancellationRequested();
            if (reference is AuthorityDesignationReference designationReference)
            {
                var designation = Trusted(designationReference);
                if (designation is null) { return; }
                if (designation.Anchor != issuer) { Reject(AuthorityRejectedReason.IssuerMismatch, reference); return; }
                CheckGrant(designation.Grants, designation.IssuanceWindow, kind, scope, period, issuedAt, reference);
                return;
            }
            var child = Find<AssessmentAuthorityAuthorization>(reference);
            if (child is null) { return; }
            if (child.Subject != issuer) { Reject(AuthorityRejectedReason.IssuerMismatch, reference); return; }
            CheckGrant(child.Grants, child.IssuanceWindow, kind, scope, period, issuedAt, reference);
            if (_failure is not null) { return; }
            var seen = new HashSet<AssessmentAuthorityReference>();
            var usedAt = issuedAt;
            while (child is not null)
            {
                _token.ThrowIfCancellationRequested();
                if (!seen.Add(child.Reference)) { _failure = Failed(AuthorityDiagnosticCode.DependencyCycle, child.Reference); return; }
                _uses.Add(new(child, usedAt, scope, period, primary));
                var designation = Trusted(child.Anchor);
                if (designation is null) { return; }
                AuthorityValues<AssessmentAuthorityGrant> parentGrants;
                OperationalMetricCoverageInterval parentWindow;
                if (child.Parent is null)
                {
                    if (child.Issuer != designation.Anchor) { Reject(AuthorityRejectedReason.IssuerMismatch, child.Reference); return; }
                    parentGrants = designation.Grants; parentWindow = designation.IssuanceWindow;
                }
                else
                {
                    Edge(child.Reference, child.Parent);
                    var parent = Find<AssessmentAuthorityAuthorization>(child.Parent);
                    if (parent is null) { return; }
                    if (parent.Subject != child.Issuer || parent.Anchor != child.Anchor)
                    { Reject(AuthorityRejectedReason.IssuerMismatch, child.Reference); return; }
                    parentGrants = parent.Grants; parentWindow = parent.IssuanceWindow;
                }
                if (!AuthoritySnapshotValidation.Contains(parentWindow, child.IssuedAtUtc)
                    || !AuthoritySnapshotValidation.Contains(parentWindow, child.IssuanceWindow))
                { Reject(AuthorityRejectedReason.IssuanceOutsideWindow, child.Reference); return; }
                foreach (var grant in child.Grants)
                {
                    var candidates = parentGrants.Where(parent => parent.Kind == grant.Kind && parent.Scope.Contains(grant.Scope)
                        && AuthoritySnapshotValidation.Contains(parent.Period, grant.Period)).ToArray();
                    if (!candidates.Any(parent => parent.MayDelegate && (!grant.HistoricalIssuance || parent.HistoricalIssuance)))
                    { Reject(candidates.Length != 0 ? AuthorityRejectedReason.DelegationDenied : AuthorityRejectedReason.PermissionExpansion, child.Reference); return; }
                    if (grant.Period.StartsAtUtc < child.IssuedAtUtc && !candidates.Any(parent => parent.MayDelegate && parent.HistoricalIssuance))
                    { Reject(AuthorityRejectedReason.HistoricalIssuanceDenied, child.Reference); return; }
                }
                usedAt = child.IssuedAtUtc;
                child = child.Parent is null ? null : Find<AssessmentAuthorityAuthorization>(child.Parent);
            }
        }

        private void VerifyRevocations(RevocationUse use)
        {
            var designation = Trusted(use.Authorization.Anchor);
            if (designation is null) { return; }
            var applicable = _values.Values.OfType<AssessmentAuthorityRevocationCompleteness>()
                .Where(item => item.CoveredLinks.Contains((AuthorityAuthorizationReference)use.Authorization.Reference)
                    && item.Source == designation.RevocationSource && item.Scope.Contains(use.Scope)
                    && AuthoritySnapshotValidation.Contains(item.ApplicablePeriod, use.Period)
                    && item.CompleteThroughUtc >= _inputs.Request.VerificationTimeUtc).ToArray();
            if (applicable.Length == 0)
            { _failure = new AuthorityVerificationResult.NotEstablished(_inputs, AuthorityNotEstablishedReason.CompletenessInsufficient, use.Authorization.Reference); return; }
            foreach (var completeness in applicable)
            {
                Edge(use.Authorization.Reference, completeness.Reference);
                Edge(completeness.Reference, completeness.Authorization);
                Authorize(completeness.Issuer, completeness.Authorization, AssessmentAuthorityClaimKind.RevocationCompleteness,
                    completeness.Scope, completeness.ApplicablePeriod, completeness.IssuedAtUtc, primary: false);
                if (_failure is not null) { DependencyFailure(completeness.Reference); return; }
                var declared = completeness.Decisions.ToHashSet();
                if (_values.Values.OfType<AssessmentAuthorityRevocation>().Any(decision =>
                    decision.Targets.Contains((AuthorityAuthorizationReference)use.Authorization.Reference)
                    && decision.IssuedAtUtc <= completeness.CompleteThroughUtc && !declared.Contains((AuthorityRevocationReference)decision.Reference)))
                { _failure = Failed(AuthorityDiagnosticCode.InvalidCompletenessManifest, completeness.Reference); return; }
                foreach (var reference in completeness.Decisions)
                {
                    var decision = Find<AssessmentAuthorityRevocation>(reference);
                    if (_failure is not null) { return; }
                    if (decision!.IssuedAtUtc > completeness.CompleteThroughUtc)
                    { _failure = Failed(AuthorityDiagnosticCode.InvalidCompletenessManifest, reference); return; }
                    Edge(completeness.Reference, reference); Edge(reference, decision.Authorization);
                    Authorize(decision.Issuer, decision.Authorization,
                        decision.Effect == AssessmentAuthorityRevocationEffect.Prospective
                            ? AssessmentAuthorityClaimKind.ProspectiveRevocation : AssessmentAuthorityClaimKind.RetrospectiveRevocation,
                        decision.Scope, decision.ApplicablePeriod, decision.IssuedAtUtc, primary: false);
                    if (_failure is not null) { DependencyFailure(decision.Reference); return; }
                    if (decision.Targets.Contains((AuthorityAuthorizationReference)use.Authorization.Reference)
                        && ScopesOverlap(decision.Scope, use.Scope) && decision.ApplicablePeriod.StartsAtUtc < use.Period.EndsAtUtc
                        && use.Period.StartsAtUtc < decision.ApplicablePeriod.EndsAtUtc
                        && use.UsedAt >= decision.EffectiveAtUtc)
                    {
                        if (!use.Primary)
                        { _failure = new AuthorityVerificationResult.NotEstablished(_inputs, AuthorityNotEstablishedReason.DependencyAuthorityNotEstablished, use.Authorization.Reference); return; }
                        _revoked ??= new AuthorityVerificationResult.Rejected(_inputs, AuthorityRejectedReason.Revoked, use.Authorization.Reference);
                    }
                }
            }
        }

        private static bool ScopesOverlap(AssessmentAuthorityScope first, AssessmentAuthorityScope second) =>
            first.Contains(second) || second.Contains(first);

        private static bool SameClaimSubject(AssessmentAuthorityClaim first, AssessmentAuthorityClaim second) =>
            first.Content is not AssessmentAuthorityClaimContent.Fragment fragment
            || second.Content is AssessmentAuthorityClaimContent.Fragment other
                && fragment.Domain == other.Domain && fragment.Identity == other.Identity;
        private void DependencyFailure(AssessmentAuthorityReference reference)
        {
            if (_failure is AuthorityVerificationResult.Rejected)
            { _failure = new AuthorityVerificationResult.NotEstablished(_inputs, AuthorityNotEstablishedReason.DependencyAuthorityNotEstablished, reference); }
        }

        private AssessmentAuthorityDesignation? Trusted(AuthorityDesignationReference reference)
        {
            var designation = Find<AssessmentAuthorityDesignation>(reference);
            if (_failure is not null) { return null; }
            if (!_policy.TrustedDesignations.Contains(designation!))
            { _failure = new AuthorityVerificationResult.NotEstablished(_inputs, AuthorityNotEstablishedReason.MissingDesignation, reference); return null; }
            return designation;
        }
        private T? Find<T>(AssessmentAuthorityReference reference) where T : AssessmentAuthorityValue
        {
            if (!_values.TryGetValue(reference, out var value))
            { _failure = new AuthorityVerificationResult.NotEstablished(_inputs, reference is AuthorityDesignationReference
                ? AuthorityNotEstablishedReason.MissingDesignation : AuthorityNotEstablishedReason.MissingDependency, reference); return null; }
            if (value is T typed) { return typed; }
            _failure = Failed(AuthorityDiagnosticCode.ValueReferenceMismatch, reference); return null;
        }
        private void CheckGrant(AuthorityValues<AssessmentAuthorityGrant> grants, OperationalMetricCoverageInterval window,
            AssessmentAuthorityClaimKind kind, AssessmentAuthorityScope scope, OperationalMetricCoverageInterval period,
            DateTimeOffset issuedAt, AssessmentAuthorityReference reference)
        {
            if (!AuthoritySnapshotValidation.Contains(window, issuedAt)) { Reject(AuthorityRejectedReason.IssuanceOutsideWindow, reference); return; }
            var matches = grants.Where(grant => grant.Kind == kind).ToArray();
            if (matches.Length == 0) { Reject(AuthorityRejectedReason.ClaimKindDenied, reference); return; }
            matches = matches.Where(grant => grant.Scope.Contains(scope)).ToArray();
            if (matches.Length == 0) { Reject(AuthorityRejectedReason.ScopeDenied, reference); return; }
            matches = matches.Where(grant => AuthoritySnapshotValidation.Contains(grant.Period, period)).ToArray();
            if (matches.Length == 0) { Reject(AuthorityRejectedReason.PeriodDenied, reference); return; }
            if (period.StartsAtUtc < issuedAt && !matches.Any(static grant => grant.HistoricalIssuance))
            { Reject(AuthorityRejectedReason.HistoricalIssuanceDenied, reference); }
        }
        private void Reject(AuthorityRejectedReason reason, AssessmentAuthorityReference reference) =>
            _failure = new AuthorityVerificationResult.Rejected(_inputs, reason, reference);
        private AuthorityVerificationResult.Failed Failed(AuthorityDiagnosticCode code, AssessmentAuthorityReference reference) =>
            new(_inputs, new(AuthorityOperationErrorKind.CorruptContent, code, reference));
        private void Edge(AssessmentAuthorityReference from, AssessmentAuthorityReference to)
        {
            if (!_proofEdges.TryGetValue(from, out var targets)) { targets = []; _proofEdges.Add(from, targets); }
            targets.Add(to);
        }
        private bool HasProofCycle()
        {
            var active = new HashSet<AssessmentAuthorityReference>(); var done = new HashSet<AssessmentAuthorityReference>();
            foreach (var root in _proofEdges.Keys)
            {
                var pending = new Stack<(AssessmentAuthorityReference Reference, bool Exit)>(); pending.Push((root, false));
                while (pending.TryPop(out var item))
                {
                    _token.ThrowIfCancellationRequested();
                    if (item.Exit) { active.Remove(item.Reference); done.Add(item.Reference); continue; }
                    if (done.Contains(item.Reference)) { continue; }
                    if (!active.Add(item.Reference)) { return true; }
                    pending.Push((item.Reference, true));
                    if (_proofEdges.TryGetValue(item.Reference, out var targets))
                    { foreach (var target in targets) { pending.Push((target, false)); } }
                }
            }
            return false;
        }
    }
}
