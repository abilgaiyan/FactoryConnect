using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

public sealed class CurrentMachineStateContractTests
{
    [Fact]
    public void AuthorityBindingRequiresExactMachineStreamCorrelation()
    {
        var machineId = MachineId.New();
        var otherMachineId = MachineId.New();
        var streamId = new ObservationStreamId(otherMachineId, "primary");

        Assert.Throws<ArgumentException>(() =>
            new CurrentStateAuthorityBinding(
                machineId,
                streamId,
                new ObservationProcessorId("mapper"),
                new ObservationProcessorId("state")));
    }

    [Fact]
    public void AuthorityCutRepresentsMissingAuthoritiesByAbsence()
    {
        var binding = CreateBinding();

        var cut = new CurrentStateAuthorityCut(binding, null, null, null);

        Assert.Same(binding, cut.Binding);
        Assert.Null(cut.AcquisitionContact);
        Assert.Null(cut.MappingCoverage);
        Assert.Null(cut.Evaluation);
    }

    [Fact]
    public void AuthorityRevisionDomainsAreDistinctAndRejectZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AcquisitionAuthorityRevision(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MappingAuthorityRevision(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StateProjectionAuthorityRevision(0));

        Assert.NotEqual(
            typeof(AcquisitionAuthorityRevision),
            typeof(MappingAuthorityRevision));
        Assert.NotEqual(
            typeof(MappingAuthorityRevision),
            typeof(StateProjectionAuthorityRevision));
    }

    [Fact]
    public void StableCutUnavailableCarriesNoPartialCutOrReason()
    {
        var result = StableCurrentStateAuthorityCutUnavailable.Instance;
        var properties = result.GetType().GetProperties();

        Assert.Empty(properties);
        Assert.IsAssignableFrom<CurrentStateAuthorityCutReadResult>(result);
    }

    [Fact]
    public void EvaluationAuthorityRetainsContinuityAttribution()
    {
        var binding = CreateBinding();
        var policy = new CurrentStatePolicyReference("state-continuity", "1");
        var evaluation = new EvaluationAuthority(
            binding.StateProcessorId,
            binding.ObservationStreamId,
            new ObservationPosition(12),
            MachineState.Running,
            44,
            policy,
            new StateProjectionAuthorityRevision(7));

        Assert.Equal(44UL, evaluation.LastConsumedInstanceId);
        Assert.Same(policy, evaluation.AppliedContinuityPolicy);
        Assert.Equal(MachineState.Running, evaluation.MachineState);
    }

    [Fact]
    public void OfflineRemainsStructurallyRepresentableInEvaluationAuthority()
    {
        var binding = CreateBinding();

        var evaluation = new EvaluationAuthority(
            binding.StateProcessorId,
            binding.ObservationStreamId,
            new ObservationPosition(1),
            MachineState.Offline,
            1,
            new CurrentStatePolicyReference("continuity", "1"),
            new StateProjectionAuthorityRevision(1));

        Assert.Equal(MachineState.Offline, evaluation.MachineState);
    }

    [Theory]
    [InlineData(CurrentStateCoverage.Behind, CurrentStateFreshness.Current, CurrentStateUsability.Behind)]
    [InlineData(CurrentStateCoverage.Behind, CurrentStateFreshness.Stale, CurrentStateUsability.Behind)]
    [InlineData(CurrentStateCoverage.Behind, CurrentStateFreshness.Indeterminate, CurrentStateUsability.Behind)]
    [InlineData(CurrentStateCoverage.Indeterminate, CurrentStateFreshness.Current, CurrentStateUsability.Indeterminate)]
    [InlineData(CurrentStateCoverage.Indeterminate, CurrentStateFreshness.Stale, CurrentStateUsability.Indeterminate)]
    [InlineData(CurrentStateCoverage.Complete, CurrentStateFreshness.Indeterminate, CurrentStateUsability.Indeterminate)]
    [InlineData(CurrentStateCoverage.Complete, CurrentStateFreshness.Stale, CurrentStateUsability.Stale)]
    [InlineData(CurrentStateCoverage.Complete, CurrentStateFreshness.Current, CurrentStateUsability.Current)]
    public void EvidenceAcceptsOnlyFrozenUsabilityPrecedence(
        CurrentStateCoverage coverage,
        CurrentStateFreshness freshness,
        CurrentStateUsability usability)
    {
        var result = new CurrentMachineStateEvidence(
            MachineId.New(),
            MachineState.Unknown,
            coverage,
            freshness,
            usability,
            DateTimeOffset.UtcNow);

        Assert.Equal(usability, result.Usability);
    }

    [Fact]
    public void EvidenceRejectsUsabilityThatContradictsCoverageAndFreshness()
    {
        Assert.Throws<ArgumentException>(() =>
            new CurrentMachineStateEvidence(
                MachineId.New(),
                MachineState.Running,
                CurrentStateCoverage.Behind,
                CurrentStateFreshness.Current,
                CurrentStateUsability.Current,
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public void NoEvidenceCarriesCoverageWithoutManufacturingEvidenceFields()
    {
        var result = new CurrentMachineStateNoEvidence(
            MachineId.New(),
            CurrentStateCoverage.Behind);

        Assert.Equal(CurrentStateCoverage.Behind, result.Coverage);
        Assert.DoesNotContain(
            result.GetType().GetProperties(),
            property => property.Name is "MachineState" or "Freshness" or "Usability" or "ReadAsOf");
    }

    [Fact]
    public void OwnerResolutionKeepsNoOwnerExactlyOneAndAmbiguousDistinct()
    {
        var binding = CreateBinding();

        CurrentStateOwnerResolution noOwner = CurrentStateNoOwner.Instance;
        CurrentStateOwnerResolution oneOwner = new CurrentStateExactlyOneOwner(binding);
        CurrentStateOwnerResolution ambiguous = CurrentStateAmbiguousOwner.Instance;

        Assert.IsType<CurrentStateNoOwner>(noOwner);
        Assert.Same(binding, Assert.IsType<CurrentStateExactlyOneOwner>(oneOwner).Binding);
        Assert.IsType<CurrentStateAmbiguousOwner>(ambiguous);
    }

    [Fact]
    public void PolicyResolutionKeepsAllFourFrozenCasesDistinct()
    {
        var policy = new CurrentStateContinuityPolicy(
            new CurrentStatePolicyReference("continuity", "1"),
            StateContinuityMode.Preserve);

        CurrentStatePolicyResolution<CurrentStateContinuityPolicy> missing =
            CurrentStateMissingPolicy<CurrentStateContinuityPolicy>.Instance;
        CurrentStatePolicyResolution<CurrentStateContinuityPolicy> one =
            new CurrentStateExactlyOnePolicy<CurrentStateContinuityPolicy>(policy);
        CurrentStatePolicyResolution<CurrentStateContinuityPolicy> ambiguous =
            CurrentStateAmbiguousPolicy<CurrentStateContinuityPolicy>.Instance;
        CurrentStatePolicyResolution<CurrentStateContinuityPolicy> unsupported =
            CurrentStateUnsupportedPolicy<CurrentStateContinuityPolicy>.Instance;

        Assert.IsType<CurrentStateMissingPolicy<CurrentStateContinuityPolicy>>(missing);
        Assert.Same(
            policy,
            Assert.IsType<CurrentStateExactlyOnePolicy<CurrentStateContinuityPolicy>>(one).Policy);
        Assert.IsType<CurrentStateAmbiguousPolicy<CurrentStateContinuityPolicy>>(ambiguous);
        Assert.IsType<CurrentStateUnsupportedPolicy<CurrentStateContinuityPolicy>>(unsupported);
    }

    [Fact]
    public void AuthorityFailureVocabularyPreservesFrozenDistinctCases()
    {
        var names = Enum.GetNames<CurrentStateAuthorityFailureReason>();

        Assert.Equal(
            [
                "NoOwner",
                "AmbiguousOwner",
                "MissingPolicyAuthority",
                "AmbiguousPolicyAuthority",
                "UnsupportedPolicyAuthority",
                "StableCutUnavailable",
                "ContradictoryAttribution",
                "ContinuityPolicyMismatch",
                "UnauthorizedStateAuthority",
                "InconsistentAuthority",
            ],
            names);
    }

    private static CurrentStateAuthorityBinding CreateBinding()
    {
        var machineId = MachineId.New();
        return new CurrentStateAuthorityBinding(
            machineId,
            new ObservationStreamId(machineId, "primary"),
            new ObservationProcessorId("mapper"),
            new ObservationProcessorId("state"));
    }
}
