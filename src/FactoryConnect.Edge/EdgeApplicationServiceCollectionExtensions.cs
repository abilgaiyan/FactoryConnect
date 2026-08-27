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
        services.AddFactoryConnectObservationProcessing(
            configuration,
            inventory.ActivityStreams);
        services.AddFactoryConnectProductionMetricInputs(
            configuration,
            inventory.ActivityStreams);
        services.AddFactoryConnectMetricAggregation(
            configuration,
            inventory.MachineIds);
        services.AddFactoryConnectMtConnectAcquisition(
            configuration,
            inventory);

        return services;
    }
}
