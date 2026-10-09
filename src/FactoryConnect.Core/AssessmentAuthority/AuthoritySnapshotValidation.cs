using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.AssessmentAuthority;

internal static class AuthoritySnapshotValidation
{
    internal static AuthorityOperationError? ValidateRequest(AuthorityVerificationRequest request)
    {
        if (!request.Selections.Contains(request.Claim) || !request.Selections.Contains(request.Policy))
        { return new(AuthorityOperationErrorKind.MalformedRequest, AuthorityDiagnosticCode.InvalidSelection); }
        if (request.Selections.Distinct().Count() != request.Selections.Count)
        { return new(AuthorityOperationErrorKind.MalformedRequest, AuthorityDiagnosticCode.DuplicateReference); }
        return null;
    }

    internal static AuthorityOperationError? Validate(ResolvedAuthorityVerificationInputs inputs, CancellationToken cancellationToken)
    {
        var invalid = ValidateRequest(inputs.Request);
        if (invalid is not null) { return invalid; }
        var map = new Dictionary<AssessmentAuthorityReference, AuthorityExactLookup>();
        foreach (var lookup in inputs.Lookups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!inputs.Request.Selections.Contains(lookup.RequestedReference))
            { return Error(AuthorityDiagnosticCode.UnexpectedLookup, lookup.RequestedReference); }
            if (map.TryGetValue(lookup.RequestedReference, out var previous))
            {
                return Error(previous == lookup ? AuthorityDiagnosticCode.DuplicateReference
                    : AuthorityDiagnosticCode.ConflictingImmutableValues, lookup.RequestedReference);
            }
            if (lookup is not AuthorityExactLookup.Found and not AuthorityExactLookup.Absent)
            { return Error(AuthorityDiagnosticCode.MissingLookup, lookup.RequestedReference); }
            if (lookup is AuthorityExactLookup.Found found)
            {
                if (found.RequestedReference != found.Value.Reference)
                { return new(AuthorityOperationErrorKind.ReferenceMismatch, AuthorityDiagnosticCode.ValueReferenceMismatch, found.RequestedReference); }
                var contentError = ValidateValue(found.Value, inputs.Request.VerificationTimeUtc);
                if (contentError is not null) { return contentError; }
            }
            map.Add(lookup.RequestedReference, lookup);
        }
        foreach (var reference in inputs.Request.Selections)
        {
            if (!map.ContainsKey(reference)) { return Error(AuthorityDiagnosticCode.MissingLookup, reference); }
        }
        foreach (var found in map.Values.OfType<AuthorityExactLookup.Found>())
        {
            foreach (var dependency in Dependencies(found.Value))
            { if (!map.ContainsKey(dependency)) { return Error(AuthorityDiagnosticCode.MissingLookup, dependency); } }
        }
        // Iterative DFS avoids stack overflow on large untrusted graphs. Presence is not authority.
        var finished = new HashSet<AssessmentAuthorityReference>();
        var active = new HashSet<AssessmentAuthorityReference>();
        foreach (var reference in inputs.Request.Selections)
        {
            var pending = new Stack<(AssessmentAuthorityReference Reference, bool Exit)>(); pending.Push((reference, false));
            while (pending.TryPop(out var item))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Exit) { active.Remove(item.Reference); finished.Add(item.Reference); continue; }
                if (finished.Contains(item.Reference)) { continue; }
                if (!active.Add(item.Reference)) { return Error(AuthorityDiagnosticCode.DependencyCycle, item.Reference); }
                pending.Push((item.Reference, true));
                if (map[item.Reference] is AuthorityExactLookup.Found found)
                { foreach (var dependency in Dependencies(found.Value).Reverse()) { pending.Push((dependency, false)); } }
            }
        }
        return null;
    }

    internal static IEnumerable<AssessmentAuthorityReference> Dependencies(AssessmentAuthorityValue value) => value switch
    {
        AssessmentAuthorityClaim claim => new[] { claim.Authorization }.Concat(
            claim.Content is AssessmentAuthorityClaimContent.Completion completion ? completion.AccountedClaims : []),
        AssessmentAuthorityAuthorization authorization => authorization.Parent is null
            ? [authorization.Anchor] : [authorization.Anchor, authorization.Parent],
        AssessmentAuthorityRevocation revocation => new[] { revocation.Authorization }.Concat(revocation.Targets),
        AssessmentAuthorityRevocationCompleteness completeness => new[] { completeness.Authorization }
            .Concat(completeness.CoveredLinks).Concat(completeness.Decisions),
        AssessmentAuthorityPolicy policy => policy.TrustedDesignations.Select(static item => item.Reference),
        AssessmentAuthorityDesignation => [],
        _ => [],
    };

    private static AuthorityOperationError? ValidateValue(AssessmentAuthorityValue value, DateTimeOffset verificationTime)
    {
        switch (value)
        {
            case AssessmentAuthorityPolicy policy:
                if (policy.Implementation != AssessmentAuthorityPolicy.V1Implementation || policy.RepresentationVersion != 1)
                { return new(AuthorityOperationErrorKind.UnsupportedRepresentation, AuthorityDiagnosticCode.UnsupportedPolicy, policy.Reference); }
                if (policy.TrustedDesignations.Select(static item => item.Reference).Distinct().Count() != policy.TrustedDesignations.Count)
                { return Error(AuthorityDiagnosticCode.ConflictingImmutableValues, policy.Reference); }
                break;
            case AssessmentAuthorityClaim claim:
                if (claim.IssuedAtUtc > verificationTime) { return Error(AuthorityDiagnosticCode.InvalidTemporalBounds, claim.Reference); }
                switch (claim.Content)
                {
                    case AssessmentAuthorityClaimContent.Schedule schedule:
                        if (!ValidIntervals(schedule.ExpectedIntervals, claim.Period)) { return Error(AuthorityDiagnosticCode.InvalidClaimContent, claim.Reference); }
                        break;
                    case AssessmentAuthorityClaimContent.Fragment fragment:
                        if (!Contains(claim.Period, fragment.Interval) || fragment.Provenance.Count == 0
                            || fragment.ContributionPositions.Any(position => position.Value == 0 || position.Value > claim.AggregationRevision.Position.Value)
                            || fragment.ContributionPositions.Distinct().Count() != fragment.ContributionPositions.Count)
                        { return Error(AuthorityDiagnosticCode.InvalidClaimContent, claim.Reference); }
                        break;
                    case AssessmentAuthorityClaimContent.Gap gap:
                        if (!Contains(claim.Period, gap.Interval) || gap.Evidence.Count == 0)
                        { return Error(AuthorityDiagnosticCode.InvalidClaimContent, claim.Reference); }
                        break;
                    case AssessmentAuthorityClaimContent.Completion completion:
                        if (completion.BoundaryUtc != claim.Period.EndsAtUtc || completion.BoundaryUtc > claim.IssuedAtUtc
                            || completion.AccountedClaims.Distinct().Count() != completion.AccountedClaims.Count)
                        { return Error(AuthorityDiagnosticCode.InvalidClaimContent, claim.Reference); }
                        break;
                    default: return new(AuthorityOperationErrorKind.UnsupportedRepresentation, AuthorityDiagnosticCode.UnsupportedValue, claim.Reference);
                }
                break;
            case AssessmentAuthorityAuthorization authorization:
                if (authorization.IssuedAtUtc > verificationTime || authorization.IssuanceWindow.StartsAtUtc < authorization.IssuedAtUtc)
                { return Error(AuthorityDiagnosticCode.InvalidTemporalBounds, authorization.Reference); }
                break;
            case AssessmentAuthorityRevocation decision:
                if (decision.IssuedAtUtc > verificationTime || decision.Targets.Count == 0
                    || decision.Targets.Distinct().Count() != decision.Targets.Count
                    || decision.Effect == AssessmentAuthorityRevocationEffect.Prospective && decision.EffectiveAtUtc < decision.IssuedAtUtc)
                { return Error(AuthorityDiagnosticCode.InvalidTemporalBounds, decision.Reference); }
                break;
            case AssessmentAuthorityRevocationCompleteness completeness:
                if (completeness.CompleteThroughUtc > completeness.IssuedAtUtc || completeness.IssuedAtUtc > verificationTime)
                { return Error(AuthorityDiagnosticCode.InvalidTemporalBounds, completeness.Reference); }
                if (completeness.CoveredLinks.Distinct().Count() != completeness.CoveredLinks.Count
                    || completeness.Decisions.Distinct().Count() != completeness.Decisions.Count)
                { return Error(AuthorityDiagnosticCode.InvalidCompletenessManifest, completeness.Reference); }
                break;
            case AssessmentAuthorityDesignation: break;
            default: return new(AuthorityOperationErrorKind.UnsupportedRepresentation, AuthorityDiagnosticCode.UnsupportedValue, value.Reference);
        }
        return null;
    }

    internal static bool Contains(OperationalMetricCoverageInterval outer, OperationalMetricCoverageInterval inner) =>
        outer.StartsAtUtc <= inner.StartsAtUtc && outer.EndsAtUtc >= inner.EndsAtUtc;
    internal static bool Contains(OperationalMetricCoverageInterval interval, DateTimeOffset instant) =>
        interval.StartsAtUtc <= instant && instant < interval.EndsAtUtc;
    private static bool ValidIntervals(IEnumerable<OperationalMetricCoverageInterval> intervals, OperationalMetricCoverageInterval extent)
    {
        OperationalMetricCoverageInterval? previous = null;
        foreach (var interval in intervals)
        {
            if (!Contains(extent, interval) || previous is not null && previous.EndsAtUtc > interval.StartsAtUtc) { return false; }
            previous = interval;
        }
        return true;
    }
    private static AuthorityOperationError Error(AuthorityDiagnosticCode code, AssessmentAuthorityReference reference) =>
        new(AuthorityOperationErrorKind.CorruptContent, code, reference);
}
