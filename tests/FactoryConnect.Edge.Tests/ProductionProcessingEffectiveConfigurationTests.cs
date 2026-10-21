using FactoryConnect.Abstractions;
using FactoryConnect.Core;
using FactoryConnect.Edge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

/// <summary>
/// FC-STAB-G1.1: prove what production composition sees after configuration
/// providers have been combined, independently of the factory server.
/// </summary>
public sealed class ProductionProcessingEffectiveConfigurationTests
{
    [Fact]
    public void MissingContextsAreRejectedDuringServiceRegistration()
    {
        // A fresh provider with no context keys models a genuinely absent section.
        var configuration = Fixture(includeContexts: false);
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddFactoryConnectProductionMetricInputs(
                configuration,
                [Stream()]));

        Assert.Contains("Contexts must contain at least one production context",
            exception.Message);
    }

    [Fact]
    public void MissingPlannedProductionIsRejectedDuringServiceRegistration()
    {
        var services = new ServiceCollection();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddFactoryConnectProductionMetricInputs(
                Fixture(includePlannedProduction: false),
                [Stream()]));

        Assert.Contains("PlannedProduction", exception.Message);
    }

    [Fact]
    public void LaterProviderNotSpecifyingChildKeysRetainsEarlierMachineAssignments()
    {
        var baseValues = Values(includeContexts: true, includePlannedProduction: true);
        var overlay = new Dictionary<string, string?>
        {
            ["ProductionProcessing:Machines:0:MachineId"] =
                "677b030b-1ede-4a67-82ae-6a494791357f",
            ["ProductionProcessing:Machines:0:CompanyId"] = "GAJRA",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(baseValues)
            .AddInMemoryCollection(overlay)
            .Build();

        Assert.Equal("GAJRA",
            configuration["ProductionProcessing:Machines:0:CompanyId"]);
        Assert.Equal("CTX-BASE",
            configuration["ProductionProcessing:Machines:0:Contexts:0:AssignmentId"]);
        Assert.Equal("POT-BASE",
            configuration["ProductionProcessing:Machines:0:PlannedProduction:AssignmentId"]);

        var services = new ServiceCollection();
        services.AddFactoryConnectProductionMetricInputs(configuration, [Stream()]);
    }

    [Fact]
    public void LaterProviderCannotSilentlyRemoveEarlierMachineChildKeysByOmission()
    {
        var baseValues = Values(includeContexts: true, includePlannedProduction: true);
        var overlay = new Dictionary<string, string?>
        {
            ["ProductionProcessing:Machines:0:MachineId"] =
                "677b030b-1ede-4a67-82ae-6a494791357f",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(baseValues)
            .AddInMemoryCollection(overlay)
            .Build();

        Assert.NotEmpty(configuration.GetSection(
            "ProductionProcessing:Machines:0:Contexts").GetChildren());
        Assert.True(configuration.GetSection(
            "ProductionProcessing:Machines:0:PlannedProduction").Exists());
    }

    private static ObservationStreamId Stream() =>
        new(new MachineId(Guid.Parse("677b030b-1ede-4a67-82ae-6a494791357f")),
            "mtconnect:GG-003");

    private static IConfiguration Fixture(
        bool includeContexts = true,
        bool includePlannedProduction = true) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(Values(includeContexts, includePlannedProduction))
            .Build();

    private static Dictionary<string, string?> Values(
        bool includeContexts,
        bool includePlannedProduction)
    {
        var values = new Dictionary<string, string?>
        {
            ["ProductionProcessing:BatchSize"] = "100",
            ["ProductionProcessing:PollingInterval"] = "00:00:01",
            ["ProductionProcessing:Machines:0:MachineId"] =
                "677b030b-1ede-4a67-82ae-6a494791357f",
            ["ProductionProcessing:Machines:0:ActivityStreamKey"] =
                "mtconnect:GG-003",
            ["ProductionProcessing:Machines:0:CompanyId"] = "COMP-1",
            ["ProductionProcessing:Machines:0:SiteId"] = "SITE-1",
            ["ProductionProcessing:Machines:0:ProductionLineId"] = "LINE-1",
            ["ProductionProcessing:ShiftSchedules:0:AssignmentId"] = "SHIFT-1",
            ["ProductionProcessing:ShiftSchedules:0:CompanyId"] = "COMP-1",
            ["ProductionProcessing:ShiftSchedules:0:SiteId"] = "SITE-1",
            ["ProductionProcessing:ShiftSchedules:0:ProductionLineId"] = "LINE-1",
            ["ProductionProcessing:ShiftSchedules:0:ShiftId"] = "SHIFT-1",
            ["ProductionProcessing:ShiftSchedules:0:Name"] = "Shift 1",
            ["ProductionProcessing:ShiftSchedules:0:TimeZoneId"] = "UTC",
            ["ProductionProcessing:ShiftSchedules:0:StartsAtLocal"] = "07:30:00",
            ["ProductionProcessing:ShiftSchedules:0:EndsAtLocal"] = "16:00:00",
            ["ProductionProcessing:ShiftSchedules:0:ActiveDays:0"] = "Wednesday",
            ["ProductionProcessing:ShiftSchedules:0:EffectiveFrom"] = "2026-10-15",
        };
        if (includeContexts)
        {
            values["ProductionProcessing:Machines:0:Contexts:0:AssignmentId"] = "CTX-BASE";
            values["ProductionProcessing:Machines:0:Contexts:0:EffectiveFrom"] =
                "2026-10-15T00:00:00+00:00";
        }
        if (includePlannedProduction)
        {
            values["ProductionProcessing:Machines:0:PlannedProduction:AssignmentId"] =
                "POT-BASE";
            values["ProductionProcessing:Machines:0:PlannedProduction:TimeZoneId"] = "UTC";
            values["ProductionProcessing:Machines:0:PlannedProduction:StartsAtLocal"] =
                "07:30:00";
            values["ProductionProcessing:Machines:0:PlannedProduction:EndsAtLocal"] =
                "16:00:00";
            values["ProductionProcessing:Machines:0:PlannedProduction:EffectiveFrom"] =
                "2026-10-15";
        }
        return values;
    }
}
