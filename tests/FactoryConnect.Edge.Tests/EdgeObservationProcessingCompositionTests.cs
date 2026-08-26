using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using FactoryConnect.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class EdgeObservationProcessingCompositionTests
{
    [Fact]
    public void InMemoryProviderComposesCompleteProcessingPipeline()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var configuration = Configuration("InMemory");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectEdgePersistence(configuration);
        services.AddFactoryConnectObservationProcessing(
            configuration,
            streamId);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(
            provider.GetRequiredService<
                DurableObservationProcessingPipeline>());
        Assert.Same(
            provider.GetRequiredService<
                InMemoryMappedMachineObservationSink>(),
            provider.GetRequiredService<
                IDurableMappedObservationReader>());
        Assert.Same(
            provider.GetRequiredService<
                InMemoryMachineStateActivityProjectionStore>(),
            provider.GetRequiredService<
                IMachineStateActivityProjectionStore>());
    }

    [Fact]
    public void ProviderWithoutProcessingCapabilitiesFailsClearly()
    {
        var machineId = MachineId.New();
        var streamId = new ObservationStreamId(machineId, "modbus:line-1");
        var configuration = Configuration(
            "SqlServer",
            "Server=test;Database=FactoryConnect;Integrated Security=True");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectEdgePersistence(configuration);
        services.AddFactoryConnectObservationProcessing(
            configuration,
            streamId);

        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<
                DurableObservationProcessingPipeline>());

        Assert.Contains(
            nameof(IDurableObservationReader),
            exception.Message,
            StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(
        string provider,
        string? connectionString = null)
    {
        Dictionary<string, string?> values = new()
        {
            ["Persistence:Provider"] = provider,
            ["ObservationProcessing:BatchSize"] = "10",
            ["ObservationProcessing:PollingInterval"] = "00:00:01",
            ["ObservationProcessing:Mappings:0:Source"] = "modbus",
            ["ObservationProcessing:Mappings:0:Address"] = "DI1",
            ["ObservationProcessing:Mappings:0:SignalKey"] = "running",
            ["ObservationProcessing:Mappings:0:Type"] = "Digital",
            ["ObservationProcessing:Mappings:0:Invert"] = "false",
        };

        if (connectionString is not null)
        {
            values["PersistenceProviders:SqlServer:ConnectionString"] =
                connectionString;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
