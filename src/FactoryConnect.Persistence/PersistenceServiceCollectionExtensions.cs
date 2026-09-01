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
        typeof(IMetricAggregationRevisionReader),
        typeof(IRevisionedOperationalMetricComponentSnapshotReader),
        typeof(IOperationalMetricProjectionStore),
        typeof(IOperationalMetricProjectionQueryReader),
        typeof(IOperationalMetricReportingQueryProvider),
        typeof(IMachineShiftOccurrenceRosterStore),
    ];

    private static readonly PersistenceProviderCapabilities[] IndividualCapabilities =
    [
        PersistenceProviderCapabilities.ObservationIngestion,
        PersistenceProviderCapabilities.ProductionContextProcessing,
        PersistenceProviderCapabilities.MetricInputReading,
        PersistenceProviderCapabilities.MetricAggregation,
        PersistenceProviderCapabilities.MetricAggregationRevisionReading,
        PersistenceProviderCapabilities.RevisionedOperationalMetricSnapshotReading,
        PersistenceProviderCapabilities.OperationalMetricProjectionStorage,
        PersistenceProviderCapabilities.OperationalMetricProjectionQuery,
        PersistenceProviderCapabilities.OperationalMetricReportingQuery,
        PersistenceProviderCapabilities.MachineShiftOccurrenceRoster,
    ];

    public static IServiceCollection AddFactoryConnectPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        PersistenceProviderCapabilities requiredCapabilities = PersistenceProviderCapabilities.Core)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        requiredCapabilities |= PersistenceProviderCapabilities.Core;

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

        var missingCapabilities = requiredCapabilities & ~selected.Capabilities;
        if (missingCapabilities != PersistenceProviderCapabilities.None)
        {
            var missingNames = IndividualCapabilities
                .Where(capability => (missingCapabilities & capability) != 0)
                .Select(static capability => capability.ToString());
            throw new InvalidOperationException(
                $"Persistence provider '{options.Provider}' does not support required capabilities: {string.Join(", ", missingNames)}.");
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

        if ((requiredCapabilities & PersistenceProviderCapabilities.OperationalMetrics) != 0)
        {
            services.AddSingleton<IMetricAggregationRevisionReader>(
                static serviceProvider => serviceProvider
                    .GetRequiredService<PersistenceProviderServices>()
                    .MetricAggregationRevisionReader
                    ?? throw MissingProviderService(nameof(IMetricAggregationRevisionReader)));
            services.AddSingleton<IRevisionedOperationalMetricComponentSnapshotReader>(
                static serviceProvider => serviceProvider
                    .GetRequiredService<PersistenceProviderServices>()
                    .RevisionedOperationalMetricComponentSnapshotReader
                    ?? throw MissingProviderService(nameof(IRevisionedOperationalMetricComponentSnapshotReader)));
            services.AddSingleton<IOperationalMetricProjectionStore>(
                static serviceProvider => serviceProvider
                    .GetRequiredService<PersistenceProviderServices>()
                    .OperationalMetricProjectionStore
                    ?? throw MissingProviderService(nameof(IOperationalMetricProjectionStore)));
            services.AddSingleton<IOperationalMetricProjectionQueryReader>(
                static serviceProvider => serviceProvider
                    .GetRequiredService<PersistenceProviderServices>()
                    .OperationalMetricProjectionQueryReader
                    ?? throw MissingProviderService(nameof(IOperationalMetricProjectionQueryReader)));
        }

        if ((requiredCapabilities & PersistenceProviderCapabilities.OperationalMetricReportingQuery) != 0)
        {
            services.AddSingleton<IOperationalMetricReportingQueryProvider>(
                static serviceProvider => serviceProvider
                    .GetRequiredService<PersistenceProviderServices>()
                    .OperationalMetricReportingQueryProvider
                    ?? throw MissingProviderService(nameof(IOperationalMetricReportingQueryProvider)));
        }

        if ((requiredCapabilities & PersistenceProviderCapabilities.MachineShiftOccurrenceRoster) != 0)
        {
            services.AddSingleton<IMachineShiftOccurrenceRosterStore>(
                static serviceProvider => serviceProvider
                    .GetRequiredService<PersistenceProviderServices>()
                    .MachineShiftOccurrenceRosterStore
                    ?? throw MissingProviderService(nameof(IMachineShiftOccurrenceRosterStore)));
        }

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

        services.AddSingleton<IPersistenceProviderRegistration>(registration);
        return services;
    }

    private static InvalidOperationException MissingProviderService(string capabilityName) =>
        new($"Selected persistence provider declared capability '{capabilityName}' but returned no implementation.");

    private static IPersistenceProviderRegistration[] GetRegistrations(IServiceCollection services) =>
        services
            .Where(descriptor => descriptor.ServiceType == typeof(IPersistenceProviderRegistration))
            .Select(descriptor => descriptor.ImplementationInstance as IPersistenceProviderRegistration
                ?? throw new InvalidOperationException(
                    "Persistence providers must be registered through AddPersistenceProvider before persistence finalization."))
            .ToArray();

    private static void ValidateUniqueProviderKeys(IPersistenceProviderRegistration[] registrations)
    {
        var duplicate = registrations
            .GroupBy(
                registration => PersistenceProviderKey.Normalize(registration.ProviderKey),
                StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Persistence provider key '{duplicate.Key}' is registered more than once.");
        }
    }
}
