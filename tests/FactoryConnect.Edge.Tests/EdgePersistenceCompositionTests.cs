using FactoryConnect.Abstractions;
using FactoryConnect.Edge;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class EdgePersistenceCompositionTests
{
    [Fact]
    public void InMemoryCoreSelectionDoesNotRequireSqlServerConfiguration()
    {
        var configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "InMemory",
            });
        var services = new ServiceCollection();

        services.AddFactoryConnectEdgePersistence(configuration);

        using var provider = services.BuildServiceProvider();
        var observation = provider.GetRequiredService<IObservationIngestionStore>();
        var production = provider.GetRequiredService<IProductionContextProcessingStore>();
        var reader = provider.GetRequiredService<IMetricInputReader>();
        var aggregation = provider.GetRequiredService<IMetricAggregationStore>();

        Assert.IsType<InMemoryObservationIngestionStore>(observation);
        Assert.Same(production, reader);
        Assert.Equal(
            "FactoryConnect.Core.InMemoryMetricAggregationStore",
            aggregation.GetType().FullName);
        Assert.Null(provider.GetService<IMetricAggregationRevisionReader>());
        Assert.Null(provider.GetService<IRevisionedOperationalMetricComponentSnapshotReader>());
        Assert.Null(provider.GetService<IOperationalMetricProjectionStore>());
        Assert.Null(provider.GetService<IOperationalMetricProjectionQueryReader>());
        Assert.Null(provider.GetService<IOperationalMetricReportingQueryProvider>());
        Assert.Null(provider.GetService<IMachineShiftOccurrenceRosterStore>());
    }

    [Fact]
    public void InMemoryFullCapabilitySelectionUsesOneProviderOwnedCapabilitySet()
    {
        var configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "InMemory",
            });
        var services = new ServiceCollection();

        services.AddFactoryConnectEdgePersistence(
            configuration,
            PersistenceProviderCapabilities.All);

        using var provider = services.BuildServiceProvider();
        var aggregation = provider.GetRequiredService<IMetricAggregationStore>();
        var revisionReader = provider.GetRequiredService<IMetricAggregationRevisionReader>();
        var snapshotReader = provider.GetRequiredService<IRevisionedOperationalMetricComponentSnapshotReader>();
        var projectionStore = provider.GetRequiredService<IOperationalMetricProjectionStore>();
        var projectionReader = provider.GetRequiredService<IOperationalMetricProjectionQueryReader>();
        var reportingProvider = provider.GetRequiredService<IOperationalMetricReportingQueryProvider>();
        var rosterStore = provider.GetRequiredService<IMachineShiftOccurrenceRosterStore>();

        Assert.Same(aggregation, revisionReader);
        Assert.Same(aggregation, snapshotReader);
        Assert.Same(projectionStore, projectionReader);
        Assert.Same(projectionStore, reportingProvider);
        Assert.Equal(
            "FactoryConnect.Core.InMemoryMachineShiftOccurrenceRosterStore",
            rosterStore.GetType().FullName);
        Assert.Equal(
            "FactoryConnect.Core.InMemoryOperationalMetricProjectionStore",
            projectionStore.GetType().FullName);
    }

    [Fact]
    public void SqlServerSelectionUsesConfiguredProviderWithoutRuntimeChanges()
    {
        var configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "SqlServer",
                ["PersistenceProviders:SqlServer:ConnectionString"] =
                    "Server=test;Database=FactoryConnect;Integrated Security=True",
            });
        var services = new ServiceCollection();
        services.AddSqlServerPersistenceProvider(
            configuration.GetSection(SqlServerPersistenceOptions.SectionName));
        services.AddFactoryConnectPersistence(configuration);

        using var provider = services.BuildServiceProvider();
        var observation = provider.GetRequiredService<IObservationIngestionStore>();
        var production = provider.GetRequiredService<IProductionContextProcessingStore>();
        var reader = provider.GetRequiredService<IMetricInputReader>();
        var aggregation = provider.GetRequiredService<IMetricAggregationStore>();

        Assert.Equal(
            "FactoryConnect.Persistence.SqlServer.SqlServerObservationIngestionStore",
            observation.GetType().FullName);
        Assert.Equal(
            "FactoryConnect.Persistence.SqlServer.SqlServerProductionContextProcessingStore",
            production.GetType().FullName);
        Assert.Equal(
            "FactoryConnect.Persistence.SqlServer.SqlServerMetricInputStore",
            reader.GetType().FullName);
        Assert.Equal(
            "FactoryConnect.Persistence.SqlServer.SqlServerMetricAggregationStore",
            aggregation.GetType().FullName);
    }

    [Fact]
    public void SqlServerFullCapabilitySelectionFailsDuringPersistenceFinalization()
    {
        var configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "SqlServer",
                ["PersistenceProviders:SqlServer:ConnectionString"] =
                    "Server=test;Database=FactoryConnect;Integrated Security=True",
            });
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddFactoryConnectEdgePersistence(
                configuration,
                PersistenceProviderCapabilities.All));

        Assert.Contains("SQLSERVER", exception.Message, StringComparison.Ordinal);
        Assert.Contains("OperationalMetricProjectionStorage", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration CreateConfiguration(
        IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
