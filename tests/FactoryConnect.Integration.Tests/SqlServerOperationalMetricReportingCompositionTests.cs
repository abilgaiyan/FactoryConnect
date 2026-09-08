using FactoryConnect.Abstractions;
using FactoryConnect.Api.Reporting;
using FactoryConnect.Core.Metrics;
using FactoryConnect.Persistence;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlServerOperationalMetricReportingCompositionTests
{
    [Fact]
    public void SqlServerProviderDeclaresOnlyImplementedReportingReaderCapabilities()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration();

        services.AddSqlServerPersistenceProvider(
            configuration.GetSection(SqlServerPersistenceOptions.SectionName));

        var registration = Assert.Single(
            services
                .Where(static descriptor =>
                    descriptor.ServiceType == typeof(IPersistenceProviderRegistration))
                .Select(static descriptor =>
                    Assert.IsAssignableFrom<IPersistenceProviderRegistration>(
                        descriptor.ImplementationInstance)));

        var expected =
            PersistenceProviderCapabilities.Core |
            PersistenceProviderCapabilities.OperationalMetricProjectionQuery |
            PersistenceProviderCapabilities.OperationalMetricReportingQuery;

        Assert.Equal(expected, registration.Capabilities);
        Assert.Equal(
            PersistenceProviderCapabilities.None,
            registration.Capabilities &
            (PersistenceProviderCapabilities.OperationalMetricProjectionStorage |
             PersistenceProviderCapabilities.MachineShiftOccurrenceRoster));
    }

    [Fact]
    public void SqlServerProviderActivatesProjectionAndReportingReadersThroughPersistenceFinalization()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration();

        services.AddSqlServerPersistenceProvider(
            configuration.GetSection(SqlServerPersistenceOptions.SectionName));
        services.AddFactoryConnectPersistence(
            configuration,
            PersistenceProviderCapabilities.OperationalMetricProjectionQuery |
            PersistenceProviderCapabilities.OperationalMetricReportingQuery);

        using var provider = services.BuildServiceProvider();

        var projectionReader =
            provider.GetRequiredService<IOperationalMetricProjectionQueryReader>();
        var reportingProvider =
            provider.GetRequiredService<IOperationalMetricReportingQueryProvider>();

        Assert.IsType<SqlServerOperationalMetricProjectionQueryReader>(projectionReader);
        Assert.IsType<SqlServerOperationalMetricReportingQueryProvider>(reportingProvider);
        Assert.Same(
            projectionReader,
            provider.GetRequiredService<IOperationalMetricProjectionQueryReader>());
        Assert.Same(
            reportingProvider,
            provider.GetRequiredService<IOperationalMetricReportingQueryProvider>());
    }

    [Fact]
    public void SqlServerProviderComposesExistingPublicReportingReaders()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration();

        services.AddSqlServerPersistenceProvider(
            configuration.GetSection(SqlServerPersistenceOptions.SectionName));
        services.AddFactoryConnectPersistence(
            configuration,
            PersistenceProviderCapabilities.OperationalMetricProjectionQuery |
            PersistenceProviderCapabilities.OperationalMetricReportingQuery);
        services.AddFactoryConnectOperationalMetricReporting();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<OperationalMetricReportingQueryReader>(
            provider.GetRequiredService<IOperationalMetricReportingQueryReader>());
        Assert.IsType<OperationalMetricReportReader>(
            provider.GetRequiredService<IOperationalMetricReportReader>());
    }

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Provider"] =
                    SqlServerPersistenceServiceCollectionExtensions.ProviderKey,
                ["PersistenceProviders:SqlServer:ConnectionString"] =
                    "Server=test;Database=FactoryConnect;Integrated Security=True;TrustServerCertificate=True",
            })
            .Build();
}
