using FactoryConnect.Abstractions;
using FactoryConnect.Core.Machines;
using FactoryConnect.Edge;
using FactoryConnect.Protocols.MTConnect;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class CurrentStateCompositionTypeTests
{
    [Fact]
    public void OwnerResolverDerivesCanonicalBindingFromInventory()
    {
        var machineId = MachineId.New();
        var inventory = Inventory(machineId, "cnc-07");
        var resolver = new EdgeCurrentStateOwnerResolver(inventory);

        var resolution = Assert.IsType<CurrentStateExactlyOneOwner>(
            resolver.Resolve(machineId));

        Assert.Equal(machineId, resolution.Binding.MachineId);
        Assert.Equal(inventory.ActivityStreams[0], resolution.Binding.ObservationStreamId);
        Assert.Equal(
            new ObservationProcessorId("canonical-mapping"),
            resolution.Binding.MappingProcessorId);
        Assert.Equal(
            new ObservationProcessorId("machine-state-activity"),
            resolution.Binding.StateProcessorId);
    }

    [Fact]
    public void OwnerResolverReturnsNoOwnerForMachineOutsideInventory()
    {
        var resolver = new EdgeCurrentStateOwnerResolver(
            Inventory(MachineId.New(), "cnc-07"));

        Assert.Same(
            CurrentStateNoOwner.Instance,
            resolver.Resolve(MachineId.New()));
    }

    [Fact]
    public void PolicyResolverProjectsTheExactComposedPolicyInstance()
    {
        var policy = CanonicalCurrentStateContinuityPolicies.Preserve;
        var resolver = new EdgeCurrentStatePolicyResolver<
            CurrentStateContinuityPolicy>(policy);

        var resolution = Assert.IsType<
            CurrentStateExactlyOnePolicy<CurrentStateContinuityPolicy>>(
            resolver.Resolve());

        Assert.Same(policy, resolution.Policy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void CanonicalFreshnessPolicyOwnsIdentityAndConfiguredMaximumAge(
        int seconds)
    {
        var maximumAge = TimeSpan.FromSeconds(seconds);

        var policy = CanonicalCurrentStateFreshnessPolicies.MaximumAge(maximumAge);

        Assert.Equal("freshness/maximum-age", policy.Reference.Identity);
        Assert.Equal("1.0", policy.Reference.Version);
        Assert.Equal(maximumAge, policy.MaximumCurrentAge);
    }

    [Fact]
    public void CanonicalFreshnessPolicyRejectsNegativeMaximumAge()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CanonicalCurrentStateFreshnessPolicies.MaximumAge(
                TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void FreshnessPolicyResolverProjectsTheExactComposedPolicyInstance()
    {
        var policy = CanonicalCurrentStateFreshnessPolicies.MaximumAge(
            TimeSpan.FromSeconds(30));
        var resolver = new EdgeCurrentStatePolicyResolver<
            ICurrentStateFreshnessPolicy>(policy);

        var resolution = Assert.IsType<
            CurrentStateExactlyOnePolicy<ICurrentStateFreshnessPolicy>>(
            resolver.Resolve());

        Assert.Same(policy, resolution.Policy);
    }

    [Fact]
    public void TimeAdapterDelegatesToTheExactComposedTimeProvider()
    {
        var expected = new DateTimeOffset(2026, 9, 16, 18, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(expected);
        var adapter = new TimeProviderCurrentStateTimeProvider(timeProvider);

        Assert.Equal(expected, adapter.GetUtcNow());
        Assert.Equal(1, timeProvider.GetUtcNowCalls);
    }

    private static MtConnectMachineInventory Inventory(
        MachineId machineId,
        string deviceKey) =>
        new(
            [
                new MtConnectAcquisitionOptions(
                    new MtConnectEndpoint(new Uri("http://localhost:5001/")),
                    machineId,
                    deviceKey,
                    fromSequence: 1,
                    pollingInterval: TimeSpan.FromSeconds(1)),
            ]);

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public int GetUtcNowCalls { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            GetUtcNowCalls++;
            return _utcNow;
        }
    }
}
