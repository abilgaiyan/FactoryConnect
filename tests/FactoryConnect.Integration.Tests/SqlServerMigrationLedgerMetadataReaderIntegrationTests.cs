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
    public async Task MissingLedgerIdentityIsReportedAsAbsentInsideLocalTransaction()
    {
        await using var connection = await OpenConnectionAsync();
        await DropLedgerIdentityAsync(connection);
        await using var transaction = connection.BeginTransaction();
        var reader = new SqlServerMigrationLedgerMetadataReader();

        var state = await reader.ResolveObjectAsync(connection, transaction, CancellationToken.None);

        Assert.Equal(SqlMigrationLedgerObjectKind.Absent, state.Kind);
        Assert.Null(state.ObjectId);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ViewOccupyingLedgerIdentityIsReportedAsIncompatibleObject()
    {
        await using var connection = await OpenConnectionAsync();
        await DropLedgerIdentityAsync(connection);
        try
        {
            await ExecuteAsync(connection, "CREATE VIEW dbo.FactoryConnectMigrationHistory AS SELECT 1 AS MigrationId;");
            await using var transaction = connection.BeginTransaction();
            var reader = new SqlServerMigrationLedgerMetadataReader();

            var state = await reader.ResolveObjectAsync(connection, transaction, CancellationToken.None);

            Assert.Equal(SqlMigrationLedgerObjectKind.IncompatibleObject, state.Kind);
            Assert.Equal("V", state.CatalogObjectType);
            await transaction.RollbackAsync();
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
            await using var transaction = connection.BeginTransaction();
            var reader = new SqlServerMigrationLedgerMetadataReader();

            var state = await reader.ResolveObjectAsync(connection, transaction, CancellationToken.None);

            Assert.Equal(SqlMigrationLedgerObjectKind.IncompatibleObject, state.Kind);
            Assert.Equal("SN", state.CatalogObjectType);
            await transaction.RollbackAsync();
        }
        finally
        {
            await DropLedgerIdentityAsync(connection);
        }
    }

    [Fact]
    public async Task ExactLedgerTableMatchesFrozenInfrastructureContractInsideLocalTransaction()
    {
        await using var connection = await OpenConnectionAsync();
        await DropLedgerIdentityAsync(connection);
        try
        {
            await CreateLedgerAsync(connection, identityMigrationId: false, reorderedColumns: false);
            await using var transaction = connection.BeginTransaction();
            var reader = new SqlServerMigrationLedgerMetadataReader();
            var state = await reader.ResolveObjectAsync(connection, transaction, CancellationToken.None);

            Assert.Equal(SqlMigrationLedgerObjectKind.UserTable, state.Kind);
            Assert.Equal("U", state.CatalogObjectType);
            var snapshot = await reader.ReadSchemaAsync(
                connection,
                transaction,
                Assert.IsType<int>(state.ObjectId),
                CancellationToken.None);

            SqlMigrationLedgerSchemaValidator.Validate(snapshot);
            await transaction.RollbackAsync();
        }
        finally
        {
            await DropLedgerIdentityAsync(connection);
        }
    }

    [Fact]
    public async Task PhysicalColumnOrderDoesNotParticipateInLedgerCompatibility()
    {
        await using var connection = await OpenConnectionAsync();
        await DropLedgerIdentityAsync(connection);
        try
        {
            await CreateLedgerAsync(connection, identityMigrationId: false, reorderedColumns: true);
            await using var transaction = connection.BeginTransaction();
            var reader = new SqlServerMigrationLedgerMetadataReader();
            var state = await reader.ResolveObjectAsync(connection, transaction, CancellationToken.None);
            var snapshot = await reader.ReadSchemaAsync(
                connection,
                transaction,
                Assert.IsType<int>(state.ObjectId),
                CancellationToken.None);

            SqlMigrationLedgerSchemaValidator.Validate(snapshot);
            await transaction.RollbackAsync();
        }
        finally
        {
            await DropLedgerIdentityAsync(connection);
        }
    }

    [Fact]
    public async Task LedgerWithUnexpectedIdentityColumnIsRejected()
    {
        await AssertSchemaMutationRejectedAsync(
            createIdentityMigrationId: true,
            mutationSql: null);
    }

    [Theory]
    [InlineData("ALTER TABLE dbo.FactoryConnectMigrationHistory ALTER COLUMN Name nvarchar(128) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;")]
    [InlineData("ALTER TABLE dbo.FactoryConnectMigrationHistory ALTER COLUMN AppliedAtUtc datetimeoffset(6) NOT NULL;")]
    [InlineData("ALTER TABLE dbo.FactoryConnectMigrationHistory DROP CONSTRAINT PK_FactoryConnectMigrationHistory;")]
    [InlineData("ALTER TABLE dbo.FactoryConnectMigrationHistory DROP CONSTRAINT PK_FactoryConnectMigrationHistory; ALTER TABLE dbo.FactoryConnectMigrationHistory ADD CONSTRAINT PK_FactoryConnectMigrationHistory PRIMARY KEY NONCLUSTERED (MigrationId ASC);")]
    [InlineData("ALTER TABLE dbo.FactoryConnectMigrationHistory DROP CONSTRAINT PK_FactoryConnectMigrationHistory; ALTER TABLE dbo.FactoryConnectMigrationHistory ADD CONSTRAINT PK_FactoryConnectMigrationHistory PRIMARY KEY CLUSTERED (MigrationId DESC);")]
    [InlineData("ALTER INDEX PK_FactoryConnectMigrationHistory ON dbo.FactoryConnectMigrationHistory DISABLE;")]
    [InlineData("ALTER TABLE dbo.FactoryConnectMigrationHistory ADD CONSTRAINT DF_FactoryConnectMigrationHistory_MigrationId DEFAULT (0) FOR MigrationId;")]
    [InlineData("ALTER TABLE dbo.FactoryConnectMigrationHistory ADD CONSTRAINT CK_FactoryConnectMigrationHistory_MigrationId CHECK (MigrationId > 0);")]
    [InlineData("CREATE INDEX IX_FactoryConnectMigrationHistory_Name ON dbo.FactoryConnectMigrationHistory (Name);")]
    [InlineData("CREATE TRIGGER TR_FactoryConnectMigrationHistory_AfterInsert ON dbo.FactoryConnectMigrationHistory AFTER INSERT AS BEGIN SET NOCOUNT ON; END;")]
    public async Task RepresentativePhysicalLedgerDriftIsRejected(string mutationSql)
    {
        await AssertSchemaMutationRejectedAsync(
            createIdentityMigrationId: false,
            mutationSql);
    }

    private async Task AssertSchemaMutationRejectedAsync(
        bool createIdentityMigrationId,
        string? mutationSql)
    {
        await using var connection = await OpenConnectionAsync();
        await DropLedgerIdentityAsync(connection);
        try
        {
            await CreateLedgerAsync(connection, createIdentityMigrationId, reorderedColumns: false);
            if (mutationSql is not null)
            {
                await ExecuteAsync(connection, mutationSql);
            }

            await using var transaction = connection.BeginTransaction();
            var reader = new SqlServerMigrationLedgerMetadataReader();
            var state = await reader.ResolveObjectAsync(connection, transaction, CancellationToken.None);
            var snapshot = await reader.ReadSchemaAsync(
                connection,
                transaction,
                Assert.IsType<int>(state.ObjectId),
                CancellationToken.None);

            Assert.Throws<SqlMigrationLedgerSchemaException>(
                () => SqlMigrationLedgerSchemaValidator.Validate(snapshot));
            await transaction.RollbackAsync();
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

    private static Task CreateLedgerAsync(
        SqlConnection connection,
        bool identityMigrationId,
        bool reorderedColumns)
    {
        var migrationId = identityMigrationId
            ? "MigrationId int IDENTITY(1,1) NOT NULL"
            : "MigrationId int NOT NULL";
        var columns = reorderedColumns
            ? $"""
                Name nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                AppliedAtUtc datetimeoffset(7) NOT NULL,
                CanonicalChecksum char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                {migrationId}
                """
            : $"""
                {migrationId},
                Name nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CanonicalChecksum char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                AppliedAtUtc datetimeoffset(7) NOT NULL
                """;

        return ExecuteAsync(
            connection,
            $"""
            CREATE TABLE dbo.FactoryConnectMigrationHistory
            (
                {columns},
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
