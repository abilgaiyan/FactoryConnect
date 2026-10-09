using FactoryConnect.Abstractions;
using FactoryConnect.Core.AssessmentAuthority;

namespace FactoryConnect.Core.Tests;

public sealed class AssessmentAuthorityVerificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectAndDelegatedAuthorityProducesExactAdmission(bool delegated)
    {
        var fixture = new Fixture(delegated);
        var resolution = Assert.IsType<AuthorityInputResolution.Resolved>(await fixture.Resolve());
        var verifier = new AssessmentAuthorityVerifier();
        var result = Assert.IsType<AuthorityVerificationResult.Authorized>(verifier.Verify(resolution.Inputs, CancellationToken.None));
        Assert.True(result.Admission.Matches(fixture.Claim, Fixture.Cut, resolution.Inputs, fixture.Policy));
        Assert.Equal(result, verifier.Verify(resolution.Inputs, CancellationToken.None));
        Assert.False(result.Admission.Matches(fixture.NewClaim(new AssessmentAuthorityClaimContent.Schedule(
            new("schedule", "changed"), "resolver-v1", true, [])), Fixture.Cut, resolution.Inputs, fixture.Policy));
        Assert.False(result.Admission.Matches(fixture.Claim, Fixture.OtherCut, resolution.Inputs, fixture.Policy));
    }

    [Theory]
    [InlineData("kind", AuthorityRejectedReason.ClaimKindDenied)]
    [InlineData("scope", AuthorityRejectedReason.ScopeDenied)]
    [InlineData("period", AuthorityRejectedReason.PeriodDenied)]
    [InlineData("window", AuthorityRejectedReason.IssuanceOutsideWindow)]
    [InlineData("historical", AuthorityRejectedReason.HistoricalIssuanceDenied)]
    [InlineData("issuer", AuthorityRejectedReason.IssuerMismatch)]
    public async Task EstablishedDisqualificationIsRejected(string change, AuthorityRejectedReason expected)
    {
        var fixture = new Fixture();
        var grants = Fixture.Grants().ToArray();
        var window = Fixture.Window;
        if (change == "kind") { grants = [new(AssessmentAuthorityClaimKind.Gap, Fixture.Scope, Fixture.Day, true, true)]; }
        if (change == "scope") { grants = [new(AssessmentAuthorityClaimKind.ScheduleCompleteness,
            new AssessmentAuthorityScope.LineScope(new("company"), new("site"), new("other")), Fixture.Day, true, true)]; }
        if (change == "period") { grants = [new(AssessmentAuthorityClaimKind.ScheduleCompleteness, Fixture.Scope,
            new(Fixture.Time.AddHours(1), Fixture.Time.AddHours(2)), true, true)]; }
        if (change == "window") { window = new(Fixture.Time.AddHours(1), Fixture.Time.AddHours(2)); }
        if (change == "historical") { grants = [new(AssessmentAuthorityClaimKind.ScheduleCompleteness, Fixture.Scope, Fixture.Day, false, true)]; }
        var root = new AssessmentAuthorityDesignation(Fixture.RootReference, change == "issuer" ? Fixture.Delegate : Fixture.Root,
            Fixture.Root, "synthetic business designation", window, grants);
        fixture.ReplaceRoot(root);
        Assert.Equal(expected, Assert.IsType<AuthorityVerificationResult.Rejected>(await fixture.Verify()).Reason);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("period")]
    [InlineData("historical")]
    [InlineData("delegation")]
    [InlineData("window")]
    public async Task DelegationCannotExpandPermissions(string change)
    {
        var fixture = new Fixture(true);
        var grants = Fixture.Grants().ToArray();
        if (change == "scope") { grants = [new(AssessmentAuthorityClaimKind.ScheduleCompleteness,
            new AssessmentAuthorityScope.SiteScope(new("company"), new("site")), Fixture.Day, true, true)]; }
        if (change == "period") { grants = [new(AssessmentAuthorityClaimKind.ScheduleCompleteness, Fixture.Scope,
            new(Fixture.Day.StartsAtUtc.AddDays(-1), Fixture.Day.EndsAtUtc), true, true)]; }
        var rootGrants = Fixture.Grants().ToArray();
        if (change == "historical") { rootGrants = [new(AssessmentAuthorityClaimKind.ScheduleCompleteness, Fixture.Scope, Fixture.Day, false, true)]; }
        if (change == "delegation") { rootGrants = [new(AssessmentAuthorityClaimKind.ScheduleCompleteness, Fixture.Scope, Fixture.Day, true, false)]; }
        fixture.ReplaceRoot(new(Fixture.RootReference, Fixture.Root, Fixture.Root, "synthetic", Fixture.Window, rootGrants));
        fixture.Replace(new AssessmentAuthorityAuthorization(Fixture.AuthorizationReference, Fixture.Root, Fixture.Delegate,
            Fixture.RootReference, null, Fixture.Time.AddHours(-3), change == "window"
                ? new(Fixture.AuthorizationWindow.StartsAtUtc, Fixture.Window.EndsAtUtc.AddDays(1)) : Fixture.AuthorizationWindow, grants));
        Assert.IsType<AuthorityVerificationResult.Rejected>(await fixture.Verify());
    }

    [Fact]
    public async Task ExpiryAtVerificationDoesNotInvalidateClaimIssuedWithinWindow()
    {
        var fixture = new Fixture(true);
        fixture.Replace(new AssessmentAuthorityAuthorization(Fixture.AuthorizationReference, Fixture.Root, Fixture.Delegate,
            Fixture.RootReference, null, Fixture.Time.AddHours(-3),
            new(Fixture.Time.AddHours(-3), Fixture.Time.AddMinutes(-30)), Fixture.Grants()));
        Assert.IsType<AuthorityVerificationResult.Authorized>(await fixture.Verify());
    }

    [Fact]
    public async Task UnrequestedDependencyIsFailureRatherThanEstablishedAbsence()
    {
        var fixture = new Fixture(true);
        fixture.Values.RemoveAll(static value => value.Reference == Fixture.AuthorizationReference);
        var result = Assert.IsType<AuthorityInputResolution.Failed>(await fixture.Resolve());
        Assert.Equal(AuthorityDiagnosticCode.MissingLookup, result.Error.Code);
    }

    [Fact]
    public async Task DuplicateSelectionFailsBeforeReaderInvocation()
    {
        var fixture = new Fixture(false);
        var original = fixture.Request();
        var request = new AuthorityVerificationRequest(original.Claim, original.AggregationRevision, original.Policy,
            original.VerificationTimeUtc, original.Selections.Concat([original.Claim]));
        var result = await new AssessmentAuthorityInputResolver(new StubReader(_ => throw new InvalidOperationException()))
            .ResolveExactAsync(request, CancellationToken.None);
        Assert.Equal(AuthorityOperationErrorKind.MalformedRequest, Assert.IsType<AuthorityInputResolution.Failed>(result).Error.Kind);
    }

    [Fact]
    public async Task ValidNarrowedChildDelegationIsVerifiedAtIssuance()
    {
        var fixture = new Fixture(true);
        var childReference = new AuthorityAuthorizationReference(Fixture.Domain, "child", new("r1"));
        var childSubject = new AuthoritySubjectId(Fixture.Domain, "child-subject");
        fixture.Values.Add(new AssessmentAuthorityAuthorization(childReference, Fixture.Delegate, childSubject,
            Fixture.RootReference, Fixture.AuthorizationReference, Fixture.Time.AddHours(-2),
            new(Fixture.Time.AddHours(-2), Fixture.Time.AddHours(2)),
            [new(AssessmentAuthorityClaimKind.ScheduleCompleteness, Fixture.Scope, Fixture.Period, true, false)]));
        fixture.ReplaceClaim(new(fixture.Claim.Reference as AuthorityClaimReference ?? throw new InvalidOperationException(), childSubject,
            childReference, Fixture.Time.AddHours(-1), Fixture.Scope, Fixture.Period, Fixture.Cut, fixture.Claim.Content));
        fixture.ReplaceCompleteness([Fixture.AuthorizationReference, childReference]);
        Assert.IsType<AuthorityVerificationResult.Authorized>(await fixture.Verify());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("links")]
    [InlineData("horizon")]
    [InlineData("source")]
    public async Task EmptyRevocationLookupRequiresApplicableCompleteProof(string change)
    {
        var fixture = new Fixture(true);
        if (change == "none") { fixture.Values.RemoveAll(static item => item is AssessmentAuthorityRevocationCompleteness); }
        else { fixture.ReplaceCompleteness(change == "links" ? [] : [Fixture.AuthorizationReference],
            change == "horizon" ? Fixture.Time.AddTicks(-1) : Fixture.Time,
            change == "source" ? Fixture.Delegate : Fixture.Root); }
        Assert.Equal(AuthorityNotEstablishedReason.CompletenessInsufficient,
            Assert.IsType<AuthorityVerificationResult.NotEstablished>(await fixture.Verify()).Reason);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RevocationEffectAndHistoricalIssuanceAreExplicit(bool retrospective, bool affectsClaim)
    {
        var fixture = new Fixture(true);
        var reference = new AuthorityRevocationReference(Fixture.Domain, "decision", new("r1"));
        var issued = retrospective ? Fixture.Time : Fixture.Time.AddHours(-2);
        var effective = affectsClaim ? Fixture.Time.AddHours(-1) : Fixture.Time.AddMinutes(-30);
        fixture.Values.Add(new AssessmentAuthorityRevocation(reference, Fixture.Root, Fixture.RootReference,
            [Fixture.AuthorizationReference], issued, effective,
            retrospective ? AssessmentAuthorityRevocationEffect.Retrospective : AssessmentAuthorityRevocationEffect.Prospective,
            Fixture.Scope, Fixture.Day));
        fixture.ReplaceCompleteness([Fixture.AuthorizationReference], decisions: [reference]);
        var result = await fixture.Verify();
        if (affectsClaim) { Assert.Equal(AuthorityRejectedReason.Revoked, Assert.IsType<AuthorityVerificationResult.Rejected>(result).Reason); }
        else { Assert.IsType<AuthorityVerificationResult.Authorized>(result); }
    }

    [Fact]
    public async Task UnauthorizedRetrospectiveDecisionCannotEstablishRevokedFinding()
    {
        var fixture = new Fixture(true);
        fixture.ReplaceRoot(new(Fixture.RootReference, Fixture.Root, Fixture.Root, "synthetic", Fixture.Window,
            Fixture.Grants().Where(static item => item.Kind != AssessmentAuthorityClaimKind.RetrospectiveRevocation)));
        fixture.Replace(new AssessmentAuthorityAuthorization(Fixture.AuthorizationReference, Fixture.Root, Fixture.Delegate,
            Fixture.RootReference, null, Fixture.Time.AddHours(-3), Fixture.AuthorizationWindow,
            Fixture.Grants().Where(static item => item.Kind != AssessmentAuthorityClaimKind.RetrospectiveRevocation)));
        var reference = new AuthorityRevocationReference(Fixture.Domain, "decision", new("1"));
        fixture.Values.Add(new AssessmentAuthorityRevocation(reference, Fixture.Root, Fixture.RootReference,
            [Fixture.AuthorizationReference], Fixture.Time, Fixture.Time.AddHours(-1),
            AssessmentAuthorityRevocationEffect.Retrospective, Fixture.Scope, Fixture.Day));
        fixture.ReplaceCompleteness([Fixture.AuthorizationReference], decisions: [reference]);
        Assert.Equal(AuthorityNotEstablishedReason.DependencyAuthorityNotEstablished,
            Assert.IsType<AuthorityVerificationResult.NotEstablished>(await fixture.Verify()).Reason);
    }

    [Fact]
    public async Task RevokedDecisionIssuerCannotEstablishPrimaryRevocation()
    {
        var fixture = new Fixture(true);
        var issuer = new AuthoritySubjectId(Fixture.Domain, "decision-issuer");
        var authorization = new AuthorityAuthorizationReference(Fixture.Domain, "decision-issuer-authorization", new("1"));
        var decision = new AuthorityRevocationReference(Fixture.Domain, "primary-revocation", new("1"));
        var issuerRevocation = new AuthorityRevocationReference(Fixture.Domain, "issuer-revocation", new("1"));
        fixture.Values.Add(new AssessmentAuthorityAuthorization(authorization, Fixture.Root, issuer,
            Fixture.RootReference, null, Fixture.Time.AddHours(-3), Fixture.AuthorizationWindow,
            [new(AssessmentAuthorityClaimKind.RetrospectiveRevocation, Fixture.Scope, Fixture.Day, true, false)]));
        fixture.Values.Add(new AssessmentAuthorityRevocation(decision, issuer, authorization,
            [Fixture.AuthorizationReference], Fixture.Time, Fixture.Time.AddHours(-1),
            AssessmentAuthorityRevocationEffect.Retrospective, Fixture.Scope, Fixture.Day));
        fixture.Values.Add(new AssessmentAuthorityRevocation(issuerRevocation, Fixture.Root, Fixture.RootReference,
            [authorization], Fixture.Time.AddHours(-2), Fixture.Time.AddHours(-1),
            AssessmentAuthorityRevocationEffect.Prospective, Fixture.Scope, Fixture.Day));
        fixture.ReplaceCompleteness([Fixture.AuthorizationReference], decisions: [decision]);
        fixture.Values.Add(new AssessmentAuthorityRevocationCompleteness(new(Fixture.Domain, "issuer-completeness", new("1")),
            Fixture.Root, [authorization], Fixture.Time, Fixture.Root, Fixture.RootReference, Fixture.Time,
            Fixture.Scope, Fixture.Day, [issuerRevocation]));
        Assert.Equal(AuthorityNotEstablishedReason.DependencyAuthorityNotEstablished,
            Assert.IsType<AuthorityVerificationResult.NotEstablished>(await fixture.Verify()).Reason);
    }

    [Fact]
    public async Task GrantsCannotCombineUnrelatedScopeAndPeriodPermissions()
    {
        var fixture = new Fixture(false);
        var other = new AssessmentAuthorityScope.MachineScope(new("company"), new("site"), new("line"), new(Guid.NewGuid()));
        fixture.ReplaceRoot(new(Fixture.RootReference, Fixture.Root, Fixture.Root, "synthetic", Fixture.Window,
            [new(AssessmentAuthorityClaimKind.ScheduleCompleteness, Fixture.Scope,
                new(Fixture.Time, Fixture.Time.AddHours(1)), true, false),
             new(AssessmentAuthorityClaimKind.ScheduleCompleteness, other, Fixture.Day, true, false)]));
        Assert.Equal(AuthorityRejectedReason.PeriodDenied, Assert.IsType<AuthorityVerificationResult.Rejected>(await fixture.Verify()).Reason);
    }

    [Fact]
    public async Task RevocationManifestCannotOmitKnownDecision()
    {
        var fixture = new Fixture(true);
        fixture.Values.Add(new AssessmentAuthorityRevocation(new(Fixture.Domain, "omitted", new("1")), Fixture.Root,
            Fixture.RootReference, [Fixture.AuthorizationReference], Fixture.Time, Fixture.Time.AddHours(-1),
            AssessmentAuthorityRevocationEffect.Retrospective, Fixture.Scope, Fixture.Day));
        Assert.Equal(AuthorityDiagnosticCode.InvalidCompletenessManifest,
            Assert.IsType<AuthorityVerificationResult.Failed>(await fixture.Verify()).Error.Code);
    }

    [Fact]
    public async Task SelfDependentCompletenessCannotAuthorize()
    {
        var fixture = new Fixture(true);
        fixture.Replace(new AssessmentAuthorityRevocationCompleteness(Fixture.CompletenessReference, Fixture.Root,
            [Fixture.AuthorizationReference], Fixture.Time, Fixture.Delegate, Fixture.AuthorizationReference,
            Fixture.Time, Fixture.Scope, Fixture.Day, []));
        Assert.Equal(AuthorityDiagnosticCode.DependencyCycle, Assert.IsType<AuthorityVerificationResult.Failed>(await fixture.Verify()).Error.Code);
    }

    [Fact]
    public async Task MissingAnchorIsNotEstablishedAndStoredDesignationIsNotAutomaticallyTrusted()
    {
        var fixture = new Fixture();
        fixture.Replace(new AssessmentAuthorityPolicy(Fixture.PolicyReference, AssessmentAuthorityPolicy.V1Implementation, 1, []));
        Assert.Equal(AuthorityNotEstablishedReason.MissingDesignation,
            Assert.IsType<AuthorityVerificationResult.NotEstablished>(await fixture.Verify()).Reason);
    }

    [Fact]
    public async Task EstablishedAbsenceRetainsExactReference()
    {
        var fixture = new Fixture();
        fixture.Values.Remove(fixture.Claim);
        var resolution = Assert.IsType<AuthorityInputResolution.Resolved>(await fixture.Resolve(extra: [fixture.Claim.Reference]));
        var absent = Assert.Single(resolution.Inputs.Lookups.OfType<AuthorityExactLookup.Absent>());
        Assert.Equal(fixture.Claim.Reference, absent.RequestedReference);
        Assert.Equal(AuthorityNotEstablishedReason.MissingDependency,
            Assert.IsType<AuthorityVerificationResult.NotEstablished>(new AssessmentAuthorityVerifier().Verify(resolution.Inputs, CancellationToken.None)).Reason);
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("mismatch")]
    [InlineData("conflict")]
    [InlineData("unavailable")]
    public async Task VerifierDoesNotTrustResolvedSnapshotShape(string defect)
    {
        var fixture = new Fixture();
        var inputs = Assert.IsType<AuthorityInputResolution.Resolved>(await fixture.Resolve()).Inputs;
        var lookups = inputs.Lookups.ToList();
        if (defect == "omitted") { lookups.RemoveAt(0); }
        if (defect == "mismatch") { lookups[0] = new AuthorityExactLookup.Found(lookups[0].RequestedReference, fixture.Policy); }
        if (defect == "conflict") { lookups.Add(new AuthorityExactLookup.Found(fixture.Claim.Reference,
            fixture.NewClaim(new AssessmentAuthorityClaimContent.Schedule(new("schedule", "new"), "v1", true, [])))); }
        if (defect == "unavailable") { lookups[0] = new AuthorityExactLookup.Unavailable(lookups[0].RequestedReference, AuthorityUnavailableReason.RetrievalUnavailable); }
        Assert.IsType<AuthorityVerificationResult.Failed>(new AssessmentAuthorityVerifier().Verify(new(inputs.Request, lookups), CancellationToken.None));
    }

    [Fact]
    public async Task ResolverPreservesUnavailableRequestAndRejectsLookupMismatch()
    {
        var fixture = new Fixture();
        var request = fixture.Request();
        var unavailable = new StubReader(reference => new AuthorityExactLookup.Unavailable(reference, AuthorityUnavailableReason.RetrievalUnavailable));
        Assert.Equal(request, Assert.IsType<AuthorityInputResolution.Unavailable>(await new AssessmentAuthorityInputResolver(unavailable)
            .ResolveExactAsync(request, CancellationToken.None)).Request);
        var mismatch = new StubReader(_ => new AuthorityExactLookup.Absent(Fixture.PolicyReference));
        Assert.Equal(AuthorityOperationErrorKind.ReferenceMismatch,
            Assert.IsType<AuthorityInputResolution.Failed>(await new AssessmentAuthorityInputResolver(mismatch)
                .ResolveExactAsync(request, CancellationToken.None)).Error.Kind);
    }

    [Fact]
    public async Task CancellationDuringResolutionAndVerificationNeverProducesAdmission()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var reader = new StubReader(reference => { cancellation.Cancel(); return new AuthorityExactLookup.Absent(reference); });
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AssessmentAuthorityInputResolver(reader)
            .ResolveExactAsync(fixture.Request(), cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var inputs = Assert.IsType<AuthorityInputResolution.Resolved>(await fixture.Resolve()).Inputs;
        Assert.ThrowsAny<OperationCanceledException>(() => new AssessmentAuthorityVerifier().Verify(inputs, cancellation.Token));
    }

    [Theory]
    [InlineData("implementation")]
    [InlineData("representation")]
    public async Task UnsupportedPolicyFailsExplicitly(string defect)
    {
        var fixture = new Fixture();
        fixture.Replace(new AssessmentAuthorityPolicy(Fixture.PolicyReference,
            defect == "implementation" ? "future-policy" : AssessmentAuthorityPolicy.V1Implementation,
            defect == "representation" ? 2 : 1, [fixture.RootDesignation]));
        Assert.Equal(AuthorityOperationErrorKind.UnsupportedRepresentation,
            Assert.IsType<AuthorityInputResolution.Failed>(await fixture.Resolve()).Error.Kind);
    }

    [Fact]
    public async Task ConflictingClaimSelectionsRequireExplicitResolution()
    {
        var fixture = new Fixture();
        fixture.Values.Add(new AssessmentAuthorityClaim(new(Fixture.Domain, "conflicting", new("1")), Fixture.Root,
            Fixture.RootReference, Fixture.Time.AddHours(-1), Fixture.Scope, Fixture.Period, Fixture.Cut,
            new AssessmentAuthorityClaimContent.Schedule(new("schedule", "other"), "v1", true, [])));
        Assert.Equal(AuthorityNotEstablishedReason.ConflictUnresolved,
            Assert.IsType<AuthorityVerificationResult.NotEstablished>(await fixture.Verify()).Reason);
    }

    [Fact]
    public async Task AccountedUnknownStateIsAFragmentNotAMissingFactGap()
    {
        var fixture = new Fixture();
        fixture.ReplaceClaim(fixture.NewClaim(new AssessmentAuthorityClaimContent.Fragment(Fixture.Domain, "fragment", "1",
            Fixture.Period, MachineState.Unknown, null, [new(1)], [new("activity", "source", "1")])));
        var result = Assert.IsType<AuthorityVerificationResult.Authorized>(await fixture.Verify());
        Assert.IsType<AssessmentAuthorityClaimContent.Fragment>(result.Admission.Claim.Content);
    }

    [Fact]
    public async Task LaterAuthorityCannotAdmitPostCutContributions()
    {
        var fixture = new Fixture();
        fixture.ReplaceClaim(fixture.NewClaim(new AssessmentAuthorityClaimContent.Fragment(Fixture.Domain, "fragment", "1",
            Fixture.Period, MachineState.Running, true, [new(3244)], [new("activity", "source", "later")])));
        Assert.Equal(AuthorityDiagnosticCode.InvalidClaimContent,
            Assert.IsType<AuthorityInputResolution.Failed>(await fixture.Resolve()).Error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionAccountsForFragmentsAndExplicitGaps(bool leaveHole)
    {
        var fixture = new Fixture();
        var split = Fixture.Period.StartsAtUtc.AddMinutes(30);
        var fragmentRef = new AuthorityClaimReference(Fixture.Domain, "fragment", new("1"));
        var gapRef = new AuthorityClaimReference(Fixture.Domain, "gap", new("1"));
        fixture.Values.Add(new AssessmentAuthorityClaim(fragmentRef, Fixture.Root, Fixture.RootReference, Fixture.Time.AddHours(-1),
            Fixture.Scope, Fixture.Period, Fixture.Cut, new AssessmentAuthorityClaimContent.Fragment(Fixture.Domain, "fragment", "1",
                new(Fixture.Period.StartsAtUtc, split), MachineState.Unknown, true, [], [new("activity", "id", "1")])));
        if (!leaveHole) { fixture.Values.Add(new AssessmentAuthorityClaim(gapRef, Fixture.Root, Fixture.RootReference,
            Fixture.Time.AddHours(-1), Fixture.Scope, Fixture.Period, Fixture.Cut,
            new AssessmentAuthorityClaimContent.Gap(new(split, Fixture.Period.EndsAtUtc), [new("gap", "id", "1")]))); }
        fixture.ReplaceClaim(fixture.NewClaim(new AssessmentAuthorityClaimContent.Completion(Fixture.Period.EndsAtUtc,
            leaveHole ? [fragmentRef] : [fragmentRef, gapRef])));
        var result = await fixture.Verify();
        if (leaveHole) { Assert.IsType<AuthorityVerificationResult.NotEstablished>(result); }
        else { Assert.IsType<AuthorityVerificationResult.Authorized>(result); }
    }

    [Fact]
    public void ReferencesAndCollectionsPreserveExactStringsOrderAndFragmentation()
    {
        var first = new AuthorityClaimReference(Fixture.Domain, "id\ud800", new("01"));
        Assert.NotEqual(first, new AuthorityClaimReference(Fixture.Domain, "id\ud800", new("1")));
        Assert.NotEqual(first, new AuthorityClaimReference(new("OTHER"), "id\ud800", new("01")));
        var items = new List<AuthorityClaimReference> { first };
        var copy = new AuthorityValues<AuthorityClaimReference>(items); items.Clear();
        Assert.Single(copy);
        Assert.Equal(copy, new AuthorityValues<AuthorityClaimReference>([first]));
        Assert.NotEqual(new AuthorityValues<OperationalMetricCoverageInterval>([Fixture.Period]),
            new AuthorityValues<OperationalMetricCoverageInterval>([new(Fixture.Period.StartsAtUtc, Fixture.Period.StartsAtUtc.AddTicks(1)),
                new(Fixture.Period.StartsAtUtc.AddTicks(1), Fixture.Period.EndsAtUtc)]));
        Assert.Throws<ArgumentException>(() => new AssessmentAuthorityScope.SiteScope(default, new("site")));
        Assert.Throws<ArgumentException>(() => new AuthorityClaimRevision(""));
    }

    [Fact]
    public async Task RetainedInputsReproduceAfterReaderChanges()
    {
        var fixture = new Fixture(true);
        var snapshot = Assert.IsType<AuthorityInputResolution.Resolved>(await fixture.Resolve()).Inputs;
        var verifier = new AssessmentAuthorityVerifier();
        var original = verifier.Verify(snapshot, CancellationToken.None);
        fixture.Values.Clear();
        Assert.Equal(original, verifier.Verify(snapshot, CancellationToken.None));
        Assert.Equal(Fixture.Time, snapshot.Request.VerificationTimeUtc);
    }

    private sealed class StubReader(Func<AssessmentAuthorityReference, AuthorityExactLookup> read) : IAssessmentAuthorityExactReader
    {
        public ValueTask<AuthorityExactLookup> ReadExactAsync(AssessmentAuthorityReference reference, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(read(reference)); }
    }

    private sealed class Fixture
    {
        internal static readonly AuthorityDomainId Domain = new("synthetic-test-only");
        internal static readonly AuthoritySubjectId Root = new(Domain, "root");
        internal static readonly AuthoritySubjectId Delegate = new(Domain, "delegate");
        internal static readonly AuthorityDesignationReference RootReference = new(Domain, "designation", new("1"));
        internal static readonly AuthorityAuthorizationReference AuthorizationReference = new(Domain, "authorization", new("1"));
        internal static readonly AuthorityCompletenessReference CompletenessReference = new(Domain, "completeness", new("1"));
        internal static readonly AuthorityPolicyReference PolicyReference = new(Domain, "policy", new("1"));
        internal static readonly DateTimeOffset Time = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        internal static readonly OperationalMetricCoverageInterval Day = new(Time.AddHours(-12), Time.AddHours(12));
        internal static readonly OperationalMetricCoverageInterval Period = new(Time.AddHours(-4), Time.AddHours(-3));
        internal static readonly OperationalMetricCoverageInterval Window = Day;
        internal static readonly OperationalMetricCoverageInterval AuthorizationWindow = new(Time.AddHours(-3), Time.AddHours(2));
        internal static readonly AssessmentAuthorityScope Scope = new AssessmentAuthorityScope.MachineScope(new("company"), new("site"), new("line"), new(Guid.Parse("4b60457b-745b-48bf-a842-fdf32481be86")));
        internal static readonly MetricAggregationCheckpoint Cut = new(new("aggregation"), new(((AssessmentAuthorityScope.MachineScope)Scope).Machine, "metrics"), new(3243));
        internal static readonly MetricAggregationCheckpoint OtherCut = new(Cut.ProcessorId, Cut.StreamId, new(3244));
        internal List<AssessmentAuthorityValue> Values { get; } = [];
        internal AssessmentAuthorityClaim Claim { get; private set; }
        internal AssessmentAuthorityDesignation RootDesignation => Values.OfType<AssessmentAuthorityDesignation>().Single();
        internal AssessmentAuthorityPolicy Policy => Values.OfType<AssessmentAuthorityPolicy>().Single();
        internal Fixture(bool delegated = false)
        {
            var root = new AssessmentAuthorityDesignation(RootReference, Root, Root, "synthetic business designation", Window, Grants());
            Values.Add(root); Values.Add(new AssessmentAuthorityPolicy(PolicyReference, AssessmentAuthorityPolicy.V1Implementation, 1, [root]));
            Claim = new(new(Domain, "claim", new("1")), delegated ? Delegate : Root, delegated ? AuthorizationReference : RootReference,
                Time.AddHours(-1), Scope, Period, Cut, new AssessmentAuthorityClaimContent.Schedule(new("schedule", "1"), "resolver-v1", true, [Period]));
            Values.Add(Claim);
            if (delegated)
            {
                Values.Add(new AssessmentAuthorityAuthorization(AuthorizationReference, Root, Delegate, RootReference, null,
                    Time.AddHours(-3), AuthorizationWindow, Grants()));
                ReplaceCompleteness([AuthorizationReference]);
            }
        }
        internal static IEnumerable<AssessmentAuthorityGrant> Grants() => Enum.GetValues<AssessmentAuthorityClaimKind>()
            .Select(kind => new AssessmentAuthorityGrant(kind, Scope, Day, true, true));
        internal void Replace(AssessmentAuthorityValue value)
        { Values.RemoveAll(item => item.Reference == value.Reference); Values.Add(value); }
        internal void ReplaceRoot(AssessmentAuthorityDesignation root)
        { Replace(root); Replace(new AssessmentAuthorityPolicy(PolicyReference, AssessmentAuthorityPolicy.V1Implementation, 1, [root])); }
        internal void ReplaceClaim(AssessmentAuthorityClaim claim) { Replace(claim); Claim = claim; }
        internal AssessmentAuthorityClaim NewClaim(AssessmentAuthorityClaimContent content) => new((AuthorityClaimReference)Claim.Reference,
            Claim.Issuer, Claim.Authorization, Claim.IssuedAtUtc, Claim.Scope, Claim.Period, Claim.AggregationRevision, content);
        internal void ReplaceCompleteness(IEnumerable<AuthorityAuthorizationReference> links, DateTimeOffset? through = null,
            AuthoritySubjectId? source = null, IEnumerable<AuthorityRevocationReference>? decisions = null) =>
            Replace(new AssessmentAuthorityRevocationCompleteness(CompletenessReference, source ?? Root, links, through ?? Time,
                Root, RootReference, Time, Scope, Day, decisions ?? []));
        internal AuthorityVerificationRequest Request(IEnumerable<AssessmentAuthorityReference>? extra = null) =>
            new((AuthorityClaimReference)Claim.Reference, Cut, PolicyReference, Time,
                Values.Select(static item => item.Reference).Concat(extra ?? []).Distinct());
        internal async Task<AuthorityInputResolution> Resolve(IEnumerable<AssessmentAuthorityReference>? extra = null) =>
            await new AssessmentAuthorityInputResolver(new StubReader(reference => Values.SingleOrDefault(item => item.Reference == reference) is { } value
                ? new AuthorityExactLookup.Found(reference, value) : new AuthorityExactLookup.Absent(reference)))
                .ResolveExactAsync(Request(extra), CancellationToken.None);
        internal async Task<AuthorityVerificationResult> Verify()
        { var resolved = Assert.IsType<AuthorityInputResolution.Resolved>(await Resolve()); return new AssessmentAuthorityVerifier().Verify(resolved.Inputs, CancellationToken.None); }
    }
}
