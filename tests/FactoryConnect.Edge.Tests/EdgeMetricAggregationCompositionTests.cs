using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class EdgeMetricAggregationCompositionTests
{
    [Fact]
    public async Task MultipleMachinesCreateIndependentAggregationRuntimes()
    {
        var machineOne = new MachineId(Guid.NewGuid());
        var machineTwo = new MachineId(Guid.NewGuid());
        var reader = new RecordingMetricInputReader();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMetricInputReader>(reader);
        services.AddSingleton<IMetricAggregationStore, InMemoryMetricAggregationStore>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["MetricAggregation:BatchSize"] = "25",
                    ["MetricAggregation:PollingInterval"] = "00:00:01",
                })
            .Build();

        services.AddFactoryConnectMetricAggregation(
            configuration,
            [machineOne, machineTwo]);

        await using var provider = services.BuildServiceProvider();
        var runtimes = provider.GetRequiredService<MetricAggregationProcessingRuntimeSet>();

        Assert.Equal(2, runtimes.Runtimes.Count);
        Assert.Equal(2, runtimes.Runtimes.Select(static runtime => runtime.ProcessorId).Distinct().Count());

        Assert.False(await runtimes.RunCycleAsync(CancellationToken.None));
        Assert.Equal(
            [MetricInputStreamId.ForMachine(machineOne), MetricInputStreamId.ForMachine(machineTwo)],
            reader.StreamIds.OrderBy(static stream => stream.StreamIdSortKey()).ToArray()
                .OrderBy(static stream => stream.StreamIdSortKey()).ToArray());
        Assert.All(reader.MaxCounts, static count => Assert.Equal(25, count));
    }

    [Fact]
    public void SelectedInMemoryProviderResolvesAggregationCompositionWithoutManualStores()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Persistence:Provider"] = "InMemory",
                    ["MetricAggregation:BatchSize"] = "25",
                    ["MetricAggregation:PollingInterval"] = "00:00:01",
                })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddFactoryConnectEdgePersistence(configuration);
        services.AddFactoryConnectMetricAggregation(configuration, [machineId]);

        using var provider = services.BuildServiceProvider();
        var runtimes = provider.GetRequiredService<MetricAggregationProcessingRuntimeSet>();

        var runtime = Assert.Single(runtimes.Runtimes);
        Assert.Equal(
            new MetricAggregationProcessorId($"metric-aggregation:{machineId.Value:D}"),
            runtime.ProcessorId);
        Assert.Same(
            provider.GetRequiredService<IProductionContextProcessingStore>(),
            provider.GetRequiredService<IMetricInputReader>());
    }

    [Fact]
    public void DuplicateMachinesAreRejected()
    {
        var machineId = new MachineId(Guid.NewGuid());
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["MetricAggregation:BatchSize"] = "25",
                    ["MetricAggregation:PollingInterval"] = "00:00:01",
                })
            .Build();

        Assert.Throws<ArgumentException>(() =>
            services.AddFactoryConnectMetricAggregation(
                configuration,
                [machineId, machineId]));
    }

    private sealed class RecordingMetricInputReader : IMetricInputReader
    {
        public List<MetricInputStreamId> StreamIds { get; } = [];

        public List<int> MaxCounts { get; } = [];

        public ValueTask<MetricInputReadBatch> ReadAsync(
            MetricInputReadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StreamIds.Add(request.StreamId);
            MaxCounts.Add(request.MaxCount);
            return ValueTask.FromResult(
                new MetricInputReadBatch(
                    request.StreamId,
                    request.AfterPosition,
                    request.AfterPosition,
                    []));
        }
    }
}

internal static class MetricInputStreamIdTestExtensions
{
    public static string StreamIdSortKey(this MetricInputStreamId streamId) =>
        $"{streamId.MachineId.Value:D}:{streamId.Value}";
}
