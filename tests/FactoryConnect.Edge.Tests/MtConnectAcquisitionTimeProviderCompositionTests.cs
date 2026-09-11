using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class MtConnectAcquisitionTimeProviderCompositionTests
{
    [Fact]
    public void AcquisitionCompositionRegistersSystemTimeProviderByDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddFactoryConnectMtConnectAcquisition(
            Configuration(),
            Inventory());

        using var provider = services.BuildServiceProvider();

        Assert.Same(
            TimeProvider.System,
            provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void AcquisitionCompositionPreservesPreRegisteredTimeProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var expected = new FixedTimeProvider(
            new DateTimeOffset(
                2026,
                9,
                11,
                8,
                30,
                0,
                TimeSpan.Zero));
        services.AddSingleton<TimeProvider>(expected);

        services.AddFactoryConnectMtConnectAcquisition(
            Configuration(),
            Inventory());

        using var provider = services.BuildServiceProvider();

        Assert.Same(
            expected,
            provider.GetRequiredService<TimeProvider>());
    }

    private static MtConnectMachineInventory Inventory() =>
        new(
            [
                new MtConnectAcquisitionOptions(
                    new MtConnectEndpoint(
                        new Uri("http://localhost:5000")),
                    MachineId.New(),
                    "CNC-01",
                    1,
                    TimeSpan.FromSeconds(1)),
            ]);

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["MTConnect:Retry:MaxAttempts"] = "3",
                    ["MTConnect:Retry:InitialDelay"] = "00:00:01",
                    ["MTConnect:Retry:MaximumDelay"] = "00:00:30",
                    ["MTConnect:Retry:JitterRatio"] = "0.20",
                })
            .Build();

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
