using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        var currentStateReader = provider.GetRequiredService<ICurrentMachineStateReader>();
        var concreteCurrentStateReader = provider.GetRequiredService<CurrentMachineStateReader>();
        var currentStateTime = provider.GetRequiredService<ICurrentStateTimeProvider>();
        var timeProvider = provider.GetRequiredService<TimeProvider>();
        var acquisitionFactories = provider
            .GetServices<IMtConnectAcquisitionRuntimeFactory>()
            .ToArray();
        var observationPipelines = provider.GetRequiredService<
            DurableObservationProcessingPipelineSet>();
        var producerRuntimes = provider.GetRequiredService<
            ProductionMetricInputRuntimeSet>();
        var aggregationRuntimes = provider.GetRequiredService<
            MetricAggregationProcessingRuntimeSet>();
        var metricRuntimes = provider.GetRequiredService<
            OperationalMetricProjectionProcessingRuntimeSet>();
        var rosterRuntimes = provider.GetRequiredService<
            MachineShiftOccurrenceRosterMaterializationRuntimeSet>();
        var rosterRequest = provider.GetRequiredService<
            MachineShiftRosterMaterializationRequest>();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();

        Assert.Equal(2, inventory.Machines.Count);
        Assert.Equal(2, inventory.ActivityStreams.Count);
        Assert.Same(concreteCurrentStateReader, currentStateReader);
        Assert.NotNull(currentStateTime);
        Assert.NotNull(timeProvider);
        Assert.Equal(2, acquisitionFactories.Length);
        Assert.Equal(2, observationPipelines.Pipelines.Count);
        Assert.Equal(2, producerRuntimes.ActivityRuntimes.Count);
        Assert.Equal(2, producerRuntimes.QuantityRuntimes.Count);
        Assert.Equal(2, aggregationRuntimes.Runtimes.Count);
        Assert.Equal(2, metricRuntimes.Runtimes.Count);
        Assert.Equal(2, rosterRuntimes.Scopes.Count);
        Assert.Equal(new DateOnly(2026, 8, 27), rosterRequest.FromProductionDayInclusive);
        Assert.Equal(new DateOnly(2026, 8, 29), rosterRequest.ToProductionDayExclusive);
        Assert.Contains(hostedServices, static service =>
            service is MachineShiftRosterMaterializationWorker);
        Assert.Contains(hostedServices, static service =>
            service is ProductionMetricInputProcessingWorker);
        Assert.True(
            Array.FindIndex(hostedServices, static service =>
                service is MachineShiftRosterMaterializationWorker) <
            Array.FindIndex(hostedServices, static service =>
                service is ProductionMetricInputProcessingWorker));
        Assert.NotNull(provider.GetRequiredService<IOperationalMetricReportReader>());

        Assert.Equal(
            new[] { machineA, machineB }.OrderBy(static item => item.Value),
            inventory.MachineIds.OrderBy(static item => item.Value));
        Assert.Equal(
            inventory.MachineIds.OrderBy(static item => item.Value),
            aggregationRuntimes.Runtimes
                .Select(static runtime => ParseMachineId(runtime.ProcessorId))
                .OrderBy(static item => item.Value));
        Assert.Equal(
            new[] { machineA, machineB }.OrderBy(static item => item.Value),
            rosterRuntimes.Scopes
                .Select(static scope => scope.MachineId)
                .OrderBy(static item => item.Value));
        Assert.Equal(
            ["LINE-A", "LINE-B"],
            rosterRuntimes.Scopes
                .OrderBy(static scope => scope.ProductionLineId.Value, StringComparer.Ordinal)
                .Select(static scope => scope.ProductionLineId.Value));
    }

    private static MachineId ParseMachineId(
        MetricAggregationProcessorId processorId)
    {
        const string prefix = "metric-aggregation:";
        Assert.StartsWith(
            prefix,
            processorId.Value,
            StringComparison.Ordinal);
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
            ["CurrentState:Freshness:MaximumCurrentAge"] = "00:00:30",
            ["ProductionProcessing:BatchSize"] = "100",
            ["ProductionProcessing:PollingInterval"] = "00:00:01",
            ["ProductionProcessing:RosterMaterialization:FromProductionDayInclusive"] = "2026-08-27",
            ["ProductionProcessing:RosterMaterialization:ToProductionDayExclusive"] = "2026-08-29",
            ["MetricAggregation:BatchSize"] = "100",
            ["MetricAggregation:PollingInterval"] = "00:00:01",
            ["OperationalMetrics:PollingInterval"] = "00:00:01",
        };

        AddMachine(values, 0, machineA, "CNC-A", "LINE-A", "CTX-A");
        AddMachine(values, 1, machineB, "CNC-B", "LINE-B", "CTX-B");
        AddSharedShift(values, 0, "LINE-A");
        AddSharedShift(values, 1, "LINE-B");

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
        var activityStreamKey = $"mtconnect:{deviceKey}";
        var mtPrefix = $"MTConnect:Machines:{index}";
        values[$"{mtPrefix}:BaseUri"] = index == 0
            ? "http://localhost:5000"
            : "http://localhost:5001";
        values[$"{mtPrefix}:MachineId"] = machineText;
        values[$"{mtPrefix}:DeviceKey"] = deviceKey;
        values[$"{mtPrefix}:FromSequence"] = "1";
        values[$"{mtPrefix}:PollingInterval"] = "00:00:01";

        var observationPrefix = $"ObservationProcessing:Streams:{index}";
        values[$"{observationPrefix}:MachineId"] = machineText;
        values[$"{observationPrefix}:StreamKey"] = activityStreamKey;

        var productionPrefix = $"ProductionProcessing:Machines:{index}";
        values[$"{productionPrefix}:MachineId"] = machineText;
        values[$"{productionPrefix}:ActivityStreamKey"] = activityStreamKey;
        values[$"{productionPrefix}:QuantityStreamKey"] = "production-quantity";
        values[$"{productionPrefix}:CompanyId"] = "COMP-1";
        values[$"{productionPrefix}:SiteId"] = "SITE-1";
        values[$"{productionPrefix}:ProductionLineId"] = lineId;
        values[$"{productionPrefix}:Contexts:0:AssignmentId"] = contextId;
        values[$"{productionPrefix}:Contexts:0:EffectiveFrom"] =
            "2026-01-01T00:00:00+00:00";
        values[$"{productionPrefix}:ShiftSchedules:0:AssignmentId"] = $"SHIFT-{index}";
        values[$"{productionPrefix}:ShiftSchedules:0:ShiftId"] = "SHIFT-1";
        values[$"{productionPrefix}:ShiftSchedules:0:Name"] = "Shift 1";
        values[$"{productionPrefix}:ShiftSchedules:0:TimeZoneId"] = "UTC";
        values[$"{productionPrefix}:ShiftSchedules:0:StartsAtLocal"] = "06:00:00";
        values[$"{productionPrefix}:ShiftSchedules:0:EndsAtLocal"] = "14:00:00";
        values[$"{productionPrefix}:ShiftSchedules:0:EffectiveFrom"] = "2026-01-01";
        values[$"{productionPrefix}:PlannedProduction:AssignmentId"] =
            $"POT-{index}";
        values[$"{productionPrefix}:PlannedProduction:TimeZoneId"] = "UTC";
        values[$"{productionPrefix}:PlannedProduction:StartsAtLocal"] = "06:00:00";
        values[$"{productionPrefix}:PlannedProduction:EndsAtLocal"] = "14:00:00";
        values[$"{productionPrefix}:PlannedProduction:EffectiveFrom"] = "2026-01-01";
    }
    private static void AddSharedShift(
        Dictionary<string, string?> values,
        int index,
        string lineId)
    {
        var prefix = $"ProductionProcessing:ShiftSchedules:{index}";
        values[$"{prefix}:AssignmentId"] = $"SHIFT-{index}";
        values[$"{prefix}:CompanyId"] = "COMP-1";
        values[$"{prefix}:SiteId"] = "SITE-1";
        values[$"{prefix}:ProductionLineId"] = lineId;
        values[$"{prefix}:ShiftId"] = "SHIFT-1";
        values[$"{prefix}:Name"] = "Shift 1";
        values[$"{prefix}:TimeZoneId"] = "UTC";
        values[$"{prefix}:StartsAtLocal"] = "06:00:00";
        values[$"{prefix}:EndsAtLocal"] = "14:00:00";
        values[$"{prefix}:EffectiveFrom"] = "2026-01-01";
        var days = Enum.GetNames<DayOfWeek>();
        for (var day = 0; day < days.Length; day++)
        {
            values[$"{prefix}:ActiveDays:{day}"] = days[day];
        }
    }

}
