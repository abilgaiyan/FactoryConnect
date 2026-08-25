using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerObservationIngestionStoreConformanceTests :
    ObservationIngestionStoreConformanceTests,
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerObservationIngestionStoreConformanceTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    protected override IObservationIngestionStore CreateStore() =>
        new SqlServerObservationIngestionStore(
            _fixture.ConnectionString);

    protected override int ReadObservationCount(
        IObservationIngestionStore store,
        ObservationStreamId streamId)
    {
        var sqlStore = Assert.IsType<SqlServerObservationIngestionStore>(store);
        using var connection = new SqlConnection(sqlStore.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM dbo.MachineObservation
            WHERE MachineId = @MachineId
              AND StreamKeyBinary = @StreamKeyBinary;
            """;
        command.Parameters.AddWithValue(
            "@MachineId",
            streamId.MachineId.Value);
        command.Parameters.AddWithValue(
            "@StreamKeyBinary",
            OrdinalStringKeyCodec.Encode(streamId.StreamKey));

        return Convert.ToInt32(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
