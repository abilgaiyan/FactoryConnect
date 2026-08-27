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
        ObservationStreamId activityStreamId)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(activityStreamId);

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

        services.AddSingleton(scope);
        services.AddSingleton(
            new InMemoryProductionContextReader([contextAssignment]));
        services.AddSingleton<IProductionContextReader>(
            static provider => provider.GetRequiredService<
                InMemoryProductionContextReader>());
        services.AddSingleton(
            new InMemoryShiftScheduleReader([shiftAssignment]));
        services.AddSingleton<IShiftScheduleReader>(
            static provider => provider.GetRequiredService<
                InMemoryShiftScheduleReader>());
        services.AddSingleton(
            new InMemoryPlannedProductionScheduleReader([plannedAssignment]));
        services.AddSingleton<IPlannedProductionScheduleReader>(
            static provider => provider.GetRequiredService<
                InMemoryPlannedProductionScheduleReader>());
        services.AddSingleton<ShiftOccurrenceResolver>();
        services.AddSingleton<PlannedProductionIntervalResolver>();

        services.AddSingleton<ProjectionProductionContextActivityReader>();
        services.AddSingleton<IProductionContextActivityReader>(
            static provider => provider.GetRequiredService<
                ProjectionProductionContextActivityReader>());
        services.AddSingleton<InMemoryProductionQuantityEvidenceReader>();
        services.AddSingleton<IProductionQuantityEvidenceReader>(
            static provider => provider.GetRequiredService<
                InMemoryProductionQuantityEvidenceReader>());

        var quantityStreamId = new ObservationStreamId(
            machineId,
            section["QuantityStreamKey"] ?? "production-quantity");

        services.AddSingleton(
            provider => new ProductionMetricInputRuntimeSet(
                [
                    new ProductionContextProcessingRuntime(
                        new ObservationProcessorId("production-context"),
                        provider.GetRequiredService<IProductionContextActivityReader>(),
                        provider.GetRequiredService<IProductionContextReader>(),
                        provider.GetRequiredService<ShiftOccurrenceResolver>(),
                        provider.GetRequiredService<PlannedProductionIntervalResolver>(),
                        provider.GetRequiredService<IProductionContextProcessingStore>(),
                        scope,
                        batchSize),
                ],
                [
                    new ProductionQuantityFactProcessingRuntime(
                        new ObservationProcessorId("production-quantity"),
                        provider.GetRequiredService<IProductionQuantityEvidenceReader>(),
                        provider.GetRequiredService<ShiftOccurrenceResolver>(),
                        provider.GetRequiredService<IProductionContextProcessingStore>(),
                        quantityStreamId,
                        batchSize),
                ],
                pollingInterval));

        services.AddHostedService<ProductionMetricInputProcessingWorker>();
        return services;
    }

    private static string Required(
        IConfigurationSection section,
        string key) =>
        section[key] ?? throw new InvalidOperationException(
            $"{section.Path}:{key} is required.");
}
