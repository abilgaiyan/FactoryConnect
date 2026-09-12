using System.Collections.Immutable;
using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlMigration006AuthorityTests
{
    private const string Migration006Checksum =
        "DDFD8C6CC1FADE9D7486EB5D3D915881A2538E2724A6B57EDA7E6D7C16EB6EEE";

    [Fact]
    public void CatalogContainsFrozenMigration006IdentityAndChecksum()
    {
        var catalog = SqlMigrationCatalog.Load();
        var migration = Assert.Single(
            catalog.Migrations,
            static candidate => candidate.MigrationId == 6);

        Assert.Equal("CorrectOperationalMetricProjectionManifestParent", migration.Name);
        Assert.Equal(
            "FactoryConnect.Persistence.SqlServer.Sql.006_CorrectOperationalMetricProjectionManifestParent.sql",
            migration.ResourceName);
        Assert.Equal(SqlMigrationTransactionPolicy.EngineOwned, migration.TransactionPolicy);
        Assert.Equal(Migration006Checksum, migration.Sha256Checksum);
    }

    [Fact]
    public void Post006DescriptorChangesOnlyManifestParentForeignKey()
    {
        var post005 = SqlRepositoryPost005SchemaDescriptor.Create(
            SqlRepositorySchemaDescriptors.LegacyPost004);
        var post006 = SqlRepositoryPost006SchemaDescriptor.Create(post005);

        Assert.Equal(post005.Tables.Length, post006.Tables.Length);

        foreach (var post005Table in post005.Tables)
        {
            var post006Table = Assert.Single(
                post006.Tables,
                candidate => candidate.Name == post005Table.Name);

            if (!string.Equals(
                    post005Table.Name.ObjectName,
                    "OperationalMetricProjectionManifest",
                    StringComparison.Ordinal))
            {
                Assert.Equal(post005Table, post006Table);
                continue;
            }

            Assert.Equal(post005Table.Columns, post006Table.Columns);
            Assert.Equal(post005Table.PrimaryKey, post006Table.PrimaryKey);
            Assert.Equal(post005Table.UniqueConstraints, post006Table.UniqueConstraints);
            Assert.Equal(post005Table.CheckConstraints, post006Table.CheckConstraints);
            Assert.Equal(post005Table.Indexes, post006Table.Indexes);

            var oldForeignKey = Assert.Single(
                post005Table.ForeignKeys,
                static foreignKey => string.Equals(
                    foreignKey.Name,
                    "FK_OperationalMetricProjectionManifest_Checkpoint",
                    StringComparison.Ordinal));
            Assert.Equal(
                new SqlObjectName("dbo", "OperationalMetricProjectionCheckpoint"),
                oldForeignKey.ReferencedTable);

            Assert.DoesNotContain(
                post006Table.ForeignKeys,
                static foreignKey => string.Equals(
                    foreignKey.Name,
                    "FK_OperationalMetricProjectionManifest_Checkpoint",
                    StringComparison.Ordinal));

            var newForeignKey = Assert.Single(
                post006Table.ForeignKeys,
                static foreignKey => string.Equals(
                    foreignKey.Name,
                    "FK_OperationalMetricProjectionManifest_Processor",
                    StringComparison.Ordinal));
            Assert.Equal(
                new SqlObjectName("dbo", "OperationalMetricProjectionProcessor"),
                newForeignKey.ReferencedTable);
            Assert.Equal(
                "OperationalMetricProjectionProcessorRowId",
                Assert.Single(newForeignKey.Columns));
            Assert.Equal(
                "OperationalMetricProjectionProcessorRowId",
                Assert.Single(newForeignKey.ReferencedColumns));
            Assert.True(newForeignKey.IsEnabled);
            Assert.True(newForeignKey.IsTrusted);
            Assert.False(newForeignKey.IsNotForReplication);

            var projectionForeignKey = Assert.Single(
                post006Table.ForeignKeys,
                static foreignKey => string.Equals(
                    foreignKey.Name,
                    "FK_OperationalMetricProjectionManifest_Projection",
                    StringComparison.Ordinal));
            Assert.Contains(
                post005Table.ForeignKeys,
                candidate => candidate == projectionForeignKey);
        }
    }

    [Fact]
    public void ExactPost005AndPost006HistoryRemainPendingAfterMigration007()
    {
        var catalog = SqlMigrationCatalog.Load();
        var appliedAtUtc = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

        var post005History = catalog.Migrations
            .Take(5)
            .Select(migration => new SqlMigrationHistoryRow(
                migration.MigrationId,
                migration.Name,
                migration.Sha256Checksum,
                appliedAtUtc))
            .ToImmutableArray();

        var post006History = catalog.Migrations
            .Take(6)
            .Select(migration => new SqlMigrationHistoryRow(
                migration.MigrationId,
                migration.Name,
                migration.Sha256Checksum,
                appliedAtUtc))
            .ToImmutableArray();

        Assert.Equal(
            SqlRuntimeMigrationHistoryClassification.ExactPrefixPending,
            SqlRuntimeMigrationHistoryClassifier.Classify(post005History, catalog));
        Assert.Equal(
            SqlRuntimeMigrationHistoryClassification.ExactPrefixPending,
            SqlRuntimeMigrationHistoryClassifier.Classify(post006History, catalog));
    }
}
