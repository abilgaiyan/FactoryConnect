using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMachineShiftOccurrenceRosterStoreConformanceTests :
    MachineShiftOccurrenceRosterStoreConformanceTests,
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMachineShiftOccurrenceRosterStoreConformanceTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    protected override IMachineShiftOccurrenceRosterStore CreateStore() =>
        new SqlServerMachineShiftOccurrenceRosterStore(_fixture.ConnectionString);
}
