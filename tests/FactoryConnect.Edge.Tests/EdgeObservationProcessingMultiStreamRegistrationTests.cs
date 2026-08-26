using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class EdgeObservationProcessingMultiStreamRegistrationTests
{
    [Fact]
    public void MultiStreamCompositionDoesNotRegisterSingularPipeline()
    {
        var first = new ObservationStreamId(
            MachineId.New(),
            "modbus:line-1");
        var second = new ObservationStreamId(
            MachineId.New(),
            "modbus:line-2");
        var configuration = Configuration(first, second);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectEdgePersistence(configuration);
        services.AddFactoryConnectObservationProcessing(
            configuration,
            [first, second]);

        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            2,
            provider.GetRequiredService<
                DurableObservationProcessingPipelineSet>()
                .Pipelines.Count);
        Assert.Null(
            provider.GetService<DurableObservationProcessingPipeline>());
    }

    private static IConfiguration Configuration(
        ObservationStreamId first,
        ObservationStreamId second)
    {
        Dictionary<string, string?> values = new()
        {
            ["Persistence:Provider"] = "InMemory",
            ["ObservationProcessing:BatchSize"] = "10",
            ["ObservationProcessing:PollingInterval"] = "00:00:01",
            ["ObservationProcessing:Streams:0:MachineId"] =
                first.MachineId.ToString(),
            ["ObservationProcessing:Streams:0:StreamKey"] = first.StreamKey,
            ["ObservationProcessing:Streams:0:Mappings:0:Source"] = "modbus",
            ["ObservationProcessing:Streams:0:Mappings:0:Address"] = "DI1",
            ["ObservationProcessing:Streams:0:Mappings:0:SignalKey"] =
                CanonicalSignalKeys.Running,
            ["ObservationProcessing:Streams:0:Mappings:0:Type"] = "Digital",
            ["ObservationProcessing:Streams:1:MachineId"] =
                second.MachineId.ToString(),
            ["ObservationProcessing:Streams:1:StreamKey"] = second.StreamKey,
            ["ObservationProcessing:Streams:1:Mappings:0:Source"] = "modbus",
            ["ObservationProcessing:Streams:1:Mappings:0:Address"] = "X1",
            ["ObservationProcessing:Streams:1:Mappings:0:SignalKey"] =
                CanonicalSignalKeys.PowerOn,
            ["ObservationProcessing:Streams:1:Mappings:0:Type"] = "Digital",
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
