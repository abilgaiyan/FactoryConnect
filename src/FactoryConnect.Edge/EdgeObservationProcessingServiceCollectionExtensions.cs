using System.Globalization;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Core.Machines;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FactoryConnect.Edge;

public static class EdgeObservationProcessingServiceCollectionExtensions
{
    public const string SectionName = "ObservationProcessing";

    private static readonly ObservationProcessorId StateActivityProcessorId =
        new("machine-state-activity");

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

        services.RemoveAll<InMemoryMappingCoverageAuthorityStore>();
        services.RemoveAll<IMappingCoverageAuthorityStore>();
        services.RemoveAll<InMemoryMachineStateActivityAuthorityStore>();
        services.RemoveAll<IMachineStateActivityAuthorityStore>();
        services.RemoveAll<IMachineStateActivityCursorReader>();
        services.RemoveAll<CurrentStateContinuityPolicy>();
        services.RemoveAll<JointAuthorityMachineStateActivityProcessor>();
        services.RemoveAll<IMappedMachineObservationProcessor>();

        services.AddSingleton<ObservationAuthorityStoreGraph>();
        services.AddSingleton<IMappingCoverageAuthorityStore>(
            static provider =>
                provider.GetRequiredService<ObservationAuthorityStoreGraph>()
                    .MappingStore);
        services.AddSingleton<IMachineStateActivityAuthorityStore>(
            static provider =>
                provider.GetRequiredService<ObservationAuthorityStoreGraph>()
                    .StateActivityStore);
        services.AddSingleton<InMemoryMappingCoverageAuthorityStore>(
            static provider =>
                RequireConcreteCompatibility<
                    InMemoryMappingCoverageAuthorityStore>(
                    provider.GetRequiredService<ObservationAuthorityStoreGraph>()
                        .MappingStore));
        services.AddSingleton<InMemoryMachineStateActivityAuthorityStore>(
            static provider =>
                RequireConcreteCompatibility<
                    InMemoryMachineStateActivityAuthorityStore>(
                    provider.GetRequiredService<ObservationAuthorityStoreGraph>()
                        .StateActivityStore));
        services.AddSingleton<IMachineStateActivityCursorReader>(
            static provider => new JointMachineStateActivityCursorReader(
                provider.GetRequiredService<ObservationAuthorityStoreGraph>()
                    .StateActivityStore));
        services.AddSingleton(
            CanonicalCurrentStateContinuityPolicies.Preserve);
        services.AddSingleton<JointAuthorityMachineStateActivityProcessor>(
            static provider => new JointAuthorityMachineStateActivityProcessor(
                StateActivityProcessorId,
                provider.GetRequiredService<ObservationAuthorityStoreGraph>()
                    .StateActivityStore,
                provider.GetRequiredService<
                    CurrentStateContinuityPolicy>()));
        services.AddSingleton<IMappedMachineObservationProcessor>(
            static provider => provider.GetRequiredService<
                JointAuthorityMachineStateActivityProcessor>());

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
                var authorityGraph = provider.GetRequiredService<
                    ObservationAuthorityStoreGraph>();
                var mappedReader = provider.GetRequiredService<
                    IDurableMappedObservationReader>();
                var cursorReader = provider.GetRequiredService<
                    IMachineStateActivityCursorReader>();
                var stateActivityProcessor = provider.GetRequiredService<
                    IMappedMachineObservationProcessor>();
                List<DurableObservationProcessingPipeline> pipelines = [];

                foreach (var streamId in streams)
                {
                    var mappingProcessor = new MachineSignalMappingProcessor(
                        new ObservationProcessorId("canonical-mapping"),
                        mappings[streamId],
                        mappedSink,
                        authorityGraph.MappingStore);

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
                                cursorReader,
                                stateActivityProcessor,
                                streamId,
                                options),
                            pollingInterval));
                }

                return new DurableObservationProcessingPipelineSet(
                    pipelines,
                    pollingInterval);
            });

        if (streams.Length == 1)
        {
            services.AddSingleton(
                static provider =>
                    provider.GetRequiredService<
                        DurableObservationProcessingPipelineSet>()
                        .Pipelines[0]);
        }

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

    private static TConcrete RequireConcreteCompatibility<TConcrete>(
        object selected)
        where TConcrete : class =>
        selected as TConcrete ??
        throw new InvalidOperationException(
            "The selected observation authority store cannot be exposed " +
            $"through concrete compatibility service '{typeof(TConcrete).Name}'.");

    private sealed class ObservationAuthorityStoreGraph
    {
        public ObservationAuthorityStoreGraph(IServiceProvider provider)
        {
            var providerServices =
                provider.GetService<PersistenceProviderServices>();
            var mappingStore =
                providerServices?.MappingCoverageAuthorityStore;
            var stateActivityStore =
                providerServices?.MachineStateActivityAuthorityStore;

            if ((mappingStore is null) != (stateActivityStore is null))
            {
                throw new InvalidOperationException(
                    "The selected persistence provider must supply mapping " +
                    "coverage and machine state/activity authority stores " +
                    "together.");
            }

            if (mappingStore is not null && stateActivityStore is not null)
            {
                MappingStore = mappingStore;
                StateActivityStore = stateActivityStore;
                return;
            }

            MappingStore = new InMemoryMappingCoverageAuthorityStore();
            StateActivityStore =
                new InMemoryMachineStateActivityAuthorityStore();
        }

        public IMappingCoverageAuthorityStore MappingStore { get; }

        public IMachineStateActivityAuthorityStore StateActivityStore { get; }
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
                    Mappings = ReadMappings(section.GetSection("Mappings")),
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
            var streamKey = Required(streamSection, "StreamKey");
            var registeredStream = streamIds.FirstOrDefault(
                stream =>
                    stream.MachineId == machineId &&
                    string.Equals(
                        stream.StreamKey,
                        streamKey,
                        StringComparison.Ordinal));

            if (registeredStream is null)
            {
                throw new InvalidOperationException(
                    "ObservationProcessing:Streams contains a stream that is not " +
                    "registered for processing.");
            }

            if (!configured.TryAdd(
                    registeredStream,
                    new MachineSignalMappingConfiguration
                    {
                        MachineId = machineId,
                        Mappings = ReadMappings(
                            streamSection.GetSection("Mappings")),
                    }))
            {
                throw new InvalidOperationException(
                    $"Duplicate observation processing stream '{streamKey}'.");
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

        return configured;
    }

    private static MachineSignalMappingDefinition[] ReadMappings(
        IConfigurationSection section)
    {
        var mappings = section.GetChildren()
            .Select(ReadMapping)
            .ToArray();

        ValidateStateMappings(mappings, section.Path);
        return mappings;
    }

    private static void ValidateStateMappings(
        IReadOnlyList<MachineSignalMappingDefinition> mappings,
        string configurationPath)
    {
        foreach (var mapping in mappings)
        {
            if (IsStateDrivingSignal(mapping.SignalKey) &&
                mapping.Type != SignalType.Digital)
            {
                throw new InvalidOperationException(
                    $"{configurationPath} maps canonical state signal " +
                    $"'{mapping.SignalKey}' as '{mapping.Type}'. " +
                    "Machine state projection currently requires state-driving " +
                    "canonical signals to use Digital Boolean semantics. " +
                    "Normalize the source value before mapping or omit the " +
                    "state-driving mapping.");
            }
        }
    }

    private static bool IsStateDrivingSignal(string signalKey) =>
        string.Equals(
            signalKey,
            CanonicalSignalKeys.Running,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            signalKey,
            CanonicalSignalKeys.Idle,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            signalKey,
            CanonicalSignalKeys.Fault,
            StringComparison.OrdinalIgnoreCase);

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
