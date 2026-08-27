using System.Globalization;
using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Edge;

public static class EdgeMtConnectAcquisitionServiceCollectionExtensions
{
    public static IServiceCollection AddFactoryConnectMtConnectAcquisition(
        this IServiceCollection services,
        IConfiguration configuration,
        MtConnectMachineInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(inventory);

        var section = configuration.GetRequiredSection("MTConnect");
        var retrySection = section.GetRequiredSection("Retry");
        var retryOptions = new MtConnectRetryOptions(
            int.Parse(
                Required(retrySection, "MaxAttempts"),
                CultureInfo.InvariantCulture),
            TimeSpan.Parse(
                Required(retrySection, "InitialDelay"),
                CultureInfo.InvariantCulture),
            TimeSpan.Parse(
                Required(retrySection, "MaximumDelay"),
                CultureInfo.InvariantCulture),
            double.Parse(
                Required(retrySection, "JitterRatio"),
                CultureInfo.InvariantCulture));

        services.AddSingleton(inventory);
        services.AddSingleton(retryOptions);
        services.AddSingleton<HttpClient>();
        services.AddSingleton<MtConnectSampleClient>();
        services.AddSingleton<MtConnectCurrentClient>();
        services.AddSingleton<IMtConnectAcquisitionSessionFactory,
            MtConnectAcquisitionSessionFactory>();
        services.AddSingleton<IMtConnectRetryDelay, SystemMtConnectRetryDelay>();
        services.AddSingleton<IMtConnectJitterSource, SystemMtConnectJitterSource>();
        services.AddSingleton<MtConnectTransientRetryPolicy>();
        services.AddSingleton<IMtConnectContinuityReporter,
            LoggingMtConnectContinuityReporter>();
        services.AddSingleton<MtConnectContinuityRecoveryPolicy>();
        services.AddSingleton<MtConnectStartupCheckpointResolver>();

        foreach (var options in inventory.Machines)
        {
            services.AddSingleton<IMtConnectAcquisitionRuntimeFactory>(provider =>
            {
                var streamId = MtConnectObservationStreamId.Create(
                    options.MachineId,
                    options.DeviceKey);
                var sink = new MtConnectDurableObservationSink(
                    provider.GetRequiredService<IObservationIngestionStore>(),
                    streamId);

                return new MtConnectAcquisitionRuntimeFactory(
                    options,
                    provider.GetRequiredService<MtConnectStartupCheckpointResolver>(),
                    provider.GetRequiredService<IMtConnectAcquisitionSessionFactory>(),
                    provider.GetRequiredService<MtConnectTransientRetryPolicy>(),
                    provider.GetRequiredService<MtConnectContinuityRecoveryPolicy>(),
                    sink);
            });
        }

        services.AddHostedService<FactoryConnectWorker>();
        return services;
    }

    private static string Required(
        IConfigurationSection section,
        string key) =>
        section[key] ?? throw new InvalidOperationException(
            $"{section.Path}:{key} is required.");
}
