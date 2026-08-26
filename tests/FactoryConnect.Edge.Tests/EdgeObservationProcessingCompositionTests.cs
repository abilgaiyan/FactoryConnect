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
        Assert.Single(
            provider.GetRequiredService<
                DurableObservationProcessingPipelineSet>().Pipelines);
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
    public async Task MultipleStreamsBindMachineSpecificMappingsAndStayIsolated()
    {
        var firstMachine = MachineId.New();
        var secondMachine = MachineId.New();
        var firstStream = new ObservationStreamId(firstMachine, "modbus:line-1");
        var secondStream = new ObservationStreamId(secondMachine, "modbus:line-2");
        var configuration = MultiStreamConfiguration(
            firstStream,
            secondStream);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectEdgePersistence(configuration);
        services.AddFactoryConnectObservationProcessing(
            configuration,
            [firstStream, secondStream]);

        using var provider = services.BuildServiceProvider();
        var rawStore = provider.GetRequiredService<IObservationIngestionStore>();
        var pipelines = provider.GetRequiredService<
            DurableObservationProcessingPipelineSet>();
        var mappedStore = provider.GetRequiredService<
            InMemoryMappedMachineObservationSink>();

        await rawStore.CommitAsync(
            Batch(firstStream, "DI1", true));
        await rawStore.CommitAsync(
            Batch(secondStream, "X1", true));

        Assert.True(await pipelines.RunCycleAsync());

        Assert.Equal(2, pipelines.Pipelines.Count);
        Assert.Equal(
            CanonicalSignalKeys.Running,
            Assert.Single(mappedStore.ReadObservations(firstStream))
                .Observation.SignalKey);
        Assert.Equal(
            CanonicalSignalKeys.PowerOn,
            Assert.Single(mappedStore.ReadObservations(secondStream))
                .Observation.SignalKey);
    }

    [Fact]
    public void MultipleStreamsRequirePerStreamMappingConfiguration()
    {
        var configuration = Configuration("InMemory");
        var services = new ServiceCollection();
        var streams = new[]
        {
            new ObservationStreamId(MachineId.New(), "modbus:line-1"),
            new ObservationStreamId(MachineId.New(), "modbus:line-2"),
        };

        Assert.Throws<InvalidOperationException>(
            () => services.AddFactoryConnectObservationProcessing(
                configuration,
                streams));
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

    private static ObservationIngestionBatch Batch(
        ObservationStreamId streamId,
        string address,
        bool value) =>
        new(
            null,
            new ObservationCheckpoint(streamId, 1, 2),
            [
                new SequencedMachineObservation(
                    1,
                    new MachineObservation
                    {
                        MachineId = streamId.MachineId,
                        Source = "modbus",
                        Address = address,
                        Type = SignalType.Digital,
                        Value = value,
                        Timestamp = DateTimeOffset.UnixEpoch,
                    }),
            ]);

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

    private static IConfiguration MultiStreamConfiguration(
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
            ["ObservationProcessing:Streams:0:Mappings:0:SignalKey"] = "running",
            ["ObservationProcessing:Streams:0:Mappings:0:Type"] = "Digital",
            ["ObservationProcessing:Streams:0:Mappings:0:Invert"] = "false",
            ["ObservationProcessing:Streams:1:MachineId"] =
                second.MachineId.ToString(),
            ["ObservationProcessing:Streams:1:StreamKey"] = second.StreamKey,
            ["ObservationProcessing:Streams:1:Mappings:0:Source"] = "modbus",
            ["ObservationProcessing:Streams:1:Mappings:0:Address"] = "X1",
            ["ObservationProcessing:Streams:1:Mappings:0:SignalKey"] = "power.on",
            ["ObservationProcessing:Streams:1:Mappings:0:Type"] = "Digital",
            ["ObservationProcessing:Streams:1:Mappings:0:Invert"] = "false",
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
