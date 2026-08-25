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
        var store = provider.GetRequiredService<IObservationIngestionStore>();

        Assert.IsType<InMemoryObservationIngestionStore>(store);
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
        var store = provider.GetRequiredService<IObservationIngestionStore>();

        Assert.Equal(
            "FactoryConnect.Persistence.SqlServer.SqlServerObservationIngestionStore",
            store.GetType().FullName);
    }

    private static IConfiguration CreateConfiguration(
        IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
