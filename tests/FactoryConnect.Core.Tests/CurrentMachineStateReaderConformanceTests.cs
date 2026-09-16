using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

public sealed class CurrentMachineStateReaderConformanceTests
{
    private static readonly CurrentStatePolicyReference ContinuityReference = new("continuity/preserve", "1.0");
    private static readonly CurrentStatePolicyReference FreshnessReference = new("freshness/default", "1.0");
    private static readonly DateTimeOffset ReadAsOf = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] FreshnessFailureEvents = ["owner", "cut", "continuity", "freshness"];
    private static readonly string[] EvidenceEvents = ["owner", "cut", "continuity", "freshness", "time"];

    [Fact]
    public async Task NoEvidenceSuppressesBothPoliciesAndTimeAcrossEstablishedEmptyCoverage()
    {
        var binding = CreateBinding();
        var mapping = CreateMapping(binding, rawThrough: 5, mappedHighWater: null);
        var dependencies = new RecordingDependencies(binding, new CurrentStateAuthorityCut(binding, null, mapping, null));

        var result = await dependencies.Reader.ReadAsync(binding.MachineId, CancellationToken.None);

        var noEvidence = Assert.IsType<CurrentMachineStateNoEvidence>(result);
        Assert.Equal(CurrentStateCoverage.Complete, noEvidence.Coverage);
        Assert.Equal(1, dependencies.OwnerCalls);
        Assert.Equal(1, dependencies.CutCalls);
        Assert.Equal(0, dependencies.ContinuityCalls);
        Assert.Equal(0, dependencies.FreshnessCalls);
        Assert.Equal(0, dependencies.TimeCalls);
    }

    [Fact]
    public async Task ContinuityFailureSuppressesFreshnessAndTime()
    {
        var binding = CreateBinding();
        var dependencies = new RecordingDependencies(binding, CreateEvaluationCut(binding, acquisition: null, mapping: null));
        dependencies.ContinuityResolution = new CurrentStateMissingPolicy<CurrentStateContinuityPolicy>();

        var result = await dependencies.Reader.ReadAsync(binding.MachineId, CancellationToken.None);

        AssertFailure(result, CurrentStateAuthorityFailureReason.MissingPolicyAuthority);
        Assert.Equal(1, dependencies.ContinuityCalls);
        Assert.Equal(0, dependencies.FreshnessCalls);
        Assert.Equal(0, dependencies.TimeCalls);
    }

    [Fact]
    public async Task ContinuityMismatchSuppressesFreshnessAndTime()
    {
        var binding = CreateBinding();
        var different = new CurrentStatePolicyReference("continuity/reset", "1.0");
        var cut = CreateEvaluationCut(binding, null, null, appliedReference: different);
        var dependencies = new RecordingDependencies(binding, cut);

        var result = await dependencies.Reader.ReadAsync(binding.MachineId, CancellationToken.None);

        AssertFailure(result, CurrentStateAuthorityFailureReason.ContinuityPolicyMismatch);
        Assert.Equal(1, dependencies.ContinuityCalls);
        Assert.Equal(0, dependencies.FreshnessCalls);
        Assert.Equal(0, dependencies.TimeCalls);
    }

    [Fact]
    public async Task UnauthorizedOfflineSuppressesFreshnessAndTimeAfterContinuityValidation()
    {
        var binding = CreateBinding();
        var cut = CreateEvaluationCut(binding, null, null, state: MachineState.Offline);
        var dependencies = new RecordingDependencies(binding, cut);

        var result = await dependencies.Reader.ReadAsync(binding.MachineId, CancellationToken.None);

        AssertFailure(result, CurrentStateAuthorityFailureReason.UnauthorizedStateAuthority);
        Assert.Equal(1, dependencies.ContinuityCalls);
        Assert.Equal(0, dependencies.FreshnessCalls);
        Assert.Equal(0, dependencies.TimeCalls);
    }

    [Fact]
    public async Task FreshnessFailureOccursAfterContinuityAndBeforeTime()
    {
        var binding = CreateBinding();
        var dependencies = new RecordingDependencies(binding, CreateEvaluationCut(binding, null, null));
        dependencies.FreshnessResolution = new CurrentStateAmbiguousPolicy<ICurrentStateFreshnessPolicy>();

        var result = await dependencies.Reader.ReadAsync(binding.MachineId, CancellationToken.None);

        AssertFailure(result, CurrentStateAuthorityFailureReason.AmbiguousPolicyAuthority);
        Assert.Equal(1, dependencies.ContinuityCalls);
        Assert.Equal(1, dependencies.FreshnessCalls);
        Assert.Equal(0, dependencies.TimeCalls);
        Assert.Equal(FreshnessFailureEvents, dependencies.Events);
    }

    [Fact]
    public async Task EvidencePathInvokesDependenciesInFrozenOrderAndCapturesTimeOnce()
    {
        var binding = CreateBinding();
        var acquisition = CreateAcquisition(binding, ReadAsOf - TimeSpan.FromMinutes(1));
        var mapping = CreateMapping(binding, rawThrough: 5, mappedHighWater: 5);
        var dependencies = new RecordingDependencies(binding, CreateEvaluationCut(binding, acquisition, mapping));

        var result = await dependencies.Reader.ReadAsync(binding.MachineId, CancellationToken.None);

        var evidence = Assert.IsType<CurrentMachineStateEvidence>(result);
        Assert.Equal(CurrentStateCoverage.Complete, evidence.Coverage);
        Assert.Equal(CurrentStateFreshness.Current, evidence.Freshness);
        Assert.Equal(CurrentStateUsability.Current, evidence.Usability);
        Assert.Equal(ReadAsOf, evidence.ReadAsOf);
        Assert.Equal(1, dependencies.OwnerCalls);
        Assert.Equal(1, dependencies.CutCalls);
        Assert.Equal(1, dependencies.ContinuityCalls);
        Assert.Equal(1, dependencies.FreshnessCalls);
        Assert.Equal(1, dependencies.TimeCalls);
        Assert.Equal(EvidenceEvents, dependencies.Events);
    }

    [Theory]
    [InlineData(false, CurrentStateCoverage.Indeterminate, CurrentStateFreshness.Current, CurrentStateUsability.Indeterminate)]
    [InlineData(true, CurrentStateCoverage.Behind, CurrentStateFreshness.Current, CurrentStateUsability.Behind)]
    public async Task PartialCutsPreserveCoverageAuthorityWhileStillProducingEvidence(
        bool includeTrailingMapping,
        CurrentStateCoverage expectedCoverage,
        CurrentStateFreshness expectedFreshness,
        CurrentStateUsability expectedUsability)
    {
        var binding = CreateBinding();
        var acquisition = CreateAcquisition(binding, ReadAsOf);
        var mapping = includeTrailingMapping ? CreateMapping(binding, rawThrough: 6, mappedHighWater: 6) : null;
        var cut = CreateEvaluationCut(binding, acquisition, mapping, evaluatedThrough: 5);
        var dependencies = new RecordingDependencies(binding, cut);

        var evidence = Assert.IsType<CurrentMachineStateEvidence>(
            await dependencies.Reader.ReadAsync(binding.MachineId, CancellationToken.None));

        Assert.Equal(expectedCoverage, evidence.Coverage);
        Assert.Equal(expectedFreshness, evidence.Freshness);
        Assert.Equal(expectedUsability, evidence.Usability);
    }

    [Fact]
    public async Task EvaluationBeyondMappedFrontierIsInternalInvariantFailureBeforePoliciesOrTime()
    {
        var binding = CreateBinding();
        var mapping = CreateMapping(binding, rawThrough: 6, mappedHighWater: 4);
        var dependencies = new RecordingDependencies(binding, CreateEvaluationCut(binding, null, mapping, evaluatedThrough: 5));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dependencies.Reader.ReadAsync(binding.MachineId, CancellationToken.None));

        Assert.Equal(0, dependencies.ContinuityCalls);
        Assert.Equal(0, dependencies.FreshnessCalls);
        Assert.Equal(0, dependencies.TimeCalls);
    }

    [Fact]
    public async Task StableCutBindingMismatchIsInternalInvariantFailureBeforeSemanticDependencies()
    {
        var selected = CreateBinding();
        var other = CreateBinding();
        var cut = new CurrentStateAuthorityCut(other, null, null, null);
        var dependencies = new RecordingDependencies(selected, cut);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dependencies.Reader.ReadAsync(selected.MachineId, CancellationToken.None));

        Assert.Equal(1, dependencies.OwnerCalls);
        Assert.Equal(1, dependencies.CutCalls);
        Assert.Equal(0, dependencies.ContinuityCalls);
        Assert.Equal(0, dependencies.FreshnessCalls);
        Assert.Equal(0, dependencies.TimeCalls);
    }

    private static CurrentStateAuthorityBinding CreateBinding()
    {
        var machineId = MachineId.New();
        return new CurrentStateAuthorityBinding(machineId, new ObservationStreamId(machineId, "primary"), new ObservationProcessorId("mapper"), new ObservationProcessorId("state"));
    }

    private static AcquisitionContactAuthority CreateAcquisition(CurrentStateAuthorityBinding binding, DateTimeOffset contactTime) =>
        new(binding.ObservationStreamId, contactTime, null, new AcquisitionAuthorityRevision(1));

    private static MappingCoverageAuthority CreateMapping(CurrentStateAuthorityBinding binding, ulong rawThrough, ulong? mappedHighWater) =>
        new(binding.MappingProcessorId, binding.ObservationStreamId, new ObservationPosition(rawThrough), mappedHighWater is null ? null : new ObservationPosition(mappedHighWater.Value), new MappingAuthorityRevision(1));

    private static CurrentStateAuthorityCut CreateEvaluationCut(
        CurrentStateAuthorityBinding binding,
        AcquisitionContactAuthority? acquisition,
        MappingCoverageAuthority? mapping,
        MachineState state = MachineState.Running,
        CurrentStatePolicyReference? appliedReference = null,
        ulong evaluatedThrough = 5)
    {
        var evaluation = new EvaluationAuthority(binding.StateProcessorId, binding.ObservationStreamId, new ObservationPosition(evaluatedThrough), state, 1, appliedReference ?? ContinuityReference, new StateProjectionAuthorityRevision(1));
        return new CurrentStateAuthorityCut(binding, acquisition, mapping, evaluation);
    }

    private static void AssertFailure(CurrentMachineStateReadResult result, CurrentStateAuthorityFailureReason reason)
    {
        var failure = Assert.IsType<CurrentMachineStateAuthorityFailure>(result);
        Assert.Equal(reason, failure.Reason);
    }

    private sealed class FreshnessPolicy : ICurrentStateFreshnessPolicy
    {
        public CurrentStatePolicyReference Reference => FreshnessReference;
        public TimeSpan MaximumCurrentAge => TimeSpan.FromMinutes(5);
    }

    private sealed class RecordingDependencies :
        ICurrentStateOwnerResolver,
        ICurrentStateAuthorityCutProvider,
        ICurrentStatePolicyResolver<CurrentStateContinuityPolicy>,
        ICurrentStatePolicyResolver<ICurrentStateFreshnessPolicy>,
        ICurrentStateTimeProvider
    {
        private readonly CurrentStateAuthorityBinding _binding;
        private readonly CurrentStateAuthorityCut _cut;

        public RecordingDependencies(CurrentStateAuthorityBinding binding, CurrentStateAuthorityCut cut)
        {
            _binding = binding;
            _cut = cut;
            Reader = new CurrentMachineStateReader(this, this, this, this, this);
            ContinuityResolution = new CurrentStateExactlyOnePolicy<CurrentStateContinuityPolicy>(new CurrentStateContinuityPolicy(ContinuityReference, StateContinuityMode.Preserve));
            FreshnessResolution = new CurrentStateExactlyOnePolicy<ICurrentStateFreshnessPolicy>(new FreshnessPolicy());
        }

        public CurrentMachineStateReader Reader { get; }
        public CurrentStatePolicyResolution<CurrentStateContinuityPolicy> ContinuityResolution { get; set; }
        public CurrentStatePolicyResolution<ICurrentStateFreshnessPolicy> FreshnessResolution { get; set; }
        public List<string> Events { get; } = [];
        public int OwnerCalls { get; private set; }
        public int CutCalls { get; private set; }
        public int ContinuityCalls { get; private set; }
        public int FreshnessCalls { get; private set; }
        public int TimeCalls { get; private set; }

        public CurrentStateOwnerResolution Resolve(MachineId machineId)
        {
            Events.Add("owner");
            OwnerCalls++;
            return new CurrentStateExactlyOneOwner(_binding);
        }

        public Task<CurrentStateAuthorityCutReadResult> ReadAuthorityCutAsync(CurrentStateAuthorityBinding binding, CancellationToken cancellationToken)
        {
            Events.Add("cut");
            CutCalls++;
            return Task.FromResult<CurrentStateAuthorityCutReadResult>(new StableCurrentStateAuthorityCut(_cut));
        }

        CurrentStatePolicyResolution<CurrentStateContinuityPolicy> ICurrentStatePolicyResolver<CurrentStateContinuityPolicy>.Resolve()
        {
            Events.Add("continuity");
            ContinuityCalls++;
            return ContinuityResolution;
        }

        CurrentStatePolicyResolution<ICurrentStateFreshnessPolicy> ICurrentStatePolicyResolver<ICurrentStateFreshnessPolicy>.Resolve()
        {
            Events.Add("freshness");
            FreshnessCalls++;
            return FreshnessResolution;
        }

        public DateTimeOffset GetUtcNow()
        {
            Events.Add("time");
            TimeCalls++;
            return ReadAsOf;
        }
    }
}
