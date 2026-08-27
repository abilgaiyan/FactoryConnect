using FactoryConnect.Abstractions;
using FactoryConnect.Infrastructure;
using FactoryConnect.Persistence;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlServerPersistenceProviderRegistrationTests
{
    [Fact]
    public void UnselectedSqlServerProviderDoesNotRequireConfiguration()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration(
            selectedProvider: "InMemory");

        services.AddSqlServerPersistenceProvider(
            configuration.GetSection(
                SqlServerPersistenceOptions.SectionName));
        services.AddInMemoryPersistenceProvider();
        services.AddFactoryConnectPersistence(configuration);

        using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IObservationIngestionStore>();

        Assert.IsType<InMemoryObservationIngestionStore>(store);
    }

    [Fact]
    public void InMemorySelectionResolvesCoherentMetricPipelineCapabilities()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("InMemory");

        services.AddSqlServerPersistenceProvider(
            configuration.GetSection(SqlServerPersistenceOptions.SectionName));
        services.AddInMemoryPersistenceProvider();
        services.AddFactoryConnectPersistence(configuration);

        using var provider = services.BuildServiceProvider();
        var observation = provider.GetRequiredService<IObservationIngestionStore>();
        var production = provider.GetRequiredService<IProductionContextProcessingStore>();
        var reader = provider.GetRequiredService<IMetricInputReader>();
        var aggregation = provider.GetRequiredService<IMetricAggregationStore>();

        Assert.IsType<InMemoryObservationIngestionStore>(observation);
        Assert.IsType<FactoryConnect.Core.InMemoryProductionContextProcessingStore>(production);
        Assert.Same(production, reader);
        Assert.IsType<FactoryConnect.Core.InMemoryMetricAggregationStore>(aggregation);
        Assert.IsNotType<SqlServerObservationIngestionStore>(observation);
    }

    [Fact]
    public void SelectedSqlServerProviderRequiresConnectionString()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration(
            selectedProvider: "SqlServer");

        services.AddSqlServerPersistenceProvider(
            configuration.GetSection(
                SqlServerPersistenceOptions.SectionName));
        services.AddFactoryConnectPersistence(configuration);

        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IObservationIngestionStore>());

        Assert.Equal(
            "PersistenceProviders:SqlServer:ConnectionString is required.",
            exception.Message);
    }

    [Fact]
    public void SelectedSqlServerProviderCreatesAllCapabilitiesWithConfiguredConnectionString()
    {
        const string connectionString = "Server=test;Database=test;";
        ServiceCollection services = new();
        var configuration = BuildConfiguration(
            selectedProvider: "SqlServer",
            connectionString);

        services.AddInMemoryPersistenceProvider();
        services.AddSqlServerPersistenceProvider(
            configuration.GetRequiredSection(
                SqlServerPersistenceOptions.SectionName));
        services.AddFactoryConnectPersistence(configuration);

        using var provider = services.BuildServiceProvider();

        var observation = Assert.IsType<SqlServerObservationIngestionStore>(
            provider.GetRequiredService<IObservationIngestionStore>());
        Assert.IsType<SqlServerProductionContextProcessingStore>(
            provider.GetRequiredService<IProductionContextProcessingStore>());
        Assert.IsType<SqlServerMetricInputStore>(
            provider.GetRequiredService<IMetricInputReader>());
        Assert.IsType<SqlServerMetricAggregationStore>(
            provider.GetRequiredService<IMetricAggregationStore>());

        Assert.Equal(connectionString, observation.ConnectionString);
    }

    [Fact]
    public void RegistrationDoesNotActivateAnySelectedCapability()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration(
            selectedProvider: "SqlServer",
            connectionString: "Server=test;Database=test;");

        services.AddSqlServerPersistenceProvider(
            configuration.GetRequiredSection(
                SqlServerPersistenceOptions.SectionName));

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType ==
                typeof(IObservationIngestionStore));
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType ==
                typeof(IProductionContextProcessingStore));
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType ==
                typeof(IMetricInputReader));
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType ==
                typeof(IMetricAggregationStore));
    }

    private static IConfiguration BuildConfiguration(
        string selectedProvider,
        string? connectionString = null)
    {
        Dictionary<string, string?> values =
            new()
            {
                ["Persistence:Provider"] = selectedProvider,
            };

        if (connectionString is not null)
        {
            values[
                "PersistenceProviders:SqlServer:ConnectionString"] =
                connectionString;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
