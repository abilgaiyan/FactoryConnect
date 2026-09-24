using System.Globalization;
using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        RejectLegacyShiftConfiguration(section);
        var shifts = ReadShiftScheduleAssignments(section);
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
        var contexts = configurations
            .SelectMany(static item => item.Contexts)
            .ToArray();
        var planned = configurations.Select(static item => item.Planned).ToArray();

        EnsureSchedulesExistForMachines(configurations, shifts);

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

        var activityReaderDescriptor =
            ServiceDescriptor.Singleton<IProductionContextActivityReader>(
                static provider => provider.GetRequiredService<
                    ProductionActivityAssociation>().ActivityReader);
        services.AddSingleton<ProductionActivityAssociation>(
            provider => new ProductionActivityAssociation(
                provider,
                services,
                activityReaderDescriptor));
        services.AddSingleton<InMemoryMachineStateActivityAuthorityStore>(
            static provider =>
                provider.GetRequiredService<ProductionActivityAssociation>()
                    .StateActivityStore as InMemoryMachineStateActivityAuthorityStore
                ?? throw new InvalidOperationException(
                    "The selected production state/activity store is not the InMemory compatibility store."));
        services.AddSingleton<JointProductionContextActivityReader>(
            static provider =>
                provider.GetRequiredService<ProductionActivityAssociation>()
                    .ActivityReader as JointProductionContextActivityReader
                ?? throw new InvalidOperationException(
                    "The selected production activity reader is not the InMemory compatibility reader."));
        services.Add(activityReaderDescriptor);
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
                IProductionContextProcessingStore publicationStore = store;
                if (rosterMaterializationEnabled)
                {
                    publicationStore = new RosterValidatedProductionContextProcessingStore(
                        store,
                        provider.GetRequiredService<IMachineShiftOccurrenceRosterStore>(),
                        schedulingScopes);
                }

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
                            publicationStore,
                            item.Scope,
                            batchSize));
                    quantityRuntimes.Add(
                        new ProductionQuantityFactProcessingRuntime(
                            new ObservationProcessorId(
                                $"production-quantity:{processorSuffix}"),
                            quantityReader,
                            shiftResolver,
                            publicationStore,
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

        var contextSections = section.GetSection("Contexts").GetChildren().ToArray();
        if (contextSections.Length == 0)
        {
            throw new InvalidOperationException(
                $"{section.Path}:Contexts must contain at least one production context.");
        }

        var contexts = contextSections
            .Select(contextSection => ReadProductionContextAssignment(
                contextSection,
                companyId,
                siteId,
                lineId,
                machineId))
            .ToArray();

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
            contexts,
            plannedAssignment);
    }

    private static ProductionContextAssignment ReadProductionContextAssignment(
        IConfigurationSection section,
        CompanyId companyId,
        SiteId siteId,
        ProductionLineId lineId,
        MachineId machineId)
    {
        var assignment = new ProductionContextAssignment
        {
            Id = new ProductionContextAssignmentId(
                Required(section, "AssignmentId")),
            CompanyId = companyId,
            SiteId = siteId,
            ProductionLineId = lineId,
            MachineId = machineId,
            ProductionOrderId = Optional(section, "ProductionOrderId") is { } productionOrderId
                ? new ProductionOrderId(productionOrderId)
                : null,
            OperationId = Optional(section, "OperationId") is { } operationId
                ? new OperationId(operationId)
                : null,
            PartId = Optional(section, "PartId") is { } partId
                ? new PartId(partId)
                : null,
            OperatorId = Optional(section, "OperatorId") is { } operatorId
                ? new OperatorId(operatorId)
                : null,
            EffectiveFrom = DateTimeOffset.Parse(
                Required(section, "EffectiveFrom"),
                CultureInfo.InvariantCulture),
            EffectiveTo = ParseOptionalDateTimeOffset(section, "EffectiveTo"),
        };
        assignment.Validate();
        return assignment;
    }

    private static ShiftScheduleAssignment[] ReadShiftScheduleAssignments(
        IConfigurationSection section)
    {
        var scheduleSections = section.GetSection("ShiftSchedules").GetChildren().ToArray();
        if (scheduleSections.Length == 0)
        {
            throw new InvalidOperationException(
                "ProductionProcessing:ShiftSchedules must contain at least one shared shift schedule.");
        }

        var assignments = scheduleSections.Select(ReadShiftScheduleAssignment).ToArray();
        _ = new InMemoryShiftScheduleReader(assignments);
        return assignments;
    }

    private static ShiftScheduleAssignment ReadShiftScheduleAssignment(
        IConfigurationSection section)
    {
        var activeDays = section.GetSection("ActiveDays")
            .GetChildren()
            .Select(day => Enum.Parse<DayOfWeek>(day.Value!, ignoreCase: true))
            .ToHashSet();
        if (activeDays.Count == 0)
        {
            throw new InvalidOperationException(
                $"{section.Path}:ActiveDays must contain at least one day.");
        }

        var timeZoneId = Required(section, "TimeZoneId");
        _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        var assignment = new ShiftScheduleAssignment
        {
            Id = new ShiftScheduleAssignmentId(Required(section, "AssignmentId")),
            CompanyId = new CompanyId(Required(section, "CompanyId")),
            SiteId = new SiteId(Required(section, "SiteId")),
            ProductionLineId = Optional(section, "ProductionLineId") is { } lineId
                ? new ProductionLineId(lineId)
                : null,
            TimeZoneId = new FactoryTimeZoneId(timeZoneId),
            ShiftId = new ShiftId(Required(section, "ShiftId")),
            Name = Required(section, "Name"),
            StartsAtLocal = TimeOnly.Parse(
                Required(section, "StartsAtLocal"),
                CultureInfo.InvariantCulture),
            EndsAtLocal = TimeOnly.Parse(
                Required(section, "EndsAtLocal"),
                CultureInfo.InvariantCulture),
            ActiveDays = activeDays,
            EffectiveFrom = DateOnly.Parse(
                Required(section, "EffectiveFrom"),
                CultureInfo.InvariantCulture),
            EffectiveTo = ParseOptionalDateOnly(section, "EffectiveTo"),
        };
        assignment.Validate();
        return assignment;
    }

    private static void RejectLegacyShiftConfiguration(IConfigurationSection section)
    {
        if (section.GetSection("Shift").Exists() ||
            section.GetSection("Machines").GetChildren().Any(
                static machine => machine.GetSection("Shift").Exists()))
        {
            throw new InvalidOperationException(
                "Legacy ProductionProcessing:Shift and ProductionProcessing:Machines[n]:Shift configuration is not supported. Use ProductionProcessing:ShiftSchedules.");
        }

        if (section["ContextAssignmentId"] is not null ||
            section["ContextEffectiveFromUtc"] is not null ||
            section.GetSection("Machines").GetChildren().Any(
                static machine =>
                    machine["ContextAssignmentId"] is not null ||
                    machine["ContextEffectiveFromUtc"] is not null))
        {
            throw new InvalidOperationException(
                "Legacy production-context properties are not supported. Configure assignments under ProductionProcessing:Machines[n]:Contexts.");
        }
    }

    private static void EnsureSchedulesExistForMachines(
        IReadOnlyList<MachineProductionConfiguration> machines,
        IReadOnlyList<ShiftScheduleAssignment> schedules)
    {
        foreach (var machine in machines)
        {
            var scope = machine.Scope;
            if (schedules.Any(schedule =>
                    schedule.CompanyId == scope.CompanyId &&
                    schedule.SiteId == scope.SiteId &&
                    (schedule.ProductionLineId is null ||
                        schedule.ProductionLineId == scope.ProductionLineId)))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"No shift schedule is configured for machine '{scope.MachineId}' in company '{scope.CompanyId}', site '{scope.SiteId}', and line '{scope.ProductionLineId}'.");
        }
    }

    private static DateOnly? ParseOptionalDateOnly(
        IConfigurationSection section,
        string key) =>
        Optional(section, key) is { } value
            ? DateOnly.Parse(value, CultureInfo.InvariantCulture)
            : null;

    private static DateTimeOffset? ParseOptionalDateTimeOffset(
        IConfigurationSection section,
        string key) =>
        Optional(section, key) is { } value
            ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture)
            : null;

    private static string? Optional(
        IConfigurationSection section,
        string key)
    {
        var value = section[key];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string Required(
        IConfigurationSection section,
        string key) =>
        section[key] ?? throw new InvalidOperationException(
            $"{section.Path}:{key} is required.");

    private sealed record MachineProductionConfiguration(
        ProductionContextProcessingScope Scope,
        ObservationStreamId QuantityStreamId,
        IReadOnlyList<ProductionContextAssignment> Contexts,
        PlannedProductionScheduleAssignment Planned);
}
