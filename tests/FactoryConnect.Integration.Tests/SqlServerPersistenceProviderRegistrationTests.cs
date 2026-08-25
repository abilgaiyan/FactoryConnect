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
    public void SelectedSqlServerProviderCreatesStoreWithConfiguredConnectionString()
    {
        const string connectionString = "Server=test;Database=test;";
        ServiceCollection services = new();
        var configuration = BuildConfiguration(
            selectedProvider: "SqlServer",
            connectionString);

        services.AddSqlServerPersistenceProvider(
            configuration.GetRequiredSection(
                SqlServerPersistenceOptions.SectionName));
        services.AddFactoryConnectPersistence(configuration);

        using var provider = services.BuildServiceProvider();

        var store = Assert.IsType<SqlServerObservationIngestionStore>(
            provider.GetRequiredService<IObservationIngestionStore>());

        Assert.Equal(connectionString, store.ConnectionString);
    }

    [Fact]
    public void RegistrationDoesNotActivateStore()
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
