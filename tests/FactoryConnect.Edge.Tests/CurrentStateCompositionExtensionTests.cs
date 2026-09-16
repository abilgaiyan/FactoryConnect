using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Machines;
using FactoryConnect.Edge;
using FactoryConnect.Protocols.MTConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class CurrentStateCompositionExtensionTests
{
    [Fact]
    public void CompositionRegistersReaderAndProjectsExactAuthorities()
    {
        var machineId = MachineId.New();
        var inventory = Inventory(machineId);
        var continuityPolicy = CanonicalCurrentStateContinuityPolicies.Preserve;
        var cutProvider = new StubCutProvider();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
        var services = new ServiceCollection();
        services.AddSingleton(continuityPolicy);
        services.AddSingleton<ICurrentStateAuthorityCutProvider>(cutProvider);
        services.AddSingleton<TimeProvider>(timeProvider);

        services.AddFactoryConnectCurrentMachineState(Configuration("00:00:30"), inventory);

        using var provider = services.BuildServiceProvider();
        var concrete = provider.GetRequiredService<CurrentMachineStateReader>();
        var reader = provider.GetRequiredService<ICurrentMachineStateReader>();
        var owner = provider.GetRequiredService<ICurrentStateOwnerResolver>();
        var continuity = provider.GetRequiredService<
            ICurrentStatePolicyResolver<CurrentStateContinuityPolicy>>();
        var freshnessPolicy = provider.GetRequiredService<ICurrentStateFreshnessPolicy>();
        var freshness = provider.GetRequiredService<
            ICurrentStatePolicyResolver<ICurrentStateFreshnessPolicy>>();
        var time = provider.GetRequiredService<ICurrentStateTimeProvider>();

        Assert.Same(concrete, reader);
        Assert.Same(
            continuityPolicy,
            Assert.IsType<CurrentStateExactlyOnePolicy<CurrentStateContinuityPolicy>>(
                continuity.Resolve()).Policy);
        Assert.Same(
            freshnessPolicy,
            Assert.IsType<CurrentStateExactlyOnePolicy<ICurrentStateFreshnessPolicy>>(
                freshness.Resolve()).Policy);
        Assert.Equal("freshness/maximum-age", freshnessPolicy.Reference.Identity);
        Assert.Equal("1.0", freshnessPolicy.Reference.Version);
        Assert.Equal(TimeSpan.FromSeconds(30), freshnessPolicy.MaximumCurrentAge);
        Assert.Equal(DateTimeOffset.UnixEpoch, time.GetUtcNow());
        Assert.Equal(
            inventory.ActivityStreams[0],
            Assert.IsType<CurrentStateExactlyOneOwner>(owner.Resolve(machineId))
                .Binding.ObservationStreamId);
        Assert.Same(cutProvider, provider.GetRequiredService<ICurrentStateAuthorityCutProvider>());
    }

    [Fact]
    public void ZeroMaximumCurrentAgeIsAccepted()
    {
        var services = PrerequisiteServices();

        services.AddFactoryConnectCurrentMachineState(
            Configuration("00:00:00"),
            Inventory(MachineId.New()));

        using var provider = services.BuildServiceProvider();
        Assert.Equal(
            TimeSpan.Zero,
            provider.GetRequiredService<ICurrentStateFreshnessPolicy>().MaximumCurrentAge);
    }

    [Fact]
    public void MissingMaximumCurrentAgeFailsComposition()
    {
        var services = PrerequisiteServices();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CurrentState:Freshness:Marker"] = "present",
                })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddFactoryConnectCurrentMachineState(
                configuration,
                Inventory(MachineId.New())));

        Assert.Contains(
            "CurrentState:Freshness:MaximumCurrentAge is required.",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedMaximumCurrentAgeFailsComposition()
    {
        var services = PrerequisiteServices();

        Assert.Throws<FormatException>(
            () => services.AddFactoryConnectCurrentMachineState(
                Configuration("not-a-duration"),
                Inventory(MachineId.New())));
    }

    [Fact]
    public void NegativeMaximumCurrentAgeFailsComposition()
    {
        var services = PrerequisiteServices();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => services.AddFactoryConnectCurrentMachineState(
                Configuration("-00:00:00.0000001"),
                Inventory(MachineId.New())));
    }

    [Fact]
    public void TimeProviderMayBeRegisteredAfterCurrentStateComposition()
    {
        var services = new ServiceCollection();
        services.AddSingleton(CanonicalCurrentStateContinuityPolicies.Preserve);
        services.AddSingleton<ICurrentStateAuthorityCutProvider>(new StubCutProvider());
        var expected = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

        services.AddFactoryConnectCurrentMachineState(
            Configuration("00:00:30"),
            Inventory(MachineId.New()));
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(expected));

        using var provider = services.BuildServiceProvider();
        Assert.Equal(
            expected,
            provider.GetRequiredService<ICurrentStateTimeProvider>().GetUtcNow());
    }

    private static ServiceCollection PrerequisiteServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(CanonicalCurrentStateContinuityPolicies.Preserve);
        services.AddSingleton<ICurrentStateAuthorityCutProvider>(new StubCutProvider());
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        return services;
    }

    private static IConfiguration Configuration(string maximumCurrentAge) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CurrentState:Freshness:MaximumCurrentAge"] = maximumCurrentAge,
                })
            .Build();

    private static MtConnectMachineInventory Inventory(MachineId machineId) =>
        new(
            [
                new MtConnectAcquisitionOptions(
                    new MtConnectEndpoint(new Uri("http://localhost:5001/")),
                    machineId,
                    "cnc-07",
                    fromSequence: 1,
                    pollingInterval: TimeSpan.FromSeconds(1)),
            ]);

    private sealed class StubCutProvider : ICurrentStateAuthorityCutProvider
    {
        public Task<CurrentStateAuthorityCutReadResult> ReadAuthorityCutAsync(
            CurrentStateAuthorityBinding binding,
            CancellationToken cancellationToken) =>
            Task.FromResult<CurrentStateAuthorityCutReadResult>(
                StableCurrentStateAuthorityCutUnavailable.Instance);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
