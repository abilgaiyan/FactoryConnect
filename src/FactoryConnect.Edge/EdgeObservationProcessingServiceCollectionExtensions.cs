using System.Globalization;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Machines;
using FactoryConnect.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Edge;

public static class EdgeObservationProcessingServiceCollectionExtensions
{
    public const string SectionName = "ObservationProcessing";

    public static IServiceCollection AddFactoryConnectObservationProcessing(
        this IServiceCollection services,
        IConfiguration configuration,
        ObservationStreamId streamId)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(streamId);

        var section = configuration.GetRequiredSection(SectionName);
        var batchSize = int.Parse(
            section["BatchSize"] ??
                throw new InvalidOperationException(
                    "ObservationProcessing:BatchSize is required."),
            CultureInfo.InvariantCulture);
        var pollingInterval = TimeSpan.Parse(
            section["PollingInterval"] ??
                throw new InvalidOperationException(
                    "ObservationProcessing:PollingInterval is required."),
            CultureInfo.InvariantCulture);
        var options = new ObservationProcessingRuntimeOptions(
            batchSize,
            pollingInterval);
        var mappingConfiguration =
            new MachineSignalMappingConfiguration
            {
                MachineId = streamId.MachineId,
                Mappings = ReadMappings(
                    section.GetRequiredSection("Mappings")),
            };

        services.AddSingleton(options);
        services.AddSingleton(mappingConfiguration);
        services.AddSingleton<InMemoryMappedMachineObservationSink>();
        services.AddSingleton<IMappedMachineObservationSink>(
            static provider =>
                provider.GetRequiredService<
                    InMemoryMappedMachineObservationSink>());
        services.AddSingleton<IDurableMappedObservationReader>(
            static provider =>
                provider.GetRequiredService<
                    InMemoryMappedMachineObservationSink>());
        services.AddSingleton<
            InMemoryMachineStateActivityProjectionStore>();
        services.AddSingleton<IMachineStateActivityProjectionStore>(
            static provider =>
                provider.GetRequiredService<
                    InMemoryMachineStateActivityProjectionStore>());

        services.AddSingleton<IDurableObservationReader>(
            static provider =>
                RequireCapability<IDurableObservationReader>(provider));
        services.AddSingleton<IObservationProcessingCheckpointStore>(
            static provider =>
                RequireCapability<
                    IObservationProcessingCheckpointStore>(provider));

        services.AddSingleton<IObservationProcessor>(
            static provider =>
                new MachineSignalMappingProcessor(
                    new ObservationProcessorId("canonical-mapping"),
                    provider.GetRequiredService<
                        MachineSignalMappingConfiguration>(),
                    provider.GetRequiredService<
                        IMappedMachineObservationSink>()));
        services.AddSingleton<IMappedMachineObservationProcessor>(
            static provider =>
                new MachineStateActivityProcessor(
                    new ObservationProcessorId("machine-state-activity"),
                    provider.GetRequiredService<
                        IMachineStateActivityProjectionStore>()));

        services.AddSingleton(
            provider =>
                new ObservationProcessingRuntime(
                    provider.GetRequiredService<
                        IDurableObservationReader>(),
                    provider.GetRequiredService<
                        IObservationProcessingCheckpointStore>(),
                    provider.GetRequiredService<IObservationProcessor>(),
                    streamId,
                    provider.GetRequiredService<
                        ObservationProcessingRuntimeOptions>()));
        services.AddSingleton(
            provider =>
                new MappedObservationProcessingRuntime(
                    provider.GetRequiredService<
                        IDurableMappedObservationReader>(),
                    provider.GetRequiredService<
                        IMachineStateActivityProjectionStore>(),
                    provider.GetRequiredService<
                        IMappedMachineObservationProcessor>(),
                    streamId,
                    provider.GetRequiredService<
                        ObservationProcessingRuntimeOptions>()));
        services.AddSingleton(
            provider =>
                new DurableObservationProcessingPipeline(
                    provider.GetRequiredService<
                        ObservationProcessingRuntime>(),
                    provider.GetRequiredService<
                        MappedObservationProcessingRuntime>(),
                    pollingInterval));
        services.AddHostedService<DurableObservationProcessingWorker>();

        return services;
    }

    private static TCapability RequireCapability<TCapability>(
        IServiceProvider provider)
        where TCapability : class
    {
        var store = provider.GetRequiredService<
            IObservationIngestionStore>();

        return store as TCapability ??
            throw new InvalidOperationException(
                $"The selected persistence provider does not support " +
                $"required processing capability '{typeof(TCapability).Name}'.");
    }

    private static MachineSignalMappingDefinition[] ReadMappings(
        IConfigurationSection section) =>
        section.GetChildren()
            .Select(ReadMapping)
            .ToArray();

    private static MachineSignalMappingDefinition ReadMapping(
        IConfigurationSection section)
    {
        var typeText = section["Type"] ??
            throw new InvalidOperationException(
                $"{section.Path}:Type is required.");

        if (!Enum.TryParse<SignalType>(
                typeText,
                ignoreCase: true,
                out var type) ||
            !Enum.IsDefined(type))
        {
            throw new InvalidOperationException(
                $"{section.Path}:Type '{typeText}' is unsupported.");
        }

        return new MachineSignalMappingDefinition
        {
            Source = Required(section, "Source"),
            Address = Required(section, "Address"),
            SignalKey = Required(section, "SignalKey"),
            Type = type,
            Invert = bool.Parse(section["Invert"] ?? "false"),
        };
    }

    private static string Required(
        IConfigurationSection section,
        string name) =>
        section[name] ??
        throw new InvalidOperationException(
            $"{section.Path}:{name} is required.");
}
