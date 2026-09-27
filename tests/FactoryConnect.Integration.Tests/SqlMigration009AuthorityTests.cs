using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class SqlMigration009AuthorityTests
{
    private const string Migration009Checksum =
        "9351AE4BC31FC38F416537EA50498F2BC5AA38BD8AFF102702C60627EE32021E";

    [Fact]
    public void CatalogContainsFrozenMigration009IdentityAndChecksum()
    {
        var catalog = SqlMigrationCatalog.Load();
        var migration = Assert.Single(
            catalog.Migrations,
            static candidate => candidate.MigrationId == 9);

        Assert.Equal("CorrectCurrentStateDurableOutputIdentity", migration.Name);
        Assert.Equal(
            "FactoryConnect.Persistence.SqlServer.Sql.009_CorrectCurrentStateDurableOutputIdentity.sql",
            migration.ResourceName);
        Assert.Equal(SqlMigrationTransactionPolicy.EngineOwned, migration.TransactionPolicy);
        Assert.Equal(Migration009Checksum, migration.Sha256Checksum);
        Assert.Equal(Enumerable.Range(1, 12), catalog.Migrations.Select(static value => value.MigrationId));
    }

    [Fact]
    public void Post009ChangesOnlyDurableHistoryPayloadIdentity()
    {
        var post008 = SqlRepositorySchemaDescriptors.Post008;
        var post009 = SqlRepositorySchemaDescriptors.Post009;

        Assert.Equal(post008.Tables.Length, post009.Tables.Length);

        foreach (var post008Table in post008.Tables)
        {
            var post009Table = Assert.Single(
                post009.Tables,
                candidate => candidate.Name == post008Table.Name);

            if (post008Table.Name.ObjectName is not ("MachineStateChangeHistory" or "MachineActivityPeriodHistory"))
            {
                Assert.Equal(post008Table, post009Table);
                continue;
            }

            Assert.Equal(post008Table.PrimaryKey, post009Table.PrimaryKey);
            Assert.Equal(post008Table.UniqueConstraints, post009Table.UniqueConstraints);
            Assert.Equal(post008Table.ForeignKeys, post009Table.ForeignKeys);
            Assert.Equal(post008Table.Indexes, post009Table.Indexes);
            Assert.Equal(post008Table.Columns.Length + 2, post009Table.Columns.Length);
            Assert.Equal(post008Table.CheckConstraints.Length + 2, post009Table.CheckConstraints.Length);

            AssertDurableUInt64Column(post009Table, "InstanceId");
            AssertDurableUInt64Column(post009Table, "Sequence");
            AssertUInt64Check(post009Table, "InstanceId");
            AssertUInt64Check(post009Table, "Sequence");
        }
    }

    [Fact]
    public void Post008RemainsTheHistoricalPreCorrectionShape()
    {
        foreach (var tableName in new[] { "MachineStateChangeHistory", "MachineActivityPeriodHistory" })
        {
            var table = Assert.Single(
                SqlRepositorySchemaDescriptors.Post008.Tables,
                candidate => candidate.Name.ObjectName == tableName);

            Assert.DoesNotContain(table.Columns, static column => column.Name == "InstanceId");
            Assert.DoesNotContain(table.Columns, static column => column.Name == "Sequence");
        }
    }

    private static void AssertDurableUInt64Column(SqlTableDescriptor table, string name)
    {
        var column = Assert.Single(table.Columns, candidate => candidate.Name == name);
        Assert.Equal("decimal", column.SqlType);
        Assert.Equal((byte)20, column.Precision);
        Assert.Equal((byte)0, column.Scale);
        Assert.False(column.IsNullable);
    }

    private static void AssertUInt64Check(SqlTableDescriptor table, string columnName)
    {
        var check = Assert.Single(
            table.CheckConstraints,
            candidate => candidate.Name == $"CK_{table.Name.ObjectName}_{columnName}_UInt64");

        Assert.Equal(
            $"([{columnName}]>=(0) AND [{columnName}]<=(18446744073709551615.))",
            check.CanonicalDefinition);
        Assert.True(check.IsEnabled);
        Assert.True(check.IsTrusted);
    }
}
