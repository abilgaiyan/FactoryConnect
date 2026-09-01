using System.Globalization;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Edge;

public static class EdgeProductionMetricInputServiceCollectionExtensions
{
    public const string SectionName = "ProductionProcessing";

    public static IServiceCollection AddFactoryConnectProductionMetricInputs(
        this IServiceCollection services,
        IConfiguration configuration,
        ObservationStreamId activityStreamId) =>
        services.AddFactoryConnectProductionMetricInputs(
            configuration,
            [activityStreamId]);

    public static IServiceCollection AddFactoryConnectProductionMetricInputs(
        this IServiceCollection services,
        IConfiguration configuration,
        IReadOnlyList<ObservationStreamId> activityStreamIds)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(activityStreamIds);

        if (activityStreamIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one production activity stream is required.",
                nameof(activityStreamIds));
        }

        if (activityStreamIds.Distinct().Count() != activityStreamIds.Count)
        {
            throw new ArgumentException(
                "Production activity streams must be unique.",
                nameof(activityStreamIds));
        }

        if (activityStreamIds
            .Select(static stream => stream.MachineId)
            .Distinct()
            .Count() != activityStreamIds.Count)
        {
            throw new ArgumentException(
                "Production processing requires exactly one activity stream per machine.",
                nameof(activityStreamIds));
        }

        var section = configuration.GetRequiredSection(SectionName);
        var batchSize = int.Parse(
            Required(section, "BatchSize"),
            CultureInfo.InvariantCulture);
        var pollingInterval = TimeSpan.Parse(
            Required(section, "PollingInterval"),
            CultureInfo.InvariantCulture);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            pollingInterval,
            TimeSpan.Zero);
        var rosterMaterializationEnabled = services.Any(descriptor =>
            descriptor.ServiceType == typeof(IMachineShiftOccurrenceRosterStore));

        var configurations = ReadMachineConfigurations(
            section,
            activityStreamIds);
        var scopes = configurations.Select(static item => item.Scope).ToArray();
        var schedulingScopes = scopes
            .Select(static scope => new MachineShiftScheduleScope(
                scope.MachineId,
                scope.SiteId,
                scope.ProductionLineId))
            .ToArray();
        var contexts = configurations.Select(static item => item.Context).ToArray();
        var shifts = configurations.Select(static item => item.Shift).ToArray();
        var planned = configurations.Select(static item => item.Planned).ToArray();

        services.AddSingleton(
            new InMemoryProductionContextReader(contexts));
        services.AddSingleton<IProductionContextReader>(
            static provider => provider.GetRequiredService<
                InMemoryProductionContextReader>());
        services.AddSingleton(
            new InMemoryShiftScheduleReader(shifts));
        services.AddSingleton<IShiftScheduleReader>(
            static provider => provider.GetRequiredService<
                InMemoryShiftScheduleReader>());
        services.AddSingleton(
            new InMemoryPlannedProductionScheduleReader(planned));
        services.AddSingleton<IPlannedProductionScheduleReader>(
            static provider => provider.GetRequiredService<
                InMemoryPlannedProductionScheduleReader>());
        services.AddSingleton<ShiftOccurrenceResolver>();
        if (rosterMaterializationEnabled)
        {
            var rosterSection = section.GetRequiredSection("RosterMaterialization");
            services.AddSingleton(new MachineShiftRosterMaterializationRequest(
                DateOnly.Parse(
                    Required(rosterSection, "FromProductionDayInclusive"),
                    CultureInfo.InvariantCulture),
                DateOnly.Parse(
                    Required(rosterSection, "ToProductionDayExclusive"),
                    CultureInfo.InvariantCulture)));
            services.AddSingleton<MachineShiftOccurrenceRosterMaterializer>();
            services.AddSingleton(
                provider => new MachineShiftOccurrenceRosterMaterializationRuntimeSet(
                    schedulingScopes,
                    provider.GetRequiredService<MachineShiftOccurrenceRosterMaterializer>()));
        }
        services.AddSingleton<PlannedProductionIntervalResolver>();

        services.AddSingleton<ProjectionProductionContextActivityReader>();
        services.AddSingleton<IProductionContextActivityReader>(
            static provider => provider.GetRequiredService<
                ProjectionProductionContextActivityReader>());
        services.AddSingleton<InMemoryProductionQuantityEvidenceReader>();
        services.AddSingleton<IProductionQuantityEvidenceReader>(
            static provider => provider.GetRequiredService<
                InMemoryProductionQuantityEvidenceReader>());

        if (scopes.Length == 1)
        {
            services.AddSingleton(scopes[0]);
        }

        services.AddSingleton(
            provider =>
            {
                var activityReader = provider.GetRequiredService<
                    IProductionContextActivityReader>();
                var contextReader = provider.GetRequiredService<
                    IProductionContextReader>();
                var shiftResolver = provider.GetRequiredService<
                    ShiftOccurrenceResolver>();
                var plannedResolver = provider.GetRequiredService<
                    PlannedProductionIntervalResolver>();
                var quantityReader = provider.GetRequiredService<
                    IProductionQuantityEvidenceReader>();
                var store = provider.GetRequiredService<
                    IProductionContextProcessingStore>();
                List<ProductionContextProcessingRuntime> activityRuntimes = [];
                List<ProductionQuantityFactProcessingRuntime> quantityRuntimes = [];

                foreach (var item in configurations)
                {
                    var processorSuffix = item.Scope.MachineId.ToString();
                    activityRuntimes.Add(
                        new ProductionContextProcessingRuntime(
                            new ObservationProcessorId(
                                $"production-context:{processorSuffix}"),
                            activityReader,
                            contextReader,
                            shiftResolver,
                            plannedResolver,
                            store,
                            item.Scope,
                            batchSize));
                    quantityRuntimes.Add(
                        new ProductionQuantityFactProcessingRuntime(
                            new ObservationProcessorId(
                                $"production-quantity:{processorSuffix}"),
                            quantityReader,
                            shiftResolver,
                            store,
                            item.QuantityStreamId,
                            batchSize));
                }

                return new ProductionMetricInputRuntimeSet(
                    activityRuntimes,
                    quantityRuntimes,
                    pollingInterval);
            });

        if (rosterMaterializationEnabled)
        {
            services.AddHostedService<MachineShiftRosterMaterializationWorker>();
        }

        services.AddHostedService<ProductionMetricInputProcessingWorker>();
        return services;
    }

    private static MachineProductionConfiguration[] ReadMachineConfigurations(
        IConfigurationSection section,
        IReadOnlyList<ObservationStreamId> activityStreamIds)
    {
        var machineSections = section.GetSection("Machines").GetChildren().ToArray();
        if (machineSections.Length == 0)
        {
            if (activityStreamIds.Count != 1)
            {
                throw new InvalidOperationException(
                    "ProductionProcessing:Machines is required when multiple machines are configured.");
            }

            return [ReadMachineConfiguration(section, activityStreamIds[0])];
        }

        Dictionary<MachineId, IConfigurationSection> byMachine = [];
        foreach (var machineSection in machineSections)
        {
            var machineId = new MachineId(
                Guid.Parse(Required(machineSection, "MachineId")));
            if (!byMachine.TryAdd(machineId, machineSection))
            {
                throw new InvalidOperationException(
                    $"Duplicate production-processing machine '{machineId}'.");
            }
        }

        var expectedMachines = activityStreamIds
            .Select(static stream => stream.MachineId)
            .ToHashSet();
        if (byMachine.Keys.Any(machineId => !expectedMachines.Contains(machineId)))
        {
            throw new InvalidOperationException(
                "ProductionProcessing:Machines contains a machine that is not registered for activity processing.");
        }

        List<MachineProductionConfiguration> configurations = [];
        foreach (var activityStreamId in activityStreamIds)
        {
            if (!byMachine.TryGetValue(
                    activityStreamId.MachineId,
                    out var machineSection))
            {
                throw new InvalidOperationException(
                    $"No production-processing configuration exists for machine '{activityStreamId.MachineId}'.");
            }

            var configuredStreamKey = Required(
                machineSection,
                "ActivityStreamKey");
            if (!StringComparer.Ordinal.Equals(
                    configuredStreamKey,
                    activityStreamId.StreamKey))
            {
                throw new InvalidOperationException(
                    $"Production-processing activity stream for machine '{activityStreamId.MachineId}' does not match the registered stream.");
            }

            configurations.Add(
                ReadMachineConfiguration(machineSection, activityStreamId));
        }

        return configurations.ToArray();
    }

    private static MachineProductionConfiguration ReadMachineConfiguration(
        IConfigurationSection section,
        ObservationStreamId activityStreamId)
    {
        var companyId = new CompanyId(Required(section, "CompanyId"));
        var siteId = new SiteId(Required(section, "SiteId"));
        var lineId = new ProductionLineId(Required(section, "ProductionLineId"));
        var machineId = activityStreamId.MachineId;
        var scope = new ProductionContextProcessingScope
        {
            CompanyId = companyId,
            SiteId = siteId,
            ProductionLineId = lineId,
            MachineId = machineId,
            StreamId = activityStreamId,
        };
        scope.Validate();

        var contextAssignment = new ProductionContextAssignment
        {
            Id = new ProductionContextAssignmentId(
                Required(section, "ContextAssignmentId")),
            CompanyId = companyId,
            SiteId = siteId,
            ProductionLineId = lineId,
            MachineId = machineId,
            EffectiveFrom = DateTimeOffset.Parse(
                Required(section, "ContextEffectiveFromUtc"),
                CultureInfo.InvariantCulture),
        };

        var shiftSection = section.GetRequiredSection("Shift");
        var shiftAssignment = new ShiftScheduleAssignment
        {
            Id = new ShiftScheduleAssignmentId(
                Required(shiftSection, "AssignmentId")),
            CompanyId = companyId,
            SiteId = siteId,
            ProductionLineId = lineId,
            TimeZoneId = new FactoryTimeZoneId(
                Required(shiftSection, "TimeZoneId")),
            ShiftId = new ShiftId(Required(shiftSection, "ShiftId")),
            Name = Required(shiftSection, "Name"),
            StartsAtLocal = TimeOnly.Parse(
                Required(shiftSection, "StartsAtLocal"),
                CultureInfo.InvariantCulture),
            EndsAtLocal = TimeOnly.Parse(
                Required(shiftSection, "EndsAtLocal"),
                CultureInfo.InvariantCulture),
            EffectiveFrom = DateOnly.Parse(
                Required(shiftSection, "EffectiveFrom"),
                CultureInfo.InvariantCulture),
        };

        var plannedSection = section.GetRequiredSection("PlannedProduction");
        var plannedAssignment = new PlannedProductionScheduleAssignment
        {
            Id = new PlannedProductionScheduleAssignmentId(
                Required(plannedSection, "AssignmentId")),
            CompanyId = companyId,
            SiteId = siteId,
            ProductionLineId = lineId,
            TimeZoneId = new FactoryTimeZoneId(
                Required(plannedSection, "TimeZoneId")),
            EffectiveFrom = DateOnly.Parse(
                Required(plannedSection, "EffectiveFrom"),
                CultureInfo.InvariantCulture),
            PlannedWindows =
            [
                new PlannedProductionWindow
                {
                    StartsAtLocal = TimeOnly.Parse(
                        Required(plannedSection, "StartsAtLocal"),
                        CultureInfo.InvariantCulture),
                    EndsAtLocal = TimeOnly.Parse(
                        Required(plannedSection, "EndsAtLocal"),
                        CultureInfo.InvariantCulture),
                },
            ],
        };

        var quantityStreamId = new ObservationStreamId(
            machineId,
            section["QuantityStreamKey"] ?? "production-quantity");

        return new MachineProductionConfiguration(
            scope,
            quantityStreamId,
            contextAssignment,
            shiftAssignment,
            plannedAssignment);
    }

    private static string Required(
        IConfigurationSection section,
        string key) =>
        section[key] ?? throw new InvalidOperationException(
            $"{section.Path}:{key} is required.");

    private sealed record MachineProductionConfiguration(
        ProductionContextProcessingScope Scope,
        ObservationStreamId QuantityStreamId,
        ProductionContextAssignment Context,
        ShiftScheduleAssignment Shift,
        PlannedProductionScheduleAssignment Planned);
}
