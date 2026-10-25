using FactoryConnect.Persistence.SqlServer;

namespace FactoryConnect.Integration.Tests;

public sealed class AssumedBreakAuthorityMigrationTests
{
    private static readonly string[] ExpectedTables = ["AssumedBreakConfigurationSnapshot", "AssumedBreakConfigurationAdmission"];
    [Fact]
    public void Migration019AddsOnlyConfigurationAndAdmissionStorage()
    {
        var before = SqlRepositorySchemaDescriptors.Post018;
        var after = SqlRepositorySchemaDescriptors.Post019;
        Assert.Equal(46, after.Tables.Length);
        Assert.Equal(before.Tables, after.Tables.Take(before.Tables.Length));
        Assert.Equal(ExpectedTables,
            after.Tables.Skip(before.Tables.Length).Select(x => x.Name.ObjectName));
        Assert.Equal("ImmutableAssumedBreakAuthority", SqlMigrationCatalog.Load().Migrations.Single(x=>x.MigrationId==19).Name);
        Assert.Same(SqlRepositorySchemaDescriptors.Post022, SqlRepositorySchemaDescriptors.Current);
    }
}
