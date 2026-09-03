using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerUnledgeredDatabaseClassificationIntegrationTests
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    [Fact]
    public async Task DatabaseWithNoFactoryConnectTablesIsUninitialized()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await DropFactoryConnectOwnedTablesAsync(connection);

        await AssertClassificationAsync(
            connection,
            UnledgeredDatabaseClassification.Uninitialized);
    }

    [Fact]
    public async Task DatabaseWithOnlyUnrelatedTablesIsUninitialized()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await DropFactoryConnectOwnedTablesAsync(connection);
        await ExecuteAsync(
            connection,
            "CREATE TABLE dbo.CustomerAdminState (Id int NOT NULL);");

        await AssertClassificationAsync(
            connection,
            UnledgeredDatabaseClassification.Uninitialized);
    }

    [Fact]
    public async Task RealMigratedPost004DatabaseIsLegacyAdoptable()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();

        await AssertLegacyAdoptableAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithUnrelatedTableRemainsLegacyAdoptable()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            "CREATE TABLE dbo.CustomerAdminState (Id int NOT NULL);");

        await AssertLegacyAdoptableAsync(connection);
    }

    [Fact]
    public async Task DatabaseWithOneRecognizedOwnedTableIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await DropFactoryConnectOwnedTablesAsync(connection);
        await ExecuteAsync(
            connection,
            "CREATE TABLE dbo.ObservationStreamCheckpoint (Id int NOT NULL);");

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task CasingOnlyCatalogIdentityUsesSqlServerResolutionSemantics()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await DropFactoryConnectOwnedTablesAsync(connection);
        await ExecuteAsync(
            connection,
            "CREATE TABLE dbo.machineobservation (Id int NOT NULL);");

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithRequiredColumnNullabilityChangedIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            "ALTER TABLE dbo.MachineObservation ALTER COLUMN [Address] nvarchar(512) NULL;");

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithRequiredColumnTypeChangedIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            $"ALTER TABLE dbo.MachineObservation ALTER COLUMN [Address] varchar(512) COLLATE {BinaryCollation} NOT NULL;");

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithExplicitCollationDriftIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            "ALTER TABLE dbo.MachineObservation ALTER COLUMN [Address] nvarchar(512) COLLATE Latin1_General_100_CI_AS NOT NULL;");

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithIdentityRemovedIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            """
            DROP TABLE dbo.ContextualizedActivityOutput;
            CREATE TABLE dbo.ContextualizedActivityOutput
            (
                ContextualizedActivityOutputRowId bigint NOT NULL,
                IdentityHash binary(32) NOT NULL,
                IdentityBinary varbinary(512) NOT NULL,
                IdentityText nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
                PayloadHash binary(32) NOT NULL,
                Payload nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CONSTRAINT PK_ContextualizedActivityOutput
                    PRIMARY KEY CLUSTERED (ContextualizedActivityOutputRowId),
                CONSTRAINT UQ_ContextualizedActivityOutput_IdentityHash
                    UNIQUE NONCLUSTERED (IdentityHash)
            );
            """);

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithOnlyPhysicalColumnOrderChangedRemainsLegacyAdoptable()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            """
            DROP TABLE dbo.ProductionTimeEligibilityOutput;
            CREATE TABLE dbo.ProductionTimeEligibilityOutput
            (
                IdentityHash binary(32) NOT NULL,
                ProductionTimeEligibilityOutputRowId bigint IDENTITY(1,1) NOT NULL,
                IdentityText nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
                IdentityBinary varbinary(512) NOT NULL,
                Payload nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                PayloadHash binary(32) NOT NULL,
                CONSTRAINT PK_ProductionTimeEligibilityOutput
                    PRIMARY KEY CLUSTERED (ProductionTimeEligibilityOutputRowId),
                CONSTRAINT UQ_ProductionTimeEligibilityOutput_IdentityHash
                    UNIQUE NONCLUSTERED (IdentityHash)
            );
            """);

        await AssertLegacyAdoptableAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithRequiredForeignKeyDisabledIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            "ALTER TABLE dbo.MetricInputFact NOCHECK CONSTRAINT FK_MetricInputFact_StreamMachine;");

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithRequiredForeignKeyUntrustedIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            """
            ALTER TABLE dbo.MetricInputFact NOCHECK CONSTRAINT FK_MetricInputFact_StreamMachine;
            ALTER TABLE dbo.MetricInputFact CHECK CONSTRAINT FK_MetricInputFact_StreamMachine;
            """);

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithForeignKeyActionChangedIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            """
            ALTER TABLE dbo.MetricInputFact
                DROP CONSTRAINT FK_MetricInputFact_StreamMachine;
            ALTER TABLE dbo.MetricInputFact WITH CHECK
                ADD CONSTRAINT FK_MetricInputFact_StreamMachine
                FOREIGN KEY (MetricInputStreamRowId, MachineId)
                REFERENCES dbo.MetricInputStream (MetricInputStreamRowId, MachineId)
                ON DELETE CASCADE;
            ALTER TABLE dbo.MetricInputFact
                CHECK CONSTRAINT FK_MetricInputFact_StreamMachine;
            """);

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithForeignKeyNotForReplicationChangedIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            """
            ALTER TABLE dbo.MetricInputFact
                DROP CONSTRAINT FK_MetricInputFact_StreamMachine;
            ALTER TABLE dbo.MetricInputFact WITH CHECK
                ADD CONSTRAINT FK_MetricInputFact_StreamMachine
                FOREIGN KEY (MetricInputStreamRowId, MachineId)
                REFERENCES dbo.MetricInputStream (MetricInputStreamRowId, MachineId)
                NOT FOR REPLICATION;
            ALTER TABLE dbo.MetricInputFact
                CHECK CONSTRAINT FK_MetricInputFact_StreamMachine;
            """);

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithRequiredCheckDisabledIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            "ALTER TABLE dbo.MetricInputFact NOCHECK CONSTRAINT CK_MetricInputFact_Interval;");

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithRequiredCheckUntrustedIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            """
            ALTER TABLE dbo.MetricInputFact NOCHECK CONSTRAINT CK_MetricInputFact_Interval;
            ALTER TABLE dbo.MetricInputFact CHECK CONSTRAINT CK_MetricInputFact_Interval;
            """);

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithCheckDefinitionChangedIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            """
            ALTER TABLE dbo.MetricInputFact
                DROP CONSTRAINT CK_MetricInputFact_Interval;
            ALTER TABLE dbo.MetricInputFact WITH CHECK
                ADD CONSTRAINT CK_MetricInputFact_Interval
                CHECK ([EndsAtUtc] > [StartsAtUtc]);
            """);

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithCheckNotForReplicationChangedIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            """
            ALTER TABLE dbo.MetricInputFact
                DROP CONSTRAINT CK_MetricInputFact_Interval;
            ALTER TABLE dbo.MetricInputFact WITH CHECK
                ADD CONSTRAINT CK_MetricInputFact_Interval
                CHECK NOT FOR REPLICATION ([EndsAtUtc] >= [StartsAtUtc]);
            """);

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithPrimaryKeyBackingIndexDisabledIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            "ALTER INDEX PK_ContextualizedActivityOutput ON dbo.ContextualizedActivityOutput DISABLE;");

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithUniqueConstraintBackingIndexDisabledIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            "ALTER INDEX UQ_ContextualizedActivityOutput_IdentityHash ON dbo.ContextualizedActivityOutput DISABLE;");

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithPrimaryAndUniqueKeyStructureChangedIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            """
            ALTER TABLE dbo.ContextualizedActivityOutput
                DROP CONSTRAINT UQ_ContextualizedActivityOutput_IdentityHash;
            ALTER TABLE dbo.ContextualizedActivityOutput
                DROP CONSTRAINT PK_ContextualizedActivityOutput;
            ALTER TABLE dbo.ContextualizedActivityOutput
                ADD CONSTRAINT PK_ContextualizedActivityOutput
                PRIMARY KEY CLUSTERED (IdentityHash);
            ALTER TABLE dbo.ContextualizedActivityOutput
                ADD CONSTRAINT UQ_ContextualizedActivityOutput_IdentityHash
                UNIQUE NONCLUSTERED (ContextualizedActivityOutputRowId);
            """);

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithRequiredIndexDisabledIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            "ALTER INDEX IX_MetricInputFact_OrderedRead ON dbo.MetricInputFact DISABLE;");

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithRequiredIndexRemovedIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            "DROP INDEX IX_MetricInputFact_OrderedRead ON dbo.MetricInputFact;");

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithUnexpectedOrdinaryIndexIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            "CREATE INDEX IX_MetricInputFact_Unexpected ON dbo.MetricInputFact (FactId);");

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    [Fact]
    public async Task Post004DatabaseWithOrdinaryIndexStructureChangedIsPartialOrIncompatibleLegacy()
    {
        await using var database = await IsolatedPost004Database.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();
        await AssertLegacyAdoptableAsync(connection);

        await ExecuteAsync(
            connection,
            """
            DROP INDEX IX_MetricInputFact_OrderedRead ON dbo.MetricInputFact;
            CREATE NONCLUSTERED INDEX IX_MetricInputFact_OrderedRead
                ON dbo.MetricInputFact ([Position] DESC, MetricInputStreamRowId ASC)
                INCLUDE (MetricInputFactRowId, FactId, MetricInputKey, MetricValue, Unit);
            """);

        await AssertPartialOrIncompatibleLegacyAsync(connection);
    }

    private static async Task AssertLegacyAdoptableAsync(SqlConnection connection) =>
        await AssertClassificationAsync(
            connection,
            UnledgeredDatabaseClassification.LegacyAdoptable);

    private static async Task AssertPartialOrIncompatibleLegacyAsync(SqlConnection connection) =>
        await AssertClassificationAsync(
            connection,
            UnledgeredDatabaseClassification.PartialOrIncompatibleLegacy);

    private static async Task AssertClassificationAsync(
        SqlConnection connection,
        UnledgeredDatabaseClassification expected)
    {
        var actual = await ReadAndClassifyAsync(connection);
        Assert.Equal(expected, actual);
    }

    private static async Task<UnledgeredDatabaseClassification> ReadAndClassifyAsync(
        SqlConnection connection)
    {
        var snapshot = await new SqlServerSchemaMetadataReader()
            .ReadFactoryConnectOwnedSchemaAsync(connection, CancellationToken.None);

        return SqlUnledgeredDatabaseClassifier.Classify(snapshot);
    }

    private static async Task DropFactoryConnectOwnedTablesAsync(SqlConnection connection)
    {
        await ExecuteAsync(
            connection,
            """
            DECLARE @DropForeignKeys nvarchar(max) = N'';

            SELECT @DropForeignKeys = @DropForeignKeys
                + N'ALTER TABLE '
                + QUOTENAME(SCHEMA_NAME(parent.schema_id))
                + N'.'
                + QUOTENAME(parent.name)
                + N' DROP CONSTRAINT '
                + QUOTENAME(foreignKey.name)
                + N';'
            FROM sys.foreign_keys AS foreignKey
            INNER JOIN sys.tables AS parent
                ON parent.object_id = foreignKey.parent_object_id;

            EXEC sys.sp_executesql @DropForeignKeys;
            """);

        foreach (var table in SqlRepositorySchemaAuthority.OwnedObjects.OwnedTables)
        {
            await ExecuteAsync(
                connection,
                $"DROP TABLE IF EXISTS {QuoteIdentifier(table.SchemaName)}.{QuoteIdentifier(table.ObjectName)};");
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string value) =>
        $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    private sealed class IsolatedPost004Database : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;

        private IsolatedPost004Database(
            string adminConnectionString,
            string databaseName,
            string connectionString)
        {
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            ConnectionString = connectionString;
        }

        private string ConnectionString { get; }

        public static async Task<IsolatedPost004Database> CreateAsync()
        {
            var sourceConnectionString = Environment.GetEnvironmentVariable(
                SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(sourceConnectionString))
            {
                throw new InvalidOperationException(
                    $"{SqlServerTestDatabaseFixture.ConnectionStringEnvironmentVariable} is required for SQL Server integration tests.");
            }

            var sourceBuilder = new SqlConnectionStringBuilder(sourceConnectionString);
            var databaseName = $"FactoryConnect_FC030_B6_{Guid.NewGuid():N}";
            var adminBuilder = new SqlConnectionStringBuilder(sourceBuilder.ConnectionString)
            {
                InitialCatalog = "master",
            };
            var databaseBuilder = new SqlConnectionStringBuilder(sourceBuilder.ConnectionString)
            {
                InitialCatalog = databaseName,
            };

            var database = new IsolatedPost004Database(
                adminBuilder.ConnectionString,
                databaseName,
                databaseBuilder.ConnectionString);

            try
            {
                await database.CreateDatabaseAsync();
                await using var connection = await database.OpenConnectionAsync();
                await ExecuteAsync(connection, SqlServerSchema.ReadInitialSchema());
                await ExecuteAsync(connection, SqlServerSchema.ReadMetricAggregationSchema());
                await ExecuteAsync(connection, SqlServerSchema.ReadMetricInputMachineBindingSchema());
                await ExecuteAsync(connection, SqlServerSchema.ReadProductionContextHandoffSchema());
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async Task<SqlConnection> OpenConnectionAsync()
        {
            var connection = new SqlConnection(ConnectionString);
            try
            {
                await connection.OpenAsync();
                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new SqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            var escapedName = QuoteIdentifier(_databaseName);
            command.CommandText =
                $"IF DB_ID(N'{EscapeLiteral(_databaseName)}') IS NOT NULL " +
                "BEGIN " +
                $"ALTER DATABASE {escapedName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                $"DROP DATABASE {escapedName}; " +
                "END";
            await command.ExecuteNonQueryAsync();
        }

        private async Task CreateDatabaseAsync()
        {
            await using var connection = new SqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE {QuoteIdentifier(_databaseName)};";
            await command.ExecuteNonQueryAsync();
        }

        private static string EscapeLiteral(string value) =>
            value.Replace("'", "''", StringComparison.Ordinal);
    }
}
