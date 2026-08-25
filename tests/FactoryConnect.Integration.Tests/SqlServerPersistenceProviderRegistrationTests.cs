using FactoryConnect.Abstractions;
using FactoryConnect.Persistence;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlServerPersistenceProviderRegistrationTests
{
    [Fact]
    public void RegistrationRequiresConnectionString()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddSqlServerPersistenceProvider(
                configuration.GetRequiredSection(
                    SqlServerPersistenceOptions.SectionName)));

        Assert.Equal(
            "PersistenceProviders:SqlServer:ConnectionString is required.",
            exception.Message);
    }

    [Fact]
    public void RegistrationDoesNotActivateStore()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("Server=test;Database=test;");

        services.AddSqlServerPersistenceProvider(
            configuration.GetRequiredSection(
                SqlServerPersistenceOptions.SectionName));

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType ==
                typeof(IObservationIngestionStore));
    }

    [Fact]
    public void SqlServerProviderIsSelectedThroughNeutralPersistence()
    {
        ServiceCollection services = new();
        var configuration = BuildConfiguration("Server=test;Database=test;");

        services.AddSqlServerPersistenceProvider(
            configuration.GetRequiredSection(
                SqlServerPersistenceOptions.SectionName));
        services.AddFactoryConnectPersistence(configuration);

        using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IObservationIngestionStore>();

        Assert.Equal("SqlServerObservationIngestionStore", store.GetType().Name);
    }

    private static IConfiguration BuildConfiguration(
        string? connectionString = null)
    {
        Dictionary<string, string?> values =
            new()
            {
                ["Persistence:Provider"] = "SqlServer",
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
