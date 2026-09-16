using System.Globalization;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Machines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Edge;

public static class EdgeCurrentMachineStateServiceCollectionExtensions
{
    public const string SectionName = "CurrentState";

    public static IServiceCollection AddFactoryConnectCurrentMachineState(
        this IServiceCollection services,
        IConfiguration configuration,
        MtConnectMachineInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(inventory);

        var freshnessSection = configuration
            .GetRequiredSection(SectionName)
            .GetRequiredSection("Freshness");
        var maximumCurrentAge = TimeSpan.Parse(
            freshnessSection["MaximumCurrentAge"] ??
                throw new InvalidOperationException(
                    "CurrentState:Freshness:MaximumCurrentAge is required."),
            CultureInfo.InvariantCulture);
        var freshnessPolicy =
            CanonicalCurrentStateFreshnessPolicies.MaximumAge(maximumCurrentAge);

        services.AddSingleton<ICurrentStateOwnerResolver>(
            new EdgeCurrentStateOwnerResolver(inventory));
        services.AddSingleton<ICurrentStatePolicyResolver<CurrentStateContinuityPolicy>>(
            static provider => new EdgeCurrentStatePolicyResolver<CurrentStateContinuityPolicy>(
                provider.GetRequiredService<CurrentStateContinuityPolicy>()));
        services.AddSingleton<ICurrentStateFreshnessPolicy>(freshnessPolicy);
        services.AddSingleton<ICurrentStatePolicyResolver<ICurrentStateFreshnessPolicy>>(
            static provider => new EdgeCurrentStatePolicyResolver<ICurrentStateFreshnessPolicy>(
                provider.GetRequiredService<ICurrentStateFreshnessPolicy>()));
        services.AddSingleton<ICurrentStateTimeProvider>(
            static provider => new TimeProviderCurrentStateTimeProvider(
                provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<CurrentMachineStateReader>();
        services.AddSingleton<ICurrentMachineStateReader>(
            static provider => provider.GetRequiredService<CurrentMachineStateReader>());

        return services;
    }
}
