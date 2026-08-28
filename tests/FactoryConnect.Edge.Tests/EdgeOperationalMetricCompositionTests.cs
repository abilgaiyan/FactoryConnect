using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class EdgeOperationalMetricCompositionTests
{
    [Fact]
    public void MultipleMachinesCreateIndependentProjectionRuntimesAndReportingReader()
    {
        var machineOne = MachineId.New();
        var machineTwo = MachineId.New();
        var aggregationStore = new InMemoryMetricAggregationStore();
        var projectionStore = new InMemoryOperationalMetricProjectionStore();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMetricAggregationRevisionReader>(aggregationStore);
        services.AddSingleton<IRevisionedOperationalMetricComponentSnapshotReader>(aggregationStore);
        services.AddSingleton<IOperationalMetricProjectionStore>(projectionStore);
        services.AddSingleton<IOperationalMetricProjectionQueryReader>(projectionStore);
        var configuration = Configuration();

        services.AddFactoryConnectOperationalMetrics(configuration, [machineOne, machineTwo]);

        using var provider = services.BuildServiceProvider();
        var runtimes = provider.GetRequiredService<OperationalMetricProjectionProcessingRuntimeSet>();

        Assert.Equal(2, runtimes.Runtimes.Count);
        Assert.Equal(2, runtimes.Runtimes.Select(static runtime => runtime.ProcessorId).Distinct().Count());
        Assert.All(
            runtimes.Runtimes,
            runtime => Assert.Contains("builtins-v1", runtime.ProcessorId.Value, StringComparison.Ordinal));
        Assert.NotNull(provider.GetRequiredService<IOperationalMetricDefinitionCatalog>());
        Assert.NotNull(provider.GetRequiredService<IOperationalMetricReportReader>());
    }

    [Fact]
    public void DuplicateMachinesAreRejected()
    {
        var machineId = MachineId.New();
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddFactoryConnectOperationalMetrics(Configuration(), [machineId, machineId]));
    }

    [Fact]
    public void RuntimeSetRejectsDuplicateProjectionProcessorIdentities()
    {
        var machineId = MachineId.New();
        var aggregationStore = new InMemoryMetricAggregationStore();
        var projectionStore = new InMemoryOperationalMetricProjectionStore();
        var catalog = new OperationalMetricDefinitionCatalog(BuiltInOperationalMetricDefinitions.All);
        var processorId = new OperationalMetricProjectionProcessorId("projection-one");
        var sourceProcessorId = new MetricAggregationProcessorId("aggregation-one");
        var streamId = MetricInputStreamId.ForMachine(machineId);

        OperationalMetricProjectionProcessingRuntime Runtime() => new(
            processorId,
            sourceProcessorId,
            streamId,
            new CoherentOperationalMetricEvaluationBatchSource(
                catalog,
                aggregationStore,
                aggregationStore,
                sourceProcessorId,
                streamId,
                OperationalMetricEvaluationContextKey.Unpartitioned),
            new OperationalMetricProjectionFactory(catalog, processorId),
            projectionStore);

        Assert.Throws<ArgumentException>(() =>
            new OperationalMetricProjectionProcessingRuntimeSet(
                [Runtime(), Runtime()],
                TimeSpan.FromSeconds(1)));
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OperationalMetrics:PollingInterval"] = "00:00:01",
                })
            .Build();
}
