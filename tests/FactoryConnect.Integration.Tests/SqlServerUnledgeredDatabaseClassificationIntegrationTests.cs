using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerUnledgeredDatabaseClassificationIntegrationTests :
    IClassFixture<SqlServerTestDatabaseFixture>
{
    private readonly SqlServerTestDatabaseFixture _fixture;

    public SqlServerUnledgeredDatabaseClassificationIntegrationTests(
        SqlServerTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DatabaseWithNoFactoryConnectTablesIsUninitialized()
    {
        await using var connection = await OpenResetConnectionAsync();
        await DropFactoryConnectOwnedTablesAsync(connection);

        var classification = await ReadAndClassifyAsync(connection);

        Assert.Equal(UnledgeredDatabaseClassification.Uninitialized, classification);
    }

    [Fact]
    public async Task DatabaseWithOnlyUnrelatedTablesIsUninitialized()
    {
        await using var connection = await OpenResetConnectionAsync();
        await DropFactoryConnectOwnedTablesAsync(connection);
        await ExecuteAsync(
            connection,
            "CREATE TABLE dbo.CustomerAdminState (Id int NOT NULL);");

        var classification = await ReadAndClassifyAsync(connection);

        Assert.Equal(UnledgeredDatabaseClassification.Uninitialized, classification);
    }

    [Fact]
    public async Task RealMigratedPost004DatabaseIsLegacyAdoptable()
    {
        await using var connection = await OpenResetConnectionAsync();

        var classification = await ReadAndClassifyAsync(connection);

        Assert.Equal(UnledgeredDatabaseClassification.LegacyAdoptable, classification);
    }

    [Fact]
    public async Task DatabaseWithOneRecognizedOwnedTableIsPartialOrIncompatibleLegacy()
    {
        await using var connection = await OpenResetConnectionAsync();
        await DropFactoryConnectOwnedTablesAsync(connection);
        await ExecuteAsync(
            connection,
            "CREATE TABLE dbo.ObservationStreamCheckpoint (Id int NOT NULL);");

        var classification = await ReadAndClassifyAsync(connection);

        Assert.Equal(
            UnledgeredDatabaseClassification.PartialOrIncompatibleLegacy,
            classification);
    }

    [Fact]
    public async Task Post004DatabaseWithRequiredColumnChangedIsPartialOrIncompatibleLegacy()
    {
        await using var connection = await OpenResetConnectionAsync();
        await ExecuteAsync(
            connection,
            "ALTER TABLE dbo.MachineObservation ALTER COLUMN [Address] nvarchar(512) NULL;");

        var classification = await ReadAndClassifyAsync(connection);

        Assert.Equal(
            UnledgeredDatabaseClassification.PartialOrIncompatibleLegacy,
            classification);
    }

    [Fact]
    public async Task Post004DatabaseWithRequiredForeignKeyDisabledIsPartialOrIncompatibleLegacy()
    {
        await using var connection = await OpenResetConnectionAsync();
        await ExecuteAsync(
            connection,
            "ALTER TABLE dbo.MetricInputFact NOCHECK CONSTRAINT FK_MetricInputFact_StreamMachine;");

        var classification = await ReadAndClassifyAsync(connection);

        Assert.Equal(
            UnledgeredDatabaseClassification.PartialOrIncompatibleLegacy,
            classification);
    }

    [Fact]
    public async Task Post004DatabaseWithRequiredCheckDisabledIsPartialOrIncompatibleLegacy()
    {
        await using var connection = await OpenResetConnectionAsync();
        await ExecuteAsync(
            connection,
            "ALTER TABLE dbo.MetricInputFact NOCHECK CONSTRAINT CK_MetricInputFact_Interval;");

        var classification = await ReadAndClassifyAsync(connection);

        Assert.Equal(
            UnledgeredDatabaseClassification.PartialOrIncompatibleLegacy,
            classification);
    }

    [Fact]
    public async Task Post004DatabaseWithRequiredIndexDisabledIsPartialOrIncompatibleLegacy()
    {
        await using var connection = await OpenResetConnectionAsync();
        await ExecuteAsync(
            connection,
            "ALTER INDEX IX_MetricInputFact_OrderedRead ON dbo.MetricInputFact DISABLE;");

        var classification = await ReadAndClassifyAsync(connection);

        Assert.Equal(
            UnledgeredDatabaseClassification.PartialOrIncompatibleLegacy,
            classification);
    }

    private async Task<SqlConnection> OpenResetConnectionAsync()
    {
        var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await ResetPost004SchemaAsync(connection);
        return connection;
    }

    private static async Task ResetPost004SchemaAsync(SqlConnection connection)
    {
        await DropFactoryConnectOwnedTablesAsync(connection);
        await ExecuteAsync(connection, SqlServerSchema.ReadInitialSchema());
        await ExecuteAsync(connection, SqlServerSchema.ReadMetricAggregationSchema());
        await ExecuteAsync(connection, SqlServerSchema.ReadMetricInputMachineBindingSchema());
        await ExecuteAsync(connection, SqlServerSchema.ReadProductionContextHandoffSchema());
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

    private static async Task<UnledgeredDatabaseClassification> ReadAndClassifyAsync(
        SqlConnection connection)
    {
        var snapshot = await new SqlServerSchemaMetadataReader()
            .ReadFactoryConnectOwnedSchemaAsync(connection, CancellationToken.None);

        return SqlUnledgeredDatabaseClassifier.Classify(snapshot);
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string value) =>
        $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
}
