using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Edge;

public static class EdgeApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddFactoryConnectEdgeApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var inventory = MtConnectMachineInventory.FromConfiguration(configuration);

        services.AddFactoryConnectEdgePersistence(configuration);
        services.AddSingleton<InMemoryOperationalMetricProjectionStore>();
        services.AddSingleton<IOperationalMetricProjectionStore>(
            static provider => provider.GetRequiredService<InMemoryOperationalMetricProjectionStore>());
        services.AddSingleton<IOperationalMetricProjectionQueryReader>(
            static provider => provider.GetRequiredService<InMemoryOperationalMetricProjectionStore>());
        services.AddSingleton<IMetricAggregationRevisionReader>(
            static provider => GetAggregationStoreCapability<IMetricAggregationRevisionReader>(provider));
        services.AddSingleton<IRevisionedOperationalMetricComponentSnapshotReader>(
            static provider => GetAggregationStoreCapability<IRevisionedOperationalMetricComponentSnapshotReader>(provider));

        services.AddFactoryConnectObservationProcessing(
            configuration,
            inventory.ActivityStreams);
        services.AddFactoryConnectProductionMetricInputs(
            configuration,
            inventory.ActivityStreams);
        services.AddFactoryConnectMetricAggregation(
            configuration,
            inventory.MachineIds);
        services.AddFactoryConnectOperationalMetrics(
            configuration,
            inventory.MachineIds);
        services.AddFactoryConnectMtConnectAcquisition(
            configuration,
            inventory);

        return services;
    }

    private static TCapability GetAggregationStoreCapability<TCapability>(IServiceProvider provider)
        where TCapability : class
    {
        var store = provider.GetRequiredService<IMetricAggregationStore>();
        return store as TCapability
            ?? throw new InvalidOperationException(
                $"Selected metric aggregation store does not provide required operational metric capability '{typeof(TCapability).Name}'.");
    }
}
