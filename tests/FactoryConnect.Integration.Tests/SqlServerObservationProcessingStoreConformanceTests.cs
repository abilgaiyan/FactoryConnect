using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerObservationProcessingStoreConformanceTests(
    SqlServerTestDatabaseFixture fixture) :
    ObservationProcessingStoreConformanceTests,
    IClassFixture<SqlServerTestDatabaseFixture>
{
    protected override IObservationIngestionStore CreateStore() =>
        new SqlServerObservationIngestionStore(fixture.ConnectionString);
}
