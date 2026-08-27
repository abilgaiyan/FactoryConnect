using FactoryConnect.Abstractions;
using FactoryConnect.Edge;
using FactoryConnect.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Edge.Tests;

public sealed class EdgePersistenceCompositionTests
{
    [Fact]
    public void InMemorySelectionDoesNotRequireSqlServerConfiguration()
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

        services.AddFactoryConnectEdgePersistence(configuration);

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

    private static IConfiguration CreateConfiguration(
        IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
