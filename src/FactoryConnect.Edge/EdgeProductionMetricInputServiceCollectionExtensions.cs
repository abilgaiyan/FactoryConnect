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
        var planned = configurations.SelectMany(static item => item.Planned)
            .GroupBy(static assignment => assignment.Id)
            .Select(static group =>
            {
                var first = group.First();
                if (group.Skip(1).Any(other =>
                    other.CompanyId != first.CompanyId ||
                    other.SiteId != first.SiteId ||
                    other.ProductionLineId != first.ProductionLineId ||
                    other.TimeZoneId != first.TimeZoneId ||
                    other.EffectiveFrom != first.EffectiveFrom ||
                    other.EffectiveTo != first.EffectiveTo ||
                    !other.ActiveDays.SetEquals(first.ActiveDays) ||
                    !other.PlannedWindows.SequenceEqual(first.PlannedWindows) ||
                    !other.BreakWindows.SequenceEqual(first.BreakWindows)))
                {
                    throw new InvalidOperationException(
                        $"Planned production assignment '{first.Id}' has conflicting machine configurations.");
                }

                return first;
            })
            .ToArray();
        ValidatePlannedAssignmentScopes(planned);
        var demoCanonicalInputs = bool.TryParse(
            configuration["DemoCanonicalInputs:Enabled"], out var demoEnabled) && demoEnabled;
        if (demoCanonicalInputs &&
            !string.Equals(configuration["DemoCanonicalInputs:SyntheticGoodQuantity"], "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Demo canonical inputs require explicit synthetic good-quantity approval.");
        }
        if (demoCanonicalInputs && configurations.Any(static item =>
            !string.Equals(item.QuantityStreamId.StreamKey, "part_count", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Demo canonical inputs require part_count quantity streams.");
        }

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
        services.AddSingleton(new LoadedPlannedProductionConfiguration(
            System.Text.Json.JsonSerializer.Serialize(configurations
                .OrderBy(static item => item.Scope.MachineId.ToString(), StringComparer.Ordinal)
                .Select(item => new
                {
                    MachineId = item.Scope.MachineId.ToString(),
                    Assignments = item.Planned.Select(assignment => new
                    {
                        AssignmentId = assignment.Id.Value,
                        CompanyId = assignment.CompanyId.Value,
                        SiteId = assignment.SiteId.Value,
                        ProductionLineId = assignment.ProductionLineId?.Value,
                        TimeZoneId = assignment.TimeZoneId.Value,
                        assignment.EffectiveFrom,
                        assignment.EffectiveTo,
                        ActiveDays = assignment.ActiveDays.Order().Select(static day => day.ToString()),
                        assignment.PlannedWindows,
                        assignment.BreakWindows,
                    }),
                }))));
        services.AddHostedService<PlannedProductionConfigurationLog>();


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
        services.AddSingleton<IProductionQuantityEvidenceReader>(provider =>
            demoCanonicalInputs
                ? new DemoPartCountEvidenceReader(
                    provider.GetRequiredService<IObservationIngestionStore>() as IDurableObservationReader
                        ?? throw new InvalidOperationException("Demo quantity requires a durable observation reader."),
                    provider.GetRequiredService<IProductionContextReader>(),
                    provider.GetRequiredService<ShiftOccurrenceResolver>(),
                    scopes.ToDictionary(static scope => scope.MachineId))
                : provider.GetRequiredService<InMemoryProductionQuantityEvidenceReader>());

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
            ProductionStartsAt = ParseOptionalDateTimeOffset(section, "ProductionStartsAt"),
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

        var plannedAssignments = ReadPlannedAssignments(section, companyId, siteId, lineId);

        var quantityStreamId = new ObservationStreamId(
            machineId,
            section["QuantityStreamKey"] ?? "production-quantity");

        return new MachineProductionConfiguration(
            scope,
            quantityStreamId,
            contexts,
            plannedAssignments);
    }

    private static PlannedProductionScheduleAssignment[] ReadPlannedAssignments(
        IConfigurationSection section, CompanyId companyId, SiteId siteId, ProductionLineId lineId)
    {
        var legacy = section.GetSection("PlannedProduction");
        var collection = section.GetSection("PlannedProductionAssignments");
        if (legacy.Exists() && collection.Exists())
        {
            throw new InvalidOperationException("Use exactly one of PlannedProduction or PlannedProductionAssignments; remove the legacy object when adopting retained assignments.");
        }

        if (!collection.Exists())
        {
            return [ReadPlannedAssignment(section.GetRequiredSection("PlannedProduction"), companyId, siteId, lineId)];
        }

        var children = collection.GetChildren().ToArray();
        if (collection.Value is not null || children.Length == 0 ||
            children.Where((child, index) => child.Key != index.ToString(CultureInfo.InvariantCulture)).Any())
        {
            throw new InvalidOperationException("PlannedProductionAssignments must be a nonempty, contiguous indexed array.");
        }

        var assignments = children.Select(child => ReadPlannedAssignment(child, companyId, siteId, lineId))
            .OrderBy(static assignment => assignment.EffectiveFrom)
            .ThenBy(static assignment => assignment.Id.Value, StringComparer.Ordinal).ToArray();
        if (assignments.Select(static assignment => assignment.Id).Distinct().Count() != assignments.Length)
        {
            throw new InvalidOperationException("Retained planned production assignment IDs must be unique per machine.");
        }

        for (var index = 1; index < assignments.Length; index++)
        {
            var previous = assignments[index - 1];
            var current = assignments[index];
            if (previous.EffectiveTo != current.EffectiveFrom || previous.TimeZoneId != current.TimeZoneId)
            {
                throw new InvalidOperationException("Retained planned production assignments must have adjacent exclusive business-date boundaries and one timezone; gaps and overlaps are not replacement chains.");
            }
        }

        return assignments;
    }

    private static PlannedProductionScheduleAssignment ReadPlannedAssignment(
        IConfigurationSection plannedSection, CompanyId companyId, SiteId siteId, ProductionLineId lineId)
    {
        var breakWindowsSection = plannedSection.GetSection("BreakWindows");
        if (breakWindowsSection.Value is not null)
        {
            throw new InvalidOperationException(
                $"Configuration '{breakWindowsSection.Path}' must be an array of break windows, not a scalar value.");
        }

        var assignment = new PlannedProductionScheduleAssignment
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
            EffectiveTo = ParseOptionalDateOnly(plannedSection, "EffectiveTo"),
            ActiveDays = ReadPlannedActiveDays(plannedSection),
            BreakWindows = breakWindowsSection.GetChildren().Select(ReadWindow).ToArray(),
            PlannedWindows = ReadPlannedWindows(plannedSection),
        };

        assignment.Validate();
        ValidateConfiguredBreakWindows(assignment);

        _ = TimeZoneInfo.FindSystemTimeZoneById(assignment.TimeZoneId.Value);
        return assignment;
    }

    private static void ValidatePlannedAssignmentScopes(PlannedProductionScheduleAssignment[] assignments)
    {
        foreach (var scope in assignments.GroupBy(static assignment => (assignment.SiteId, assignment.ProductionLineId)))
        {
            var ordered = scope.OrderBy(static assignment => assignment.EffectiveFrom).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                if (ordered[index - 1].CompanyId != ordered[index].CompanyId ||
                    ordered[index - 1].EffectiveTo is not { } end || end > ordered[index].EffectiveFrom)
                {
                    throw new InvalidOperationException("Conflicting planned production assignments share a site/line scope.");
                }
            }
        }

        // Resolve around every activation/termination boundary, including a full weekday cycle
        // and physical lookaround. This also rejects cross-business-date window collisions.
        var resolver = new PlannedProductionIntervalResolver(new InMemoryPlannedProductionScheduleReader(assignments));
        foreach (var assignment in assignments)
        {
            ValidateBoundary(assignment.EffectiveFrom);
            if (assignment.EffectiveTo is { } end) ValidateBoundary(end);

            void ValidateBoundary(DateOnly boundary)
            {
                var from = DateOnly.FromDayNumber(Math.Max(DateOnly.MinValue.DayNumber, boundary.DayNumber - 7));
                var to = DateOnly.FromDayNumber(Math.Min(DateOnly.MaxValue.DayNumber, boundary.DayNumber + 8));
                // The startup reader is in-memory; no SQL/network work or async lock is involved.
                _ = resolver.ResolveAsync(assignment.SiteId, assignment.ProductionLineId!.Value,
                    from, to, CancellationToken.None).GetAwaiter().GetResult();
            }
        }
    }

    private static void ValidateConfiguredBreakWindows(PlannedProductionScheduleAssignment assignment)
    {
        _ = PlannedProductionBreakPlacement.Resolve(assignment.PlannedWindows, assignment.BreakWindows);
    }

    private static HashSet<DayOfWeek> ReadPlannedActiveDays(IConfigurationSection section)
    {
        var days = section.GetSection("ActiveDays").GetChildren().ToArray();
        return days.Length == 0 ? new HashSet<DayOfWeek>(Enum.GetValues<DayOfWeek>())
            : days.Select(day => Enum.Parse<DayOfWeek>(day.Value!, true)).ToHashSet();
    }

    private static PlannedProductionWindow ReadWindow(IConfigurationSection section)
    {
        var window = new PlannedProductionWindow
        {
            StartsAtLocal = TimeOnly.Parse(Required(section, "StartsAtLocal"), CultureInfo.InvariantCulture),
            EndsAtLocal = TimeOnly.Parse(Required(section, "EndsAtLocal"), CultureInfo.InvariantCulture),
        };
        return Optional(section, "StartDayOffset") is { } offset
            ? window with { StartDayOffset = int.Parse(offset, CultureInfo.InvariantCulture) }
            : window;
    }

    private static PlannedProductionWindow[] ReadPlannedWindows(IConfigurationSection section)
    {
        var collection = section.GetSection("PlannedWindows");
        if (collection.Exists())
        {
            if (collection.Value is not null || section["StartsAtLocal"] is not null ||
                section["EndsAtLocal"] is not null)
            {
                throw new InvalidOperationException("Use either PlannedWindows[] or scalar planned times.");
            }
            var windows = collection.GetChildren().Select(ReadWindow).ToArray();
            if (windows.Length == 0) throw new InvalidOperationException("PlannedWindows must not be empty.");
            return windows;
        }
        return [ReadWindow(section)];
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
            StartDayOffset = int.Parse(section["StartDayOffset"] ?? "0", CultureInfo.InvariantCulture),
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
        IReadOnlyList<PlannedProductionScheduleAssignment> Planned);
}
