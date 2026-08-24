using FactoryConnect.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddFactoryConnectPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(
                descriptor => descriptor.ServiceType ==
                    typeof(IObservationIngestionStore)))
        {
            throw new InvalidOperationException(
                "IObservationIngestionStore is already registered. " +
                "Persistence activation must own the single store registration.");
        }

        var provider = configuration["Persistence:Provider"];

        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new InvalidOperationException(
                "Persistence:Provider is required.");
        }

        var options = new PersistenceOptions(provider);

        services.AddSingleton(options);
        services.AddSingleton<IObservationIngestionStore>(ActivateProvider);

        return services;
    }

    public static IServiceCollection AddPersistenceProvider(
        this IServiceCollection services,
        IPersistenceProviderRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registration);

        services.AddSingleton<IPersistenceProviderRegistration>(
            registration);

        return services;
    }

    private static IObservationIngestionStore ActivateProvider(
        IServiceProvider services)
    {
        var options = services.GetRequiredService<PersistenceOptions>();
        var registrations = services
            .GetServices<IPersistenceProviderRegistration>()
            .ToArray();

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

        var selected = registrations.SingleOrDefault(
            registration => PersistenceProviderKey.Normalize(
                registration.ProviderKey) == options.Provider);

        if (selected is null)
        {
            throw new InvalidOperationException(
                $"Persistence provider '{options.Provider}' is not registered.");
        }

        return selected.Create(services);
    }
}
