using FactoryConnect.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Integration.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class SqlServerMigration005SchemaAuthorityIntegrationTests
{
    [Fact]
    public void RepositoryAuthoritySeparatesLegacyPost004FromCurrentPost005()
    {
        Assert.Equal(13, SqlRepositorySchemaDescriptors.LegacyPost004.Tables.Length);
        Assert.Equal(20, SqlRepositorySchemaDescriptors.Current.Tables.Length);

        var legacyNames = SqlRepositorySchemaDescriptors.LegacyPost004.Tables
            .Select(static table => table.Name.ObjectName)
            .ToHashSet(StringComparer.Ordinal);
        var currentNames = SqlRepositorySchemaDescriptors.Current.Tables
            .Select(static table => table.Name.ObjectName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("OperationalMetricProjection", legacyNames);
        Assert.Contains("OperationalMetricProjection", currentNames);
        Assert.Contains("OperationalMetricProjectionProcessor", currentNames);
        Assert.Contains("OperationalMetricProjectionCheckpoint", currentNames);
        Assert.Contains("OperationalMetricProjectionManifest", currentNames);
        Assert.Contains("OperationalMetricProjectionEvidence", currentNames);
        Assert.Contains("MachineShiftOccurrenceRoster", currentNames);
        Assert.Contains("MachineShiftOccurrenceRosterOccurrence", currentNames);
    }

    [Fact]
    public void MigrationCatalogIncludesExactEngineOwned005Identity()
    {
        var migration = Assert.Single(
            SqlMigrationCatalog.Load().Migrations,
            static item => item.MigrationId == 5);

        Assert.Equal("OperationalMetricReportingPersistence", migration.Name);
        Assert.Equal(
            "FactoryConnect.Persistence.SqlServer.Sql.005_OperationalMetricReportingPersistence.sql",
            migration.ResourceName);
        Assert.Equal(SqlMigrationTransactionPolicy.EngineOwned, migration.TransactionPolicy);
    }

    [Fact]
    public async Task FreshMigration005DatabaseExactlyMatchesCurrentDescriptor()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        await ApplyCatalogWithoutFinalValidationAsync(connection);

        var actual = await new SqlServerSchemaMetadataReader()
            .ReadFactoryConnectOwnedSchemaAsync(connection, CancellationToken.None);
        var comparison = SqlSchemaComparator.Compare(SqlRepositorySchemaDescriptors.Current, actual);

        Assert.True(
            comparison.IsExactMatch,
            string.Join(
                Environment.NewLine,
                comparison.Differences.Select(static difference =>
                    $"{difference.Kind}: {difference.Table.SchemaName}.{difference.Table.ObjectName}.{difference.ArtifactName} — {difference.Detail}")));
    }

    [Fact]
    public async Task FreshMigration005ProjectsFrozenMachineOrderKeyComputedAuthority()
    {
        await using var database = await SqlStartupIsolatedDatabase.CreateAsync();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        await ApplyCatalogWithoutFinalValidationAsync(connection);

        var actual = await new SqlServerSchemaMetadataReader()
            .ReadFactoryConnectOwnedSchemaAsync(connection, CancellationToken.None);
        var projection = actual.Tables.Single(
            static table => table.Name.ObjectName == "OperationalMetricProjection");
        var machineOrderKey = projection.Columns.Single(
            static column => column.Name == "MachineOrderKey");
        var computed = Assert.IsType<SqlComputedDescriptor>(machineOrderKey.Computed);

        Assert.Equal("binary", machineOrderKey.SqlType);
        Assert.Equal(16, Assert.IsType<SqlLengthDescriptor>(machineOrderKey.MaxLength).Value);
        Assert.False(machineOrderKey.IsNullable);
        Assert.True(computed.IsPersisted);
        Assert.False(string.IsNullOrWhiteSpace(computed.Definition));
    }

    private static async Task ApplyCatalogWithoutFinalValidationAsync(SqlConnection connection)
    {
        var catalog = SqlMigrationCatalog.Load();
        await using var transaction = connection.BeginTransaction();
        foreach (var migration in catalog.Migrations)
        {
            await SqlServerMigrationExecutor.ExecuteAsync(
                connection,
                transaction,
                migration,
                CancellationToken.None);
        }

        await transaction.CommitAsync();
    }
}
