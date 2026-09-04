using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigrationLedgerMetadataReaderIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerMigrationLedgerMetadataReaderIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MissingLedgerIdentityIsReportedAsAbsent()
    {
        await using var connection = await OpenConnectionAsync();
        await DropLedgerIdentityAsync(connection);
        var reader = new SqlServerMigrationLedgerMetadataReader();

        var state = await reader.ResolveObjectAsync(connection, CancellationToken.None);

        Assert.Equal(SqlMigrationLedgerObjectKind.Absent, state.Kind);
        Assert.Null(state.ObjectId);
    }

    [Fact]
    public async Task ViewOccupyingLedgerIdentityIsReportedAsIncompatibleObject()
    {
        await using var connection = await OpenConnectionAsync();
        await DropLedgerIdentityAsync(connection);
        try
        {
            await ExecuteAsync(connection, "CREATE VIEW dbo.FactoryConnectMigrationHistory AS SELECT 1 AS MigrationId;");
            var reader = new SqlServerMigrationLedgerMetadataReader();

            var state = await reader.ResolveObjectAsync(connection, CancellationToken.None);

            Assert.Equal(SqlMigrationLedgerObjectKind.IncompatibleObject, state.Kind);
            Assert.Equal("V", state.CatalogObjectType);
        }
        finally
        {
            await DropLedgerIdentityAsync(connection);
        }
    }

    [Fact]
    public async Task SynonymOccupyingLedgerIdentityIsReportedAsIncompatibleObject()
    {
        await using var connection = await OpenConnectionAsync();
        await DropLedgerIdentityAsync(connection);
        try
        {
            await ExecuteAsync(
                connection,
                "CREATE SYNONYM dbo.FactoryConnectMigrationHistory FOR dbo.MachineObservation;");
            var reader = new SqlServerMigrationLedgerMetadataReader();

            var state = await reader.ResolveObjectAsync(connection, CancellationToken.None);

            Assert.Equal(SqlMigrationLedgerObjectKind.IncompatibleObject, state.Kind);
            Assert.Equal("SN", state.CatalogObjectType);
        }
        finally
        {
            await DropLedgerIdentityAsync(connection);
        }
    }

    [Fact]
    public async Task ExactLedgerTableMatchesFrozenInfrastructureContract()
    {
        await using var connection = await OpenConnectionAsync();
        await DropLedgerIdentityAsync(connection);
        try
        {
            await CreateExactLedgerAsync(connection, identityMigrationId: false);
            var reader = new SqlServerMigrationLedgerMetadataReader();
            var state = await reader.ResolveObjectAsync(connection, CancellationToken.None);

            Assert.Equal(SqlMigrationLedgerObjectKind.UserTable, state.Kind);
            var snapshot = await reader.ReadSchemaAsync(
                connection,
                Assert.IsType<int>(state.ObjectId),
                CancellationToken.None);

            SqlMigrationLedgerSchemaValidator.Validate(snapshot);
        }
        finally
        {
            await DropLedgerIdentityAsync(connection);
        }
    }

    [Fact]
    public async Task LedgerWithUnexpectedIdentityColumnIsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        await DropLedgerIdentityAsync(connection);
        try
        {
            await CreateExactLedgerAsync(connection, identityMigrationId: true);
            var reader = new SqlServerMigrationLedgerMetadataReader();
            var state = await reader.ResolveObjectAsync(connection, CancellationToken.None);
            var snapshot = await reader.ReadSchemaAsync(
                connection,
                Assert.IsType<int>(state.ObjectId),
                CancellationToken.None);

            Assert.Throws<InvalidOperationException>(
                () => SqlMigrationLedgerSchemaValidator.Validate(snapshot));
        }
        finally
        {
            await DropLedgerIdentityAsync(connection);
        }
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        return connection;
    }

    private static Task CreateExactLedgerAsync(
        SqlConnection connection,
        bool identityMigrationId)
    {
        var migrationId = identityMigrationId
            ? "MigrationId int IDENTITY(1,1) NOT NULL"
            : "MigrationId int NOT NULL";

        return ExecuteAsync(
            connection,
            $"""
            CREATE TABLE dbo.FactoryConnectMigrationHistory
            (
                {migrationId},
                Name nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CanonicalChecksum char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                AppliedAtUtc datetimeoffset(7) NOT NULL,
                CONSTRAINT PK_FactoryConnectMigrationHistory
                    PRIMARY KEY CLUSTERED (MigrationId ASC)
            );
            """);
    }

    private static async Task DropLedgerIdentityAsync(SqlConnection connection)
    {
        await ExecuteAsync(
            connection,
            """
            IF OBJECT_ID(N'dbo.FactoryConnectMigrationHistory', N'V') IS NOT NULL
                DROP VIEW dbo.FactoryConnectMigrationHistory;

            IF EXISTS
            (
                SELECT 1
                FROM sys.synonyms AS sn
                INNER JOIN sys.schemas AS s
                    ON s.schema_id = sn.schema_id
                WHERE s.name = N'dbo'
                  AND sn.name = N'FactoryConnectMigrationHistory'
            )
                DROP SYNONYM dbo.FactoryConnectMigrationHistory;

            IF OBJECT_ID(N'dbo.FactoryConnectMigrationHistory', N'U') IS NOT NULL
                DROP TABLE dbo.FactoryConnectMigrationHistory;
            """);
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
