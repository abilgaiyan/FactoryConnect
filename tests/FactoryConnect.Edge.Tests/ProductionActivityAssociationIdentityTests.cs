using FactoryConnect.Abstractions;
using FactoryConnect.Edge;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class ProductionActivityAssociationIdentityTests
{
    [Fact]
    public void ProductionOnlyProjectsOneAssociationOwnedStateStoreAndReader()
    {
        var streamId = new ObservationStreamId(MachineId.New(), "modbus:line-1");
        var services = new ServiceCollection();
        services.AddFactoryConnectProductionMetricInputs(
            ProductionConfiguration("InMemory"),
            streamId);

        using var provider = services.BuildServiceProvider();

        var association = provider.GetRequiredService<ProductionActivityAssociation>();
        var concreteStore = provider.GetRequiredService<
            InMemoryMachineStateActivityAuthorityStore>();
        var concreteReader = provider.GetRequiredService<
            JointProductionContextActivityReader>();
        var portableReader = provider.GetRequiredService<
            IProductionContextActivityReader>();

        Assert.Same(association.StateActivityStore, concreteStore);
        Assert.Same(association.ActivityReader, concreteReader);
        Assert.Same(concreteReader, portableReader);
    }

    [Fact]
    public void CombinedFinalizedInMemoryUsesObservationSelectedProviderOwnedStateStore()
    {
        var streamId = new ObservationStreamId(MachineId.New(), "modbus:line-1");
        var configuration = ProductionConfiguration("InMemory");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectEdgePersistence(configuration);
        services.AddFactoryConnectObservationProcessing(configuration, streamId);
        services.AddFactoryConnectProductionMetricInputs(configuration, streamId);

        using var provider = services.BuildServiceProvider();

        var providerServices = provider.GetRequiredService<PersistenceProviderServices>();
        var observationGraph = provider.GetRequiredService<ObservationAuthorityStoreGraph>();
        var association = provider.GetRequiredService<ProductionActivityAssociation>();
        var concreteStore = provider.GetRequiredService<
            InMemoryMachineStateActivityAuthorityStore>();
        var authorityStore = provider.GetRequiredService<
            IMachineStateActivityAuthorityStore>();
        var concreteReader = provider.GetRequiredService<
            JointProductionContextActivityReader>();
        var portableReader = provider.GetRequiredService<
            IProductionContextActivityReader>();

        Assert.Same(providerServices.MachineStateActivityAuthorityStore, observationGraph.StateActivityStore);
        Assert.Same(observationGraph.StateActivityStore, association.StateActivityStore);
        Assert.Same(association.StateActivityStore, concreteStore);
        Assert.Same(concreteStore, authorityStore);
        Assert.Same(association.ActivityReader, concreteReader);
        Assert.Same(concreteReader, portableReader);
    }

    [Fact]
    public void CombinedSqlUsesObservationSelectedEdgeFallbackStateStore()
    {
        var streamId = new ObservationStreamId(MachineId.New(), "modbus:line-1");
        var configuration = ProductionConfiguration(
            "SqlServer",
            "Server=test;Database=FactoryConnect;Integrated Security=True;TrustServerCertificate=True");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFactoryConnectEdgePersistence(configuration);
        services.AddFactoryConnectObservationProcessing(configuration, streamId);
        services.AddFactoryConnectProductionMetricInputs(configuration, streamId);

        using var provider = services.BuildServiceProvider();

        var providerServices = provider.GetRequiredService<PersistenceProviderServices>();
        var observationGraph = provider.GetRequiredService<ObservationAuthorityStoreGraph>();
        var association = provider.GetRequiredService<ProductionActivityAssociation>();
        var concreteStore = provider.GetRequiredService<
            InMemoryMachineStateActivityAuthorityStore>();
        var authorityStore = provider.GetRequiredService<
            IMachineStateActivityAuthorityStore>();
        var concreteReader = provider.GetRequiredService<
            JointProductionContextActivityReader>();
        var portableReader = provider.GetRequiredService<
            IProductionContextActivityReader>();

        Assert.Null(providerServices.MachineStateActivityAuthorityStore);
        Assert.Same(observationGraph.StateActivityStore, association.StateActivityStore);
        Assert.Same(association.StateActivityStore, concreteStore);
        Assert.Same(concreteStore, authorityStore);
        Assert.Same(association.ActivityReader, concreteReader);
        Assert.Same(concreteReader, portableReader);
    }

    private static IConfiguration ProductionConfiguration(
        string provider,
        string? connectionString = null)
    {
        Dictionary<string, string?> values = new()
        {
            ["Persistence:Provider"] = provider,
            ["ObservationProcessing:BatchSize"] = "10",
            ["ObservationProcessing:PollingInterval"] = "00:00:01",
            ["ObservationProcessing:Mappings:0:Source"] = "modbus",
            ["ObservationProcessing:Mappings:0:Address"] = "DI1",
            ["ObservationProcessing:Mappings:0:SignalKey"] = CanonicalSignalKeys.Running,
            ["ObservationProcessing:Mappings:0:Type"] = "Digital",
            ["ObservationProcessing:Mappings:0:Invert"] = "false",
            ["ProductionProcessing:BatchSize"] = "100",
            ["ProductionProcessing:PollingInterval"] = "00:00:01",
            ["ProductionProcessing:CompanyId"] = "COMP-1",
            ["ProductionProcessing:SiteId"] = "SITE-1",
            ["ProductionProcessing:ProductionLineId"] = "LINE-1",
            ["ProductionProcessing:ContextAssignmentId"] = "CTX-1",
            ["ProductionProcessing:ContextEffectiveFromUtc"] = "2026-01-01T00:00:00+00:00",
            ["ProductionProcessing:QuantityStreamKey"] = "production-quantity",
            ["ProductionProcessing:Shift:AssignmentId"] = "SHIFT-SCHEDULE-1",
            ["ProductionProcessing:Shift:ShiftId"] = "SHIFT-1",
            ["ProductionProcessing:Shift:Name"] = "Shift 1",
            ["ProductionProcessing:Shift:TimeZoneId"] = "UTC",
            ["ProductionProcessing:Shift:StartsAtLocal"] = "06:00:00",
            ["ProductionProcessing:Shift:EndsAtLocal"] = "14:00:00",
            ["ProductionProcessing:Shift:EffectiveFrom"] = "2026-01-01",
            ["ProductionProcessing:PlannedProduction:AssignmentId"] = "POT-1",
            ["ProductionProcessing:PlannedProduction:TimeZoneId"] = "UTC",
            ["ProductionProcessing:PlannedProduction:StartsAtLocal"] = "06:00:00",
            ["ProductionProcessing:PlannedProduction:EndsAtLocal"] = "14:00:00",
            ["ProductionProcessing:PlannedProduction:EffectiveFrom"] = "2026-01-01",
        };

        if (connectionString is not null)
        {
            values["PersistenceProviders:SqlServer:ConnectionString"] = connectionString;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
