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
            DescribeDifferences(
                SqlRepositorySchemaDescriptors.Current,
                actual,
                comparison));
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

    private static string DescribeDifferences(
        SqlSchemaDescriptor expected,
        SqlSchemaDescriptor actual,
        SqlSchemaComparisonResult comparison)
    {
        var expectedTables = expected.Tables.ToDictionary(static table => table.Name);
        var actualTables = actual.Tables.ToDictionary(static table => table.Name);
        var lines = new List<string>();

        foreach (var difference in comparison.Differences)
        {
            lines.Add(
                $"{difference.Kind}: {difference.Table.SchemaName}.{difference.Table.ObjectName}.{difference.ArtifactName} — {difference.Detail}");

            if (!expectedTables.TryGetValue(difference.Table, out var expectedTable) ||
                !actualTables.TryGetValue(difference.Table, out var actualTable))
            {
                continue;
            }

            switch (difference.Kind)
            {
                case SqlSchemaDifferenceKind.CheckConstraintMismatch:
                {
                    var expectedCheck = expectedTable.CheckConstraints.Single(
                        item => string.Equals(item.Name, difference.ArtifactName, StringComparison.Ordinal));
                    var actualCheck = actualTable.CheckConstraints.Single(
                        item => string.Equals(item.Name, difference.ArtifactName, StringComparison.Ordinal));
                    lines.Add($"  expected: {SqlFragmentCanonicalizer.Canonicalize(expectedCheck.CanonicalDefinition)}");
                    lines.Add($"  actual:   {SqlFragmentCanonicalizer.Canonicalize(actualCheck.CanonicalDefinition)}");
                    break;
                }

                case SqlSchemaDifferenceKind.IndexMismatch:
                {
                    var expectedIndex = expectedTable.Indexes.Single(
                        item => string.Equals(item.Name, difference.ArtifactName, StringComparison.Ordinal));
                    var actualIndex = actualTable.Indexes.Single(
                        item => string.Equals(item.Name, difference.ArtifactName, StringComparison.Ordinal));
                    lines.Add($"  expected filter: {CanonicalizeNullable(expectedIndex.IndexStructure.CanonicalFilterDefinition)}");
                    lines.Add($"  actual filter:   {CanonicalizeNullable(actualIndex.IndexStructure.CanonicalFilterDefinition)}");
                    break;
                }

                case SqlSchemaDifferenceKind.ColumnComputedMismatch:
                {
                    var expectedColumn = expectedTable.Columns.Single(
                        item => string.Equals(item.Name, difference.ArtifactName, StringComparison.Ordinal));
                    var actualColumn = actualTable.Columns.Single(
                        item => string.Equals(item.Name, difference.ArtifactName, StringComparison.Ordinal));
                    lines.Add($"  expected computed: {CanonicalizeNullable(expectedColumn.Computed?.Definition)} | persisted={expectedColumn.Computed?.IsPersisted}");
                    lines.Add($"  actual computed:   {CanonicalizeNullable(actualColumn.Computed?.Definition)} | persisted={actualColumn.Computed?.IsPersisted}");
                    break;
                }
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string CanonicalizeNullable(string? value) =>
        value is null ? "<null>" : SqlFragmentCanonicalizer.Canonicalize(value);

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
