using FactoryConnect.Abstractions;
using FactoryConnect.Persistence;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryConnect.Integration.Tests;

public sealed class OperationalMetricCoverageCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoverageStoreIsActivatedOnlyWhenExplicitlyRequested(bool requested)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "SqlServer",
            ["PersistenceProviders:SqlServer:ConnectionString"] = "Server=test;Database=test;",
        }).Build();
        var services = new ServiceCollection();
        services.AddSqlServerPersistenceProvider(configuration.GetSection(SqlServerPersistenceOptions.SectionName));
        services.AddFactoryConnectPersistence(configuration, PersistenceProviderCapabilities.Core |
            (requested ? PersistenceProviderCapabilities.OperationalMetricCoverageAssessmentStorage : PersistenceProviderCapabilities.None));
        using var provider = services.BuildServiceProvider();
        var store = provider.GetService<IOperationalMetricCoverageAssessmentStore>();
        if (requested)
        {
            Assert.IsType<SqlServerOperationalMetricCoverageAssessmentStore>(store);
            Assert.Same(store, provider.GetRequiredService<PersistenceProviderServices>().OperationalMetricCoverageAssessmentStore);
        }
        else { Assert.Null(store); }
    }
}
