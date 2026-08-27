using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class EdgeApplicationMultiMachineCompositionTests
{
    [Fact]
    public void RootCompositionCreatesAllPipelineLayersFromOneMachineInventory()
    {
        var machineA = new MachineId(Guid.NewGuid());
        var machineB = new MachineId(Guid.NewGuid());
        var configuration = CreateConfiguration(machineA, machineB);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectEdgeApplication(configuration);

        using var provider = services.BuildServiceProvider();
        var inventory = provider.GetRequiredService<MtConnectMachineInventory>();
        var acquisitionFactories = provider
            .GetServices<IMtConnectAcquisitionRuntimeFactory>()
            .ToArray();
        var observationPipelines = provider.GetRequiredService<
            DurableObservationProcessingPipelineSet>();
        var producerRuntimes = provider.GetRequiredService<
            ProductionMetricInputRuntimeSet>();
        var aggregationRuntimes = provider.GetRequiredService<
            MetricAggregationProcessingRuntimeSet>();

        Assert.Equal(2, inventory.Machines.Count);
        Assert.Equal(2, inventory.ActivityStreams.Count);
        Assert.Equal(2, acquisitionFactories.Length);
        Assert.Equal(2, observationPipelines.Pipelines.Count);
        Assert.Equal(2, producerRuntimes.ActivityRuntimes.Count);
        Assert.Equal(2, producerRuntimes.QuantityRuntimes.Count);
        Assert.Equal(2, aggregationRuntimes.Runtimes.Count);

        Assert.Equal(
            new[] { machineA, machineB }.OrderBy(static item => item.Value),
            inventory.MachineIds.OrderBy(static item => item.Value));
        Assert.Equal(
            inventory.MachineIds.OrderBy(static item => item.Value),
            aggregationRuntimes.Runtimes
                .Select(static runtime => ParseMachineId(runtime.ProcessorId))
                .OrderBy(static item => item.Value));
    }

    private static MachineId ParseMachineId(
        MetricAggregationProcessorId processorId)
    {
        const string prefix = "metric-aggregation:";
        Assert.StartsWith(prefix, processorId.Value, StringComparison.Ordinal);
        return new MachineId(Guid.Parse(processorId.Value[prefix.Length..]));
    }

    private static IConfiguration CreateConfiguration(
        MachineId machineA,
        MachineId machineB)
    {
        var values = new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "InMemory",
            ["MTConnect:Retry:MaxAttempts"] = "3",
            ["MTConnect:Retry:InitialDelay"] = "00:00:01",
            ["MTConnect:Retry:MaximumDelay"] = "00:00:30",
            ["MTConnect:Retry:JitterRatio"] = "0.20",
            ["ObservationProcessing:BatchSize"] = "100",
            ["ObservationProcessing:PollingInterval"] = "00:00:01",
            ["ProductionProcessing:BatchSize"] = "100",
            ["ProductionProcessing:PollingInterval"] = "00:00:01",
            ["MetricAggregation:BatchSize"] = "100",
            ["MetricAggregation:PollingInterval"] = "00:00:01",
        };

        AddMachine(values, 0, machineA, "CNC-A", "LINE-A", "CTX-A");
        AddMachine(values, 1, machineB, "CNC-B", "LINE-B", "CTX-B");

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static void AddMachine(
        Dictionary<string, string?> values,
        int index,
        MachineId machineId,
        string deviceKey,
        string lineId,
        string contextId)
    {
        var machineText = machineId.Value.ToString("D");
        var mtPrefix = $"MTConnect:Machines:{index}";
        values[$"{mtPrefix}:BaseUri"] = $"http://localhost:{5000 + index}";
        values[$"{mtPrefix}:MachineId"] = machineText;
        values[$"{mtPrefix}:DeviceKey"] = deviceKey;
        values[$"{mtPrefix}:FromSequence"] = "1";
        values[$"{mtPrefix}:PollingInterval"] = "00:00:01";

        var observationPrefix = $"ObservationProcessing:Streams:{index}";
        values[$"{observationPrefix}:MachineId"] = machineText;
        values[$"{observationPrefix}:StreamKey"] = deviceKey;

        var productionPrefix = $"ProductionProcessing:Machines:{index}";
        values[$"{productionPrefix}:MachineId"] = machineText;
        values[$"{productionPrefix}:ActivityStreamKey"] = deviceKey;
        values[$"{productionPrefix}:QuantityStreamKey"] = "production-quantity";
        values[$"{productionPrefix}:CompanyId"] = "COMP-1";
        values[$"{productionPrefix}:SiteId"] = "SITE-1";
        values[$"{productionPrefix}:ProductionLineId"] = lineId;
        values[$"{productionPrefix}:ContextAssignmentId"] = contextId;
        values[$"{productionPrefix}:ContextEffectiveFromUtc"] =
            "2026-01-01T00:00:00+00:00";
        values[$"{productionPrefix}:Shift:AssignmentId"] = $"SHIFT-{index}";
        values[$"{productionPrefix}:Shift:ShiftId"] = "SHIFT-1";
        values[$"{productionPrefix}:Shift:Name"] = "Shift 1";
        values[$"{productionPrefix}:Shift:TimeZoneId"] = "UTC";
        values[$"{productionPrefix}:Shift:StartsAtLocal"] = "06:00:00";
        values[$"{productionPrefix}:Shift:EndsAtLocal"] = "14:00:00";
        values[$"{productionPrefix}:Shift:EffectiveFrom"] = "2026-01-01";
        values[$"{productionPrefix}:PlannedProduction:AssignmentId"] =
            $"POT-{index}";
        values[$"{productionPrefix}:PlannedProduction:TimeZoneId"] = "UTC";
        values[$"{productionPrefix}:PlannedProduction:StartsAtLocal"] = "06:00:00";
        values[$"{productionPrefix}:PlannedProduction:EndsAtLocal"] = "14:00:00";
        values[$"{productionPrefix}:PlannedProduction:EffectiveFrom"] = "2026-01-01";
    }
}
