using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlMigration022AuthorityTests
{
    private static readonly string[] AdmissionColumns = ["AdmissionBound", "ProductionStartsAt"];
    [Fact]
    public void SharedUpgradeAndConcurrencyHistoryExpectationMatchesEmbeddedCatalog()
    {
        Assert.Equal(SqlMigrationCatalog.Load().Migrations.Select(migration => migration.MigrationId),
            SqlMigrationTestExpectations.CurrentMigrationIds);
    }

    [Fact]
    public void AdmissionSchemaOnlyExtendsProductionContextProcessor()
    {
        var previous = SqlRepositorySchemaDescriptors.Post021;
        var current = SqlRepositorySchemaDescriptors.Post022;
        Assert.Equal(previous.Tables.Length, current.Tables.Length);
        foreach (var table in previous.Tables)
        {
            var updated = Assert.Single(current.Tables, value => value.Name == table.Name);
            if (table.Name.ObjectName != "ProductionContextProcessor")
            {
                Assert.Equal(table, updated);
                continue;
            }
            Assert.Equal(table.Columns, updated.Columns.Take(table.Columns.Length));
            Assert.Equal(AdmissionColumns,
                updated.Columns.Skip(table.Columns.Length).Select(column => column.Name));
            Assert.Equal(table.PrimaryKey, updated.PrimaryKey);
            Assert.Equal(table.ForeignKeys, updated.ForeignKeys);
        }
        var migration = SqlMigrationCatalog.Load().Migrations.Single(migration => migration.MigrationId == 22);
        Assert.Equal(22, migration.MigrationId);
        Assert.Equal(SqlMigrationTransactionPolicy.EngineOwned, migration.TransactionPolicy);
        Assert.Single(SqlMigrationExecutionBatches.For(migration));
        Assert.DoesNotContain("DELETE", migration.CanonicalSql, StringComparison.OrdinalIgnoreCase);
    }
}
