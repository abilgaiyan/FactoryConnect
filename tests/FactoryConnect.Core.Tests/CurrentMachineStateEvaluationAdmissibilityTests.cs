using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

public sealed class CurrentMachineStateEvaluationAdmissibilityTests
{
    private static readonly CurrentStatePolicyReference AuthorizedReference = new("continuity/preserve", "1.0");

    [Fact]
    public async Task MissingContinuityPolicyIsTerminalBeforeFreshnessOrTime()
    {
        var result = await ReadEvaluationAsync(new CurrentStateMissingPolicy<CurrentStateContinuityPolicy>(), MachineState.Running, AuthorizedReference);
        AssertFailure(result, CurrentStateAuthorityFailureReason.MissingPolicyAuthority);
    }

    [Fact]
    public async Task AmbiguousContinuityPolicyIsTerminalBeforeFreshnessOrTime()
    {
        var result = await ReadEvaluationAsync(new CurrentStateAmbiguousPolicy<CurrentStateContinuityPolicy>(), MachineState.Running, AuthorizedReference);
        AssertFailure(result, CurrentStateAuthorityFailureReason.AmbiguousPolicyAuthority);
    }

    [Fact]
    public async Task UnsupportedContinuityPolicyIsTerminalBeforeFreshnessOrTime()
    {
        var result = await ReadEvaluationAsync(new CurrentStateUnsupportedPolicy<CurrentStateContinuityPolicy>(), MachineState.Running, AuthorizedReference);
        AssertFailure(result, CurrentStateAuthorityFailureReason.UnsupportedPolicyAuthority);
    }

    [Fact]
    public async Task ContinuityPolicyMismatchPrecedesUnauthorizedOfflineState()
    {
        var differentReference = new CurrentStatePolicyReference("continuity/reset", "1.0");
        var result = await ReadEvaluationAsync(ExactlyOneAuthorizedPolicy(), MachineState.Offline, differentReference);
        AssertFailure(result, CurrentStateAuthorityFailureReason.ContinuityPolicyMismatch);
    }

    [Fact]
    public async Task MatchingContinuityPolicyRejectsOfflineAsUnauthorizedStateAuthority()
    {
        var result = await ReadEvaluationAsync(ExactlyOneAuthorizedPolicy(), MachineState.Offline, AuthorizedReference);
        AssertFailure(result, CurrentStateAuthorityFailureReason.UnauthorizedStateAuthority);
    }

    [Fact]
    public async Task AdmissibleEvaluationContinuesToFreshnessAndEvidence()
    {
        var binding = CreateBinding();
        var cut = CreateEvaluationCut(binding, MachineState.Running, AuthorizedReference);
        var readAsOf = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        var reader = new CurrentMachineStateReader(
            new StubOwnerResolver(binding),
            new StubCutProvider(cut),
            new StubPolicyResolver<CurrentStateContinuityPolicy>(ExactlyOneAuthorizedPolicy()),
            new StubPolicyResolver<ICurrentStateFreshnessPolicy>(
                new CurrentStateExactlyOnePolicy<ICurrentStateFreshnessPolicy>(new StubFreshnessPolicy())),
            new StubTimeProvider(readAsOf));

        var evidence = Assert.IsType<CurrentMachineStateEvidence>(
            await reader.ReadAsync(binding.MachineId, CancellationToken.None));

        Assert.Equal(MachineState.Running, evidence.MachineState);
        Assert.Equal(CurrentStateCoverage.Indeterminate, evidence.Coverage);
        Assert.Equal(CurrentStateFreshness.Indeterminate, evidence.Freshness);
        Assert.Equal(CurrentStateUsability.Indeterminate, evidence.Usability);
        Assert.Equal(readAsOf, evidence.ReadAsOf);
    }

    [Fact]
    public async Task NoEvidencePathDoesNotResolveContinuityPolicy()
    {
        var binding = CreateBinding();
        var cut = new CurrentStateAuthorityCut(binding, null, null, null);
        var reader = new CurrentMachineStateReader(
            new StubOwnerResolver(binding),
            new StubCutProvider(cut),
            new ThrowingPolicyResolver<CurrentStateContinuityPolicy>(),
            new ThrowingPolicyResolver<ICurrentStateFreshnessPolicy>(),
            new ThrowingTimeProvider());
        var result = await reader.ReadAsync(binding.MachineId, CancellationToken.None);
        var noEvidence = Assert.IsType<CurrentMachineStateNoEvidence>(result);
        Assert.Equal(CurrentStateCoverage.Indeterminate, noEvidence.Coverage);
    }

    private static async Task<CurrentMachineStateReadResult> ReadEvaluationAsync(CurrentStatePolicyResolution<CurrentStateContinuityPolicy> continuityResolution, MachineState machineState, CurrentStatePolicyReference appliedReference)
    {
        var binding = CreateBinding();
        var cut = CreateEvaluationCut(binding, machineState, appliedReference);
        var reader = CreateReader(binding, cut, continuityResolution);
        return await reader.ReadAsync(binding.MachineId, CancellationToken.None);
    }

    private static CurrentMachineStateReader CreateReader(CurrentStateAuthorityBinding binding, CurrentStateAuthorityCut cut, CurrentStatePolicyResolution<CurrentStateContinuityPolicy> continuityResolution) =>
        new(new StubOwnerResolver(binding), new StubCutProvider(cut), new StubPolicyResolver<CurrentStateContinuityPolicy>(continuityResolution), new ThrowingPolicyResolver<ICurrentStateFreshnessPolicy>(), new ThrowingTimeProvider());

    private static CurrentStateExactlyOnePolicy<CurrentStateContinuityPolicy> ExactlyOneAuthorizedPolicy() =>
        new(new CurrentStateContinuityPolicy(AuthorizedReference, StateContinuityMode.Preserve));

    private static CurrentStateAuthorityCut CreateEvaluationCut(CurrentStateAuthorityBinding binding, MachineState machineState, CurrentStatePolicyReference appliedReference)
    {
        var evaluation = new EvaluationAuthority(binding.StateProcessorId, binding.ObservationStreamId, new ObservationPosition(7), machineState, 1, appliedReference, new StateProjectionAuthorityRevision(1));
        return new CurrentStateAuthorityCut(binding, null, null, evaluation);
    }

    private static CurrentStateAuthorityBinding CreateBinding()
    {
        var machineId = MachineId.New();
        return new CurrentStateAuthorityBinding(machineId, new ObservationStreamId(machineId, "primary"), new ObservationProcessorId("mapper"), new ObservationProcessorId("state"));
    }

    private static void AssertFailure(CurrentMachineStateReadResult result, CurrentStateAuthorityFailureReason reason)
    {
        var failure = Assert.IsType<CurrentMachineStateAuthorityFailure>(result);
        Assert.Equal(reason, failure.Reason);
    }

    private sealed class StubFreshnessPolicy : ICurrentStateFreshnessPolicy
    {
        public CurrentStatePolicyReference Reference { get; } = new("freshness/default", "1.0");
        public TimeSpan MaximumCurrentAge => TimeSpan.FromMinutes(5);
    }

    private sealed class StubOwnerResolver(CurrentStateAuthorityBinding binding) : ICurrentStateOwnerResolver
    {
        public CurrentStateOwnerResolution Resolve(MachineId machineId) => new CurrentStateExactlyOneOwner(binding);
    }

    private sealed class StubCutProvider(CurrentStateAuthorityCut cut) : ICurrentStateAuthorityCutProvider
    {
        public Task<CurrentStateAuthorityCutReadResult> ReadAuthorityCutAsync(CurrentStateAuthorityBinding binding, CancellationToken cancellationToken) =>
            Task.FromResult<CurrentStateAuthorityCutReadResult>(new StableCurrentStateAuthorityCut(cut));
    }

    private sealed class StubPolicyResolver<TPolicy>(CurrentStatePolicyResolution<TPolicy> resolution) : ICurrentStatePolicyResolver<TPolicy> where TPolicy : class
    {
        public CurrentStatePolicyResolution<TPolicy> Resolve() => resolution;
    }

    private sealed class ThrowingPolicyResolver<TPolicy> : ICurrentStatePolicyResolver<TPolicy> where TPolicy : class
    {
        public CurrentStatePolicyResolution<TPolicy> Resolve() => throw new InvalidOperationException("This policy resolver must not be called before its semantic boundary.");
    }

    private sealed class StubTimeProvider(DateTimeOffset value) : ICurrentStateTimeProvider
    {
        public DateTimeOffset GetUtcNow() => value;
    }

    private sealed class ThrowingTimeProvider : ICurrentStateTimeProvider
    {
        public DateTimeOffset GetUtcNow() => throw new InvalidOperationException("Time provider must not be called before freshness semantics.");
    }
}
