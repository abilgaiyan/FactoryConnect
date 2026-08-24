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
        var registrations = GetRegistrations(services);

        if (registrations.Length == 0)
        {
            throw new InvalidOperationException(
                "At least one persistence provider must be registered " +
                "before persistence finalization.");
        }

        ValidateUniqueProviderKeys(registrations);

        var selected = registrations.SingleOrDefault(
            registration => registration.ProviderKey == options.Provider);

        if (selected is null)
        {
            throw new InvalidOperationException(
                $"Persistence provider '{options.Provider}' is not registered.");
        }

        services.AddSingleton(options);
        services.AddSingleton<IObservationIngestionStore>(
            serviceProvider => selected.Create(serviceProvider));

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
        IReadOnlyCollection<IPersistenceProviderRegistration> registrations)
    {
        var duplicate = registrations
            .GroupBy(
                registration => registration.ProviderKey,
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
