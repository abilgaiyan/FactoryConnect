using System.Collections.Immutable;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlMigration007AuthorityTests
{
    private const string Migration007Checksum =
        "17A98E7606F7C1A89832492A81553DF7350E9A5E79EF1FFCC571B5430CD210C6";

    [Fact]
    public void CatalogContainsFrozenMigration007IdentityAndChecksum()
    {
        var catalog = SqlMigrationCatalog.Load();
        var migration = Assert.Single(
            catalog.Migrations,
            static candidate => candidate.MigrationId == 7);

        Assert.Equal("AcquisitionContactAuthority", migration.Name);
        Assert.Equal(
            "FactoryConnect.Persistence.SqlServer.Sql.007_AcquisitionContactAuthority.sql",
            migration.ResourceName);
        Assert.Equal(SqlMigrationTransactionPolicy.EngineOwned, migration.TransactionPolicy);
        Assert.Equal(Migration007Checksum, migration.Sha256Checksum);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], catalog.Migrations.Select(static value => value.MigrationId));
    }

    [Fact]
    public void ExactPost007HistoryIsCurrent()
    {
        var catalog = SqlMigrationCatalog.Load();
        var appliedAtUtc = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
        var history = catalog.Migrations
            .Select(migration => new SqlMigrationHistoryRow(
                migration.MigrationId,
                migration.Name,
                migration.Sha256Checksum,
                appliedAtUtc))
            .ToImmutableArray();

        Assert.Equal(
            SqlRuntimeMigrationHistoryClassification.ExactCurrent,
            SqlRuntimeMigrationHistoryClassifier.Classify(history, catalog));
    }

    [Fact]
    public void CurrentDescriptorContainsPost007AcquisitionAuthorityShape()
    {
        var machineObservation = Assert.Single(
            SqlRepositorySchemaDescriptors.Current.Tables,
            static table => table.Name.ObjectName == "MachineObservation");
        Assert.Contains(
            machineObservation.Columns,
            static column => column.Name == "Position" &&
                             column.SqlType == "decimal" &&
                             column.Precision == 20 &&
                             column.Scale == 0 &&
                             !column.IsNullable);

        var authority = Assert.Single(
            SqlRepositorySchemaDescriptors.Current.Tables,
            static table => table.Name.ObjectName == "AcquisitionContactAuthority");
        Assert.Contains(
            authority.Columns,
            static column => column.Name == "SuccessfulContactTime");
        Assert.Contains(
            authority.Columns,
            static column => column.Name == "RawAcceptedThrough" && column.IsNullable);
        Assert.Contains(
            authority.Columns,
            static column => column.Name == "AcquisitionRevision");
    }
}
