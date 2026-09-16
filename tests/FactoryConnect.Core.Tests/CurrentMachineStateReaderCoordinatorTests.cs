using FactoryConnect.Abstractions;

namespace FactoryConnect.Core.Tests;

public sealed class CurrentMachineStateReaderCoordinatorTests
{
    [Fact]
    public void ConstructorRequiresAllFrozenReaderDependencies()
    {
        var ownerResolver = new StubOwnerResolver(CurrentStateNoOwner.Instance);
        var cutProvider = new StubCutProvider(StableCurrentStateAuthorityCutUnavailable.Instance);
        var continuityResolver = new ThrowingPolicyResolver<CurrentStateContinuityPolicy>();
        var freshnessResolver = new ThrowingPolicyResolver<ICurrentStateFreshnessPolicy>();
        var timeProvider = new ThrowingTimeProvider();

        Assert.Throws<ArgumentNullException>(() =>
            new CurrentMachineStateReader(null!, cutProvider, continuityResolver, freshnessResolver, timeProvider));
        Assert.Throws<ArgumentNullException>(() =>
            new CurrentMachineStateReader(ownerResolver, null!, continuityResolver, freshnessResolver, timeProvider));
        Assert.Throws<ArgumentNullException>(() =>
            new CurrentMachineStateReader(ownerResolver, cutProvider, null!, freshnessResolver, timeProvider));
        Assert.Throws<ArgumentNullException>(() =>
            new CurrentMachineStateReader(ownerResolver, cutProvider, continuityResolver, null!, timeProvider));
        Assert.Throws<ArgumentNullException>(() =>
            new CurrentMachineStateReader(ownerResolver, cutProvider, continuityResolver, freshnessResolver, null!));
    }

    [Fact]
    public async Task NoOwnerIsTerminalBeforeCutPolicyOrTimeDependencies()
    {
        var cutProvider = new ThrowingCutProvider();
        var reader = CreateReader(
            new StubOwnerResolver(CurrentStateNoOwner.Instance),
            cutProvider);

        var machineId = MachineId.New();
        var result = await reader.ReadAsync(machineId, CancellationToken.None);

        var failure = Assert.IsType<CurrentMachineStateAuthorityFailure>(result);
        Assert.Equal(machineId, failure.MachineId);
        Assert.Equal(CurrentStateAuthorityFailureReason.NoOwner, failure.Reason);
    }

    [Fact]
    public async Task AmbiguousOwnerIsTerminalBeforeCutPolicyOrTimeDependencies()
    {
        var reader = CreateReader(
            new StubOwnerResolver(CurrentStateAmbiguousOwner.Instance),
            new ThrowingCutProvider());

        var machineId = MachineId.New();
        var result = await reader.ReadAsync(machineId, CancellationToken.None);

        var failure = Assert.IsType<CurrentMachineStateAuthorityFailure>(result);
        Assert.Equal(CurrentStateAuthorityFailureReason.AmbiguousOwner, failure.Reason);
    }

    [Fact]
    public async Task ExactlyOneOwnerPassesSelectedBindingAndCallerCancellationToCutProvider()
    {
        var binding = CreateBinding();
        using var cancellation = new CancellationTokenSource();
        var cutProvider = new StubCutProvider(StableCurrentStateAuthorityCutUnavailable.Instance);
        var reader = CreateReader(
            new StubOwnerResolver(new CurrentStateExactlyOneOwner(binding)),
            cutProvider);

        var result = await reader.ReadAsync(binding.MachineId, cancellation.Token);

        var failure = Assert.IsType<CurrentMachineStateAuthorityFailure>(result);
        Assert.Equal(CurrentStateAuthorityFailureReason.StableCutUnavailable, failure.Reason);
        Assert.Same(binding, cutProvider.Binding);
        Assert.Equal(cancellation.Token, cutProvider.CancellationToken);
        Assert.Equal(1, cutProvider.CallCount);
    }

    [Fact]
    public async Task CutProviderCancellationPropagatesWithoutSemanticMapping()
    {
        var binding = CreateBinding();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reader = CreateReader(
            new StubOwnerResolver(new CurrentStateExactlyOneOwner(binding)),
            new CancellingCutProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadAsync(binding.MachineId, cancellation.Token));
    }

    [Fact]
    public async Task OwnerBindingForDifferentMachineIsInternalInvariantFailureBeforeCutRead()
    {
        var requestedMachine = MachineId.New();
        var otherBinding = CreateBinding();
        var reader = CreateReader(
            new StubOwnerResolver(new CurrentStateExactlyOneOwner(otherBinding)),
            new ThrowingCutProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reader.ReadAsync(requestedMachine, CancellationToken.None));
    }

    [Fact]
    public async Task StableAllNullCutCompletesAsIndeterminateNoEvidenceWithoutPoliciesOrTime()
    {
        var binding = CreateBinding();
        var cut = new CurrentStateAuthorityCut(binding, null, null, null);
        var reader = CreateReader(
            new StubOwnerResolver(new CurrentStateExactlyOneOwner(binding)),
            new StubCutProvider(new StableCurrentStateAuthorityCut(cut)));

        var result = await reader.ReadAsync(binding.MachineId, CancellationToken.None);

        var noEvidence = Assert.IsType<CurrentMachineStateNoEvidence>(result);
        Assert.Equal(CurrentStateCoverage.Indeterminate, noEvidence.Coverage);
    }

    [Fact]
    public async Task EmptyMachineIdIsRejectedBeforeOwnerResolution()
    {
        var reader = CreateReader(
            new ThrowingOwnerResolver(),
            new ThrowingCutProvider());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.ReadAsync(default, CancellationToken.None));
    }

    private static CurrentMachineStateReader CreateReader(
        ICurrentStateOwnerResolver ownerResolver,
        ICurrentStateAuthorityCutProvider cutProvider) =>
        new(
            ownerResolver,
            cutProvider,
            new ThrowingPolicyResolver<CurrentStateContinuityPolicy>(),
            new ThrowingPolicyResolver<ICurrentStateFreshnessPolicy>(),
            new ThrowingTimeProvider());

    private static CurrentStateAuthorityBinding CreateBinding()
    {
        var machineId = MachineId.New();
        return new CurrentStateAuthorityBinding(
            machineId,
            new ObservationStreamId(machineId, "primary"),
            new ObservationProcessorId("mapper"),
            new ObservationProcessorId("state"));
    }

    private sealed class StubOwnerResolver(CurrentStateOwnerResolution resolution)
        : ICurrentStateOwnerResolver
    {
        public CurrentStateOwnerResolution Resolve(MachineId machineId) => resolution;
    }

    private sealed class ThrowingOwnerResolver : ICurrentStateOwnerResolver
    {
        public CurrentStateOwnerResolution Resolve(MachineId machineId) =>
            throw new InvalidOperationException("Owner resolver must not be called.");
    }

    private sealed class StubCutProvider(CurrentStateAuthorityCutReadResult result)
        : ICurrentStateAuthorityCutProvider
    {
        public CurrentStateAuthorityBinding? Binding { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public int CallCount { get; private set; }

        public Task<CurrentStateAuthorityCutReadResult> ReadAuthorityCutAsync(
            CurrentStateAuthorityBinding binding,
            CancellationToken cancellationToken)
        {
            Binding = binding;
            CancellationToken = cancellationToken;
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingCutProvider : ICurrentStateAuthorityCutProvider
    {
        public Task<CurrentStateAuthorityCutReadResult> ReadAuthorityCutAsync(
            CurrentStateAuthorityBinding binding,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Cut provider must not be called.");
    }

    private sealed class CancellingCutProvider : ICurrentStateAuthorityCutProvider
    {
        public Task<CurrentStateAuthorityCutReadResult> ReadAuthorityCutAsync(
            CurrentStateAuthorityBinding binding,
            CancellationToken cancellationToken) =>
            Task.FromCanceled<CurrentStateAuthorityCutReadResult>(cancellationToken);
    }

    private sealed class ThrowingPolicyResolver<TPolicy> : ICurrentStatePolicyResolver<TPolicy>
        where TPolicy : class
    {
        public CurrentStatePolicyResolution<TPolicy> Resolve() =>
            throw new InvalidOperationException("Policy resolver must not be called before its authorized semantic slice.");
    }

    private sealed class ThrowingTimeProvider : ICurrentStateTimeProvider
    {
        public DateTimeOffset GetUtcNow() =>
            throw new InvalidOperationException("Time provider must not be called before FC-031.3C.5.");
    }
}
