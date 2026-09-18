using System.Data;
using FactoryConnect.Abstractions;
using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMappingCoverageAuthorityProviderConformanceTests :
    MappingCoverageAuthorityProviderConformanceTests,
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMappingCoverageAuthorityProviderConformanceTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    protected override IMappingCoverageAuthorityStore CreateStore() =>
        new SqlServerMappingCoverageAuthorityStore(_fixture.ConnectionString);

    protected override async ValueTask PrepareIdentityAsync(
        ObservationProcessorId processorId,
        ObservationStreamId streamId)
    {
        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF NOT EXISTS
            (
                SELECT 1
                FROM dbo.ObservationStreamCheckpoint
                WHERE MachineId = @MachineId
                  AND StreamKeyBinary = @StreamKeyBinary
            )
            BEGIN
                INSERT INTO dbo.ObservationStreamCheckpoint
                    (MachineId, StreamKeyBinary, StreamKey, InstanceId, NextSequence)
                VALUES
                    (@MachineId, @StreamKeyBinary, @StreamKey, 0, 0);
            END;
            """;
        command.Parameters.Add("@MachineId", SqlDbType.UniqueIdentifier).Value =
            streamId.MachineId.Value;
        command.Parameters.Add(
            "@StreamKeyBinary",
            SqlDbType.VarBinary,
            OrdinalStringKeyCodec.MaxCodeUnits * 2).Value =
            OrdinalStringKeyCodec.Encode(streamId.StreamKey);
        command.Parameters.Add("@StreamKey", SqlDbType.NVarChar, 256).Value =
            streamId.StreamKey;
        await command.ExecuteNonQueryAsync();
    }
}
