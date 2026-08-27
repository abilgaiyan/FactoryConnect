using System.Globalization;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Edge;

public static class EdgeMetricAggregationServiceCollectionExtensions
{
    public const string SectionName = "MetricAggregation";

    public static IServiceCollection AddFactoryConnectMetricAggregation(
        this IServiceCollection services,
        IConfiguration configuration,
        IReadOnlyList<MachineId> machineIds)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(machineIds);

        if (machineIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one machine is required for metric aggregation.",
                nameof(machineIds));
        }

        if (machineIds.Distinct().Count() != machineIds.Count)
        {
            throw new ArgumentException(
                "Metric aggregation machines must be unique.",
                nameof(machineIds));
        }

        var machines = machineIds.ToArray();
        var section = configuration.GetRequiredSection(SectionName);
        var batchSize = int.Parse(
            section["BatchSize"] ??
                throw new InvalidOperationException(
                    "MetricAggregation:BatchSize is required."),
            CultureInfo.InvariantCulture);
        var pollingInterval = TimeSpan.Parse(
            section["PollingInterval"] ??
                throw new InvalidOperationException(
                    "MetricAggregation:PollingInterval is required."),
            CultureInfo.InvariantCulture);

        if (batchSize <= 0)
        {
            throw new InvalidOperationException(
                "MetricAggregation:BatchSize must be greater than zero.");
        }

        if (pollingInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "MetricAggregation:PollingInterval must be greater than zero.");
        }

        services.AddSingleton(
            provider =>
            {
                var reader = provider.GetRequiredService<IMetricInputReader>();
                var store = provider.GetRequiredService<IMetricAggregationStore>();
                var runtimes = machines
                    .Select(machineId =>
                        new MetricAggregationProcessingRuntime(
                            new MetricAggregationProcessorId(
                                $"metric-aggregation:{machineId.Value:D}"),
                            reader,
                            store,
                            MetricInputStreamId.ForMachine(machineId),
                            batchSize))
                    .ToArray();

                return new MetricAggregationProcessingRuntimeSet(
                    runtimes,
                    pollingInterval);
            });

        services.AddHostedService<MetricAggregationProcessingWorker>();
        return services;
    }
}
