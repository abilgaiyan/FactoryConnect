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
        ObservationStreamId streamId) =>
        services.AddFactoryConnectObservationProcessing(
            configuration,
            [streamId]);

    public static IServiceCollection AddFactoryConnectObservationProcessing(
        this IServiceCollection services,
        IConfiguration configuration,
        IReadOnlyList<ObservationStreamId> streamIds)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(streamIds);

        if (streamIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one observation stream is required.",
                nameof(streamIds));
        }

        if (streamIds.Distinct().Count() != streamIds.Count)
        {
            throw new ArgumentException(
                "Observation processing streams must be unique.",
                nameof(streamIds));
        }

        var streams = streamIds.ToArray();
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
        var mappings = ReadMappingConfigurations(section, streams);

        services.AddSingleton(options);
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

        services.AddSingleton(
            provider =>
            {
                var rawReader = provider.GetRequiredService<
                    IDurableObservationReader>();
                var checkpoints = provider.GetRequiredService<
                    IObservationProcessingCheckpointStore>();
                var mappedSink = provider.GetRequiredService<
                    IMappedMachineObservationSink>();
                var mappedReader = provider.GetRequiredService<
                    IDurableMappedObservationReader>();
                var projectionStore = provider.GetRequiredService<
                    IMachineStateActivityProjectionStore>();
                List<DurableObservationProcessingPipeline> pipelines = [];

                foreach (var streamId in streams)
                {
                    var mappingProcessor = new MachineSignalMappingProcessor(
                        new ObservationProcessorId("canonical-mapping"),
                        mappings[streamId],
                        mappedSink);
                    var stateActivityProcessor =
                        new MachineStateActivityProcessor(
                            new ObservationProcessorId(
                                "machine-state-activity"),
                            projectionStore);

                    pipelines.Add(
                        new DurableObservationProcessingPipeline(
                            new ObservationProcessingRuntime(
                                rawReader,
                                checkpoints,
                                mappingProcessor,
                                streamId,
                                options),
                            new MappedObservationProcessingRuntime(
                                mappedReader,
                                projectionStore,
                                stateActivityProcessor,
                                streamId,
                                options),
                            pollingInterval));
                }

                return new DurableObservationProcessingPipelineSet(
                    pipelines,
                    pollingInterval);
            });
        services.AddSingleton(
            static provider =>
            {
                var pipelines = provider.GetRequiredService<
                    DurableObservationProcessingPipelineSet>();

                if (pipelines.Pipelines.Count != 1)
                {
                    throw new InvalidOperationException(
                        "A singular durable observation processing pipeline " +
                        "can only be resolved when exactly one stream is configured.");
                }

                return pipelines.Pipelines[0];
            });
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

    private static Dictionary<
        ObservationStreamId,
        MachineSignalMappingConfiguration> ReadMappingConfigurations(
            IConfigurationSection section,
            ObservationStreamId[] streamIds)
    {
        var streamSections = section.GetSection("Streams").GetChildren().ToArray();

        if (streamSections.Length == 0)
        {
            if (streamIds.Length != 1)
            {
                throw new InvalidOperationException(
                    "ObservationProcessing:Streams is required when multiple " +
                    "observation streams are configured.");
            }

            var streamId = streamIds[0];
            return new Dictionary<
                ObservationStreamId,
                MachineSignalMappingConfiguration>
            {
                [streamId] = new MachineSignalMappingConfiguration
                {
                    MachineId = streamId.MachineId,
                    Mappings = ReadMappings(
                        section.GetRequiredSection("Mappings")),
                },
            };
        }

        Dictionary<
            ObservationStreamId,
            MachineSignalMappingConfiguration> configured = [];

        foreach (var streamSection in streamSections)
        {
            var machineId = new MachineId(
                Guid.Parse(Required(streamSection, "MachineId")));
            var streamId = new ObservationStreamId(
                machineId,
                Required(streamSection, "StreamKey"));

            if (!configured.TryAdd(
                    streamId,
                    new MachineSignalMappingConfiguration
                    {
                        MachineId = machineId,
                        Mappings = ReadMappings(
                            streamSection.GetRequiredSection("Mappings")),
                    }))
            {
                throw new InvalidOperationException(
                    $"Duplicate observation processing stream '{streamId.StreamKey}'.");
            }
        }

        foreach (var streamId in streamIds)
        {
            if (!configured.ContainsKey(streamId))
            {
                throw new InvalidOperationException(
                    $"No observation processing mapping configuration exists " +
                    $"for machine '{streamId.MachineId}' and stream " +
                    $"'{streamId.StreamKey}'.");
            }
        }

        if (configured.Keys.Any(streamId => !streamIds.Contains(streamId)))
        {
            throw new InvalidOperationException(
                "ObservationProcessing:Streams contains a stream that is not " +
                "registered for processing.");
        }

        return configured;
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
