using FactoryConnect.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    private static readonly Type[] ActivatedCapabilityTypes =
    [
        typeof(IObservationIngestionStore),
        typeof(IProductionContextProcessingStore),
        typeof(IMetricInputReader),
        typeof(IMetricAggregationStore),
    ];

    public static IServiceCollection AddFactoryConnectPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var existingCapability = ActivatedCapabilityTypes.FirstOrDefault(
            capabilityType => services.Any(
                descriptor => descriptor.ServiceType == capabilityType));
        if (existingCapability is not null)
        {
            throw new InvalidOperationException(
                $"{existingCapability.Name} is already registered. " +
                "Persistence activation must own the selected provider capabilities.");
        }

        var provider = configuration["Persistence:Provider"];

        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new InvalidOperationException(
                "Persistence:Provider is required.");
        }

        var options = new PersistenceOptions(provider);
        var registrations = GetRegistrations(services);

        if (registrations.Length == 0)
        {
            throw new InvalidOperationException(
                "At least one persistence provider must be registered " +
                "before persistence finalization.");
        }

        ValidateUniqueProviderKeys(registrations);

        var selected = registrations.SingleOrDefault(
            registration => PersistenceProviderKey.Normalize(
                registration.ProviderKey) == options.Provider);

        if (selected is null)
        {
            throw new InvalidOperationException(
                $"Persistence provider '{options.Provider}' is not registered.");
        }

        services.AddSingleton(options);
        services.AddSingleton<PersistenceProviderServices>(
            serviceProvider => selected.Create(serviceProvider));
        services.AddSingleton<IObservationIngestionStore>(
            static serviceProvider => serviceProvider
                .GetRequiredService<PersistenceProviderServices>()
                .ObservationIngestionStore);
        services.AddSingleton<IProductionContextProcessingStore>(
            static serviceProvider => serviceProvider
                .GetRequiredService<PersistenceProviderServices>()
                .ProductionContextProcessingStore);
        services.AddSingleton<IMetricInputReader>(
            static serviceProvider => serviceProvider
                .GetRequiredService<PersistenceProviderServices>()
                .MetricInputReader);
        services.AddSingleton<IMetricAggregationStore>(
            static serviceProvider => serviceProvider
                .GetRequiredService<PersistenceProviderServices>()
                .MetricAggregationStore);

        return services;
    }

    public static IServiceCollection AddPersistenceProvider(
        this IServiceCollection services,
        IPersistenceProviderRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registration);

        if (services.Any(
                descriptor => descriptor.ServiceType ==
                    typeof(PersistenceOptions)))
        {
            throw new InvalidOperationException(
                "Persistence has already been finalized. " +
                "Register providers before AddFactoryConnectPersistence.");
        }

        services.AddSingleton<IPersistenceProviderRegistration>(
            registration);

        return services;
    }

    private static IPersistenceProviderRegistration[] GetRegistrations(
        IServiceCollection services)
    {
        return services
            .Where(
                descriptor => descriptor.ServiceType ==
                    typeof(IPersistenceProviderRegistration))
            .Select(
                descriptor => descriptor.ImplementationInstance as
                    IPersistenceProviderRegistration
                    ?? throw new InvalidOperationException(
                        "Persistence providers must be registered through " +
                        "AddPersistenceProvider before persistence finalization."))
            .ToArray();
    }

    private static void ValidateUniqueProviderKeys(
        IPersistenceProviderRegistration[] registrations)
    {
        var duplicate = registrations
            .GroupBy(
                registration => PersistenceProviderKey.Normalize(
                    registration.ProviderKey),
                StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Persistence provider key '{duplicate.Key}' is registered " +
                "more than once.");
        }
    }
}
